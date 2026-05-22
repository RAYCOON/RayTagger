namespace RayTagger.Core.Models;

/// <summary>
/// One row in the per-track provider audit trail. Captures what each configured
/// <c>IMetadataProvider</c> returned (or skipped) during a single
/// <see cref="Pipeline.ILookupRunner.RunAsync"/> invocation — independent of the taxonomy
/// resolver's downstream decisions. Surfaced to the UI's "Regeln"-popup so the user can see
/// the raw API responses, not just the aggregated/filtered candidates.
/// </summary>
/// <param name="Provider">
/// Provider name as registered (<c>"acoustid"</c>, <c>"musicbrainz"</c>, <c>"discogs"</c>,
/// <c>"lastfm"</c>). Lowercase, stable identifier.
/// </param>
/// <param name="Status">See <see cref="ProviderTraceStatus"/>.</param>
/// <param name="Genres">Raw genre values as returned by the API (original case preserved).</param>
/// <param name="Subgenres">
/// Raw sub-genre values. Today only Discogs populates this slot via its <c>style</c> field.
/// </param>
/// <param name="MbRecordingId">MBID of the recording, when supplied by AcoustID or MusicBrainz.</param>
/// <param name="MbReleaseId">MBID of the release, when supplied by AcoustID or MusicBrainz.</param>
/// <param name="ErrorMessage">Free-text error description when <see cref="Status"/> is <see cref="ProviderTraceStatus.Failed"/>.</param>
/// <param name="DurationMs">
/// Wall-clock time spent in the provider's <c>LookupAsync</c>, including any internal
/// rate-limiter wait. UI displays this with the caveat that "fast" providers can still show
/// 1000+ ms when they hit their 1-req/s gate from a previous call.
/// </param>
public sealed record ProviderTraceEntry(
    string Provider,
    ProviderTraceStatus Status,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Subgenres,
    Guid? MbRecordingId,
    Guid? MbReleaseId,
    string? ErrorMessage,
    double DurationMs);

/// <summary>
/// Result classification for one provider call. Kept technical (no localisation) so log lines
/// and UI labels match across CLI and UI hosts.
/// </summary>
public enum ProviderTraceStatus
{
    /// <summary>Provider returned a usable result (genres, subgenres, or an MBID).</summary>
    Ok,

    /// <summary>Provider was queried but returned no usable data (empty result set).</summary>
    NoHit,

    /// <summary>Provider was registered but its <c>CanHandle</c> rejected the query (e.g. AcoustID without a fingerprint).</summary>
    Skipped,

    /// <summary>Provider threw a transport or parser exception. <see cref="ProviderTraceEntry.ErrorMessage"/> carries the detail.</summary>
    Failed,
}
