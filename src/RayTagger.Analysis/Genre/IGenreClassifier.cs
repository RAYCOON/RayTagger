using RayTagger.Core.Models;

namespace RayTagger.Analysis.Genre;

/// <summary>
/// Optional audio-based genre classifier. Output is appended to the candidate stream the
/// taxonomy resolver consumes — <see cref="GenreCandidate.Source"/> is <c>classifier:&lt;name&gt;</c>
/// so the trace shows it distinctly from provider-sourced candidates. See
/// <c>docs/PLAN_GENRE_CLASSIFICATION.md §3.3</c>.
/// </summary>
public interface IGenreClassifier
{
    /// <summary>Display name used in <see cref="GenreCandidate.Source"/> and status logs (e.g. "heuristic").</summary>
    string Name { get; }

    /// <summary>
    /// Classifies one track. Returns <see cref="GenreClassificationResult.Empty"/> when the
    /// classifier cannot run for this track (silent failure mode — pipeline continues). Never
    /// throws for per-track issues; only <see cref="OperationCanceledException"/> may surface.
    /// </summary>
    Task<GenreClassificationResult> ClassifyAsync(TrackFile file, CancellationToken cancellationToken);
}

/// <summary>
/// Output of a single <see cref="IGenreClassifier"/>. The candidates list is already sorted by
/// descending confidence and pre-normalised via <see cref="ClassifierLabelNormaliser"/>.
/// </summary>
public sealed record GenreClassificationResult(IReadOnlyList<GenreCandidate> Candidates)
{
    public static GenreClassificationResult Empty { get; } = new([]);
}
