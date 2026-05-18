namespace RayTagger.Core.Models;

/// <summary>
/// A genre or sub-genre proposal from one online provider. <see cref="Source"/> is the provider
/// name (e.g. "musicbrainz", "discogs") so future mapping rules can prefer one provider over
/// another.
/// </summary>
public sealed record GenreCandidate(string Value, double Confidence, string Source);

/// <summary>
/// Aggregated, ranked output of all configured online providers for one track. Candidates are
/// pre-sorted by descending confidence. <see cref="MbReleaseId"/>/<see cref="MbRecordingId"/> are
/// retained for the optional "tagger explain" debug verb.
/// </summary>
public sealed record LookupResult(
    IReadOnlyList<GenreCandidate> GenreCandidates,
    IReadOnlyList<GenreCandidate> SubGenreCandidates,
    Guid? MbReleaseId = null,
    Guid? MbRecordingId = null)
{
    public static LookupResult Empty { get; } = new([], []);
}
