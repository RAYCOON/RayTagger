using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Metadata.Internal;
using FsFile = System.IO.File;

namespace RayTagger.Metadata;

/// <summary>
/// Default <see cref="ITagWriter"/> implementation backed by TagLib#. Stateless; thread-safe per
/// call (each invocation opens its own <c>TagLib.File</c>).
/// </summary>
public sealed class TagLibTagWriter : ITagWriter
{
    private readonly ITagReader _reader;
    private readonly BackupSidecarWriter _backupWriter;

    public TagLibTagWriter(ITagReader reader, BackupSidecarWriter backupWriter)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(backupWriter);

        _reader = reader;
        _backupWriter = backupWriter;
    }

    public TagWriteResult Write(string path, ResolvedTrackTags resolved, TagWriteOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(options);

        if (!FsFile.Exists(path))
        {
            throw new MetadataException($"File not found: {path}", path);
        }

        // Compute the diff first so we know whether any work is needed (and so dry-run reports
        // accurately without ever touching the file).
        var fieldsToWrite = ResolveFieldsToWrite(resolved);
        if (fieldsToWrite.Count == 0)
        {
            return new TagWriteResult(path, options.DryRun, [], BackupPath: null);
        }

        if (options.DryRun)
        {
            return new TagWriteResult(path, DryRun: true, fieldsToWrite, BackupPath: null);
        }

        // Pre-flight: refuse if the audio file is held open by another process. ARCHITECTURE.md
        // §6.3 promises a graceful refusal rather than letting TagLib# throw an IOException deep
        // inside the save. Open + dispose with FileShare.None — if another process has it locked,
        // this fails fast with a clean message and we never touch the file.
        EnsureFileIsWritable(path);

        // Backup before any mutation.
        string? backupPath = null;
        if (options.Backup)
        {
            var existing = _reader.Read(path);
            backupPath = _backupWriter.Write(path, existing, options.BackupDirectory);
        }

        // Atomic write: TagLib# mutates the file in place, which leaves a half-written audio
        // file if the process dies during Save(). We copy to a temp sibling, let TagLib# rewrite
        // it there, then atomic-rename over the original. On macOS/Linux File.Move with
        // overwrite is rename(2) — atomic. On Windows File.Move uses MoveFileEx with
        // MOVEFILE_REPLACE_EXISTING, also atomic on the same volume.
        //
        // TagLib.File.Create dispatches on the file extension (no magic-byte sniff in this code
        // path), so the temp file must keep the original suffix — otherwise `.tagger.tmp` hits
        // UnsupportedFormatException and the write blows up.
        var tempPath = path + ".tagger.tmp" + Path.GetExtension(path);
        try
        {
            FsFile.Copy(path, tempPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MetadataException($"Failed to stage temp copy of {path}: {ex.Message}", path, ex);
        }

        TagLib.File tagFile;
        try
        {
            tagFile = TagLib.File.Create(tempPath);
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            TryDelete(tempPath);
            throw new MetadataException($"Cannot open file for writing: {path}: {ex.Message}", path, ex);
        }

        try
        {
            ApplyChanges(tagFile, resolved, fieldsToWrite, options.TagFieldMap ?? TagFieldMap.Default);
            tagFile.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tagFile.Dispose();
            TryDelete(tempPath);
            throw new MetadataException($"Failed to save tags to {path}: {ex.Message}", path, ex);
        }
        finally
        {
            tagFile.Dispose();
        }

        try
        {
            FsFile.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new MetadataException($"Failed to commit tag changes to {path}: {ex.Message}", path, ex);
        }

        return new TagWriteResult(path, DryRun: false, fieldsToWrite, backupPath);
    }

    /// <summary>
    /// Probes the file with <see cref="FileShare.None"/>. Throws a structured
    /// <see cref="MetadataException"/> if another process holds an exclusive or shared lock —
    /// caller maps it to a per-file pipeline error. Disposes the handle immediately on success
    /// so we don't hold the lock ourselves into the actual write.
    /// </summary>
    private static void EnsureFileIsWritable(string path)
    {
        try
        {
            using var probe = FsFile.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new MetadataException(
                $"Cannot write to {path}: file is currently held open by another process.",
                path, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MetadataException(
                $"Cannot write to {path}: permission denied.",
                path, ex);
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        // ERROR_SHARING_VIOLATION on Windows = 0x80070020; on POSIX an EBUSY-style error doesn't
        // collide with normal "not found" / "out of space" cases. Use HResult low byte to detect
        // the sharing case portably; fall back to message inspection for cross-platform parity.
        var lowByte = ex.HResult & 0xFFFF;
        return lowByte == 32
            || ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Resource busy", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (FsFile.Exists(path))
            {
                FsFile.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort: a leftover .tagger.tmp gets overwritten on the next write attempt.
        }
        catch (UnauthorizedAccessException)
        {
            // Same — informational, not actionable here.
        }
    }

    /// <summary>
    /// Returns the logical-field names that need writing — i.e. those whose <see cref="TagFieldSource"/>
    /// is anything other than <see cref="TagFieldSource.Existing"/>. The pipeline merge step has
    /// already applied the policy at this point, so we trust the sources. Custom-field names are
    /// prefixed with <c>"tag."</c> to disambiguate from the named logical slots in the dispatch.
    /// </summary>
    private static List<string> ResolveFieldsToWrite(ResolvedTrackTags resolved)
    {
        var list = new List<string>(10 + resolved.Custom.Count);
        if (resolved.Genre.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Genre));
        if (resolved.SubGenre.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.SubGenre));
        if (resolved.Bpm.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Bpm));
        if (resolved.Key.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Key));
        if (resolved.Energy.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Energy));
        if (resolved.Mood.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Mood));
        if (resolved.SetPosition.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.SetPosition));
        // Custom fields touched by mapping rules (TagFieldSource.Rules) need to be written too —
        // a `set: { tag.mood: "Driving" }` rule that ran in the engine but never produced a
        // TXXX:MOOD frame would silently lose the user's declarative intent.
        foreach (var (name, field) in resolved.Custom)
        {
            if (field.Source != TagFieldSource.Existing)
            {
                list.Add("tag." + name);
            }
        }
        return list;
    }

    private static void ApplyChanges(
        TagLib.File file,
        ResolvedTrackTags resolved,
        IReadOnlyList<string> fields,
        TagFieldMap fieldMap)
    {
        foreach (var field in fields)
        {
            switch (field)
            {
                case nameof(TrackTags.Genre):
                    FrameMapper.WriteGenre(file, resolved.Genre.Value);
                    break;
                case nameof(TrackTags.SubGenre):
                    FrameMapper.WriteSubGenre(file, resolved.SubGenre.Value, fieldMap);
                    break;
                case nameof(TrackTags.Bpm):
                    FrameMapper.WriteBpm(file, resolved.Bpm.Value);
                    break;
                case nameof(TrackTags.Key):
                    FrameMapper.WriteStandardKey(file, resolved.Key.Value?.Standard);
                    FrameMapper.WriteCamelotKey(file, resolved.Key.Value?.Camelot, fieldMap);
                    break;
                case nameof(TrackTags.Energy):
                    FrameMapper.WriteEnergy(file, resolved.Energy.Value, fieldMap);
                    break;
                case nameof(TrackTags.Mood):
                    FrameMapper.WriteMood(file, resolved.Mood.Value, fieldMap);
                    break;
                case nameof(TrackTags.SetPosition):
                    FrameMapper.WriteSetPosition(file, resolved.SetPosition.Value, fieldMap);
                    break;
                default:
                    if (field.StartsWith("tag.", StringComparison.Ordinal))
                    {
                        var customName = field[4..];
                        if (resolved.Custom.TryGetValue(customName, out var customField))
                        {
                            FrameMapper.WriteCustomField(file, customName, customField.Value);
                        }
                    }
                    break;
            }
        }
    }
}
