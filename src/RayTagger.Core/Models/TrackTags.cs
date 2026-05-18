namespace RayTagger.Core.Models;

/// <summary>
/// Snapshot of tag values for a single track — either as read from disk or as a target write
/// payload. Logical fields are nullable so the absence of a tag is distinguishable from an
/// empty-string tag. <see cref="Custom"/> carries any extra tag frames we don't have a logical
/// slot for, so round-trip read/write preserves them.
/// </summary>
public sealed record TrackTags(
    string? Title = null,
    string? Artist = null,
    string? AlbumArtist = null,
    string? Album = null,
    int? Year = null,
    string? Genre = null,
    string? SubGenre = null,
    double? Bpm = null,
    MusicalKey? Key = null,
    int? Energy = null,
    int? DurationSeconds = null,
    IReadOnlyDictionary<string, string>? Custom = null)
{
    public IReadOnlyDictionary<string, string> Custom { get; init; } =
        Custom ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static TrackTags Empty { get; } = new();
}
