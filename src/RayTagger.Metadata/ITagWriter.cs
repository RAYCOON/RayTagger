using RayTagger.Core.Models;

namespace RayTagger.Metadata;

/// <summary>
/// Writes resolved tag values back to an audio file. Implementations handle:
/// <list type="bullet">
///   <item>Per-format frame mapping (MP3/AIFF via ID3v2, FLAC via Vorbis comments).</item>
///   <item>Optional sidecar backup before any change (so writes are reversible).</item>
///   <item>Dry-run mode (computes what would change, makes no IO).</item>
///   <item>Source-aware skipping: fields with <see cref="TagFieldSource.Existing"/> are not re-written.</item>
/// </list>
/// </summary>
public interface ITagWriter
{
    /// <summary>
    /// Writes <paramref name="resolved"/> to the file at <paramref name="path"/>, returning a
    /// description of what changed (or would have, in dry-run).
    /// </summary>
    /// <exception cref="MetadataException">
    /// Thrown if the file is missing, unreadable, locked by another process, or write fails.
    /// </exception>
    TagWriteResult Write(string path, ResolvedTrackTags resolved, TagWriteOptions options);
}

/// <summary>Options for a single write operation. Decoupled from the global <c>WriteOptions</c> POCO.</summary>
public sealed record TagWriteOptions(bool DryRun, bool Backup, string? BackupDirectory = null);

/// <summary>Result of a write operation. Always returned, even on dry-run.</summary>
public sealed record TagWriteResult(
    string FilePath,
    bool DryRun,
    IReadOnlyList<string> WrittenFields,
    string? BackupPath);
