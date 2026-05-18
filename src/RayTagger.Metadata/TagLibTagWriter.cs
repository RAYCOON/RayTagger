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

        // Backup before any mutation.
        string? backupPath = null;
        if (options.Backup)
        {
            var existing = _reader.Read(path);
            backupPath = _backupWriter.Write(path, existing, options.BackupDirectory);
        }

        TagLib.File tagFile;
        try
        {
            tagFile = TagLib.File.Create(path);
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            throw new MetadataException($"Cannot open file for writing: {path}: {ex.Message}", path, ex);
        }

        try
        {
            ApplyChanges(tagFile, resolved, fieldsToWrite);
            tagFile.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MetadataException($"Failed to save tags to {path}: {ex.Message}", path, ex);
        }
        finally
        {
            tagFile.Dispose();
        }

        return new TagWriteResult(path, DryRun: false, fieldsToWrite, backupPath);
    }

    /// <summary>
    /// Returns the logical-field names that need writing — i.e. those whose <see cref="TagFieldSource"/>
    /// is anything other than <see cref="TagFieldSource.Existing"/>. The pipeline merge step has
    /// already applied the policy at this point, so we trust the sources. Custom-field names are
    /// prefixed with <c>"tag."</c> to disambiguate from the named logical slots in the dispatch.
    /// </summary>
    private static List<string> ResolveFieldsToWrite(ResolvedTrackTags resolved)
    {
        var list = new List<string>(8 + resolved.Custom.Count);
        if (resolved.Genre.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Genre));
        if (resolved.SubGenre.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.SubGenre));
        if (resolved.Bpm.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Bpm));
        if (resolved.Key.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Key));
        if (resolved.Energy.Source != TagFieldSource.Existing) list.Add(nameof(TrackTags.Energy));
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

    private static void ApplyChanges(TagLib.File file, ResolvedTrackTags resolved, IReadOnlyList<string> fields)
    {
        foreach (var field in fields)
        {
            switch (field)
            {
                case nameof(TrackTags.Genre):
                    FrameMapper.WriteGenre(file, resolved.Genre.Value);
                    break;
                case nameof(TrackTags.SubGenre):
                    FrameMapper.WriteSubGenre(file, resolved.SubGenre.Value);
                    break;
                case nameof(TrackTags.Bpm):
                    FrameMapper.WriteBpm(file, resolved.Bpm.Value);
                    break;
                case nameof(TrackTags.Key):
                    FrameMapper.WriteStandardKey(file, resolved.Key.Value?.Standard);
                    FrameMapper.WriteCamelotKey(file, resolved.Key.Value?.Camelot);
                    break;
                case nameof(TrackTags.Energy):
                    FrameMapper.WriteEnergy(file, resolved.Energy.Value);
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
