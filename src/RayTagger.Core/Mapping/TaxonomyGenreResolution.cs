using RayTagger.Core.Models;

namespace RayTagger.Core.Mapping;

/// <summary>
/// Output of <see cref="TaxonomyGenreResolver.Resolve"/>. Carries both the values the caller
/// should write (<c>ProposedGenre</c>/<c>ProposedSubgenre</c>, already filtered by the
/// existing-tag-protection rule) and the raw match (<c>ChosenGenre</c>/<c>ChosenSubgenre</c>)
/// for trace / UI display.
/// </summary>
/// <remarks>
/// <c>MatchedCandidate</c> is the source candidate for the applied value: the candidate whose
/// value matched a taxonomy genre (standard path), or — when <c>FallbackApplied</c> is true —
/// the top-1 candidate that supplied the raw fallback value. Null only when neither path
/// produced a value (no candidates at all, or all rejected + existing kept).
/// </remarks>
public sealed record TaxonomyGenreResolution(
    string? ProposedGenre,
    string? ProposedSubgenre,
    string? ChosenGenre,
    string? ChosenSubgenre,
    bool FallbackApplied,
    GenreCandidate? MatchedCandidate,
    IReadOnlyList<CandidateTraceEntry> Trace)
{
    /// <summary>Sentinel for "no useful input" — empty candidate list and empty trace.</summary>
    public static TaxonomyGenreResolution Empty { get; } =
        new(null, null, null, null, false, null, []);
}

/// <summary>
/// One row in the resolver's audit trail — captures what each inspected API candidate matched
/// against the taxonomy and which value the resolver chose from it. The UI's "Regeln"-Popup
/// shows this so the user can debug why a track got (or didn't get) a particular genre.
/// </summary>
public sealed record CandidateTraceEntry(
    string ApiValue,
    string ApiSource,
    double Confidence,
    IReadOnlyList<string> MatchedGenres,
    string? ChosenGenre,
    IReadOnlyList<string> MatchedSubgenres,
    string? ChosenSubgenre);
