namespace RayTagger.Core.Pipeline;

/// <summary>
/// Inputs an online provider needs to look up metadata for one track. Not all providers use all
/// fields: AcoustID wants <see cref="Fingerprint"/> + <see cref="DurationSeconds"/>; MusicBrainz
/// can use an MBID-anchored lookup if AcoustID already produced one, otherwise falls back to
/// artist+title; Discogs and Last.fm are text-only.
/// </summary>
/// <remarks>
/// The query is intentionally a snapshot — provider chaining works by re-issuing a richer query
/// (e.g. with an MBID filled in) rather than mutating an in-flight instance.
/// </remarks>
public sealed record LookupQuery
{
    public string? Artist { get; init; }
    public string? Title { get; init; }
    public string? Album { get; init; }
    public int? DurationSeconds { get; init; }
    public string? Fingerprint { get; init; }
    public Guid? ReleaseMbid { get; init; }
    public Guid? RecordingMbid { get; init; }

    /// <summary>True when at least one provider could conceivably answer this query.</summary>
    public bool HasAnySignal =>
        !string.IsNullOrWhiteSpace(Fingerprint)
        || (!string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Title))
        || ReleaseMbid is not null
        || RecordingMbid is not null;
}
