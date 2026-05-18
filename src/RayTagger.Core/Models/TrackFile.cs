namespace RayTagger.Core.Models;

/// <summary>
/// Filesystem-level identity of an audio file. <see cref="LastModifiedUtc"/> is used for cache
/// invalidation; we deliberately avoid a content hash because computing one for every track in a
/// large library would force a full file read at discovery time. See docs/ARCHITECTURE.md §2.
/// </summary>
public sealed record TrackFile(
    string Path,
    AudioFormat Format,
    long SizeBytes,
    DateTime LastModifiedUtc);
