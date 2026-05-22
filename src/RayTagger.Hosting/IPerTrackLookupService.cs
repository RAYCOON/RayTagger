using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Hosting;

/// <summary>
/// On-demand single-track lookup runner. Powers the UI's per-row "API" button. Separate from
/// the bulk-scan pipeline (which runs all providers + all analyzers per track) so it can fire
/// without a full pipeline build and without disturbing an in-flight scan.
/// </summary>
public interface IPerTrackLookupService
{
    /// <summary>
    /// Quick gate for the UI: is the per-track lookup even possible with the current options?
    /// Returns true when at least one provider in <c>options.Lookup.Providers</c> can fire —
    /// MusicBrainz always qualifies, the others need their API key set. Ignores
    /// <c>options.Lookup.Enabled</c> (that flag only gates the automatic scan-time lookup).
    /// </summary>
    bool IsAvailable(TaggerOptions options);

    /// <summary>
    /// Runs the lookup + taxonomy resolution for one track. Result describes what would be
    /// proposed (genre / sub-genre) plus an audit trail for the UI's "Regeln"-popup.
    /// </summary>
    /// <remarks>
    /// <c>filePath</c>: optional absolute path. When supplied, the service shells out to
    /// <c>fpcalc</c> to compute a Chromaprint fingerprint before the lookup, which enables
    /// AcoustID (and the AcoustID→MusicBrainz MBID handshake). Null skips fingerprinting — the
    /// other providers still work on artist+title.
    /// </remarks>
    Task<PerTrackLookupResult> ResolveAsync(
        TaggerOptions options,
        TrackTags existing,
        string? filePath = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Output of <see cref="IPerTrackLookupService.ResolveAsync"/>. Mirrors
/// <see cref="TaxonomyGenreResolution"/> plus an error slot for transport failures so the UI
/// can show a useful tooltip instead of just "nothing happened".
/// </summary>
public sealed record PerTrackLookupResult(
    string? ProposedGenre,
    string? ProposedSubgenre,
    string? ChosenGenre,
    string? ChosenSubgenre,
    bool FallbackApplied,
    GenreCandidate? MatchedCandidate,
    IReadOnlyList<CandidateTraceEntry> Trace,
    string? ErrorMessage)
{
    /// <summary>
    /// True when at least one provider found the track in its database — even if that hit had no
    /// genre/tag annotations. Used by the UI to distinguish "MB doesn't know this track" from
    /// "MB knows it but its community hasn't tagged it yet".
    /// </summary>
    public bool ApiFoundTrack { get; init; }

    /// <summary>
    /// Number of raw genre/sub-genre candidates returned by the API (across providers, before
    /// taxonomy filtering). Drives the tooltip wording when no taxonomy match was found.
    /// </summary>
    public int RawCandidateCount { get; init; }

    /// <summary>
    /// Per-provider trace from the lookup runner (acoustid / musicbrainz / discogs / lastfm)
    /// — what each one returned, how long it took, and any error message. Surfaced in the UI's
    /// "Regeln"-popup so the user can debug "which API gave me what" without reading log files.
    /// </summary>
    public IReadOnlyList<ProviderTraceEntry>? ProviderTrace { get; init; }

    /// <summary>Returned when the track has no usable signal (no artist/title and no fingerprint).</summary>
    public static PerTrackLookupResult NoSignal { get; } =
        new(null, null, null, null, false, null, [], "Kein Lookup möglich — Track hat weder Artist+Title noch Fingerprint.");

    /// <summary>Returned on transport / parser failure. Message comes from the underlying exception.</summary>
    public static PerTrackLookupResult Failed(string error) =>
        new(null, null, null, null, false, null, [], error);
}
