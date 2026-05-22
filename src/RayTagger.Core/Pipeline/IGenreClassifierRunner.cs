using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Runs every enabled audio-based genre classifier for one track and merges their outputs into
/// a single ranked candidate list. Mirrors <see cref="ILookupRunner"/>'s shape (fail-open
/// contract, separate trace for the UI, NoOp variant for the all-disabled case). The candidates
/// are appended to <see cref="LookupResult.GenreCandidates"/> by <c>TagPipeline</c> before the
/// merger runs — the taxonomy resolver then treats classifier hits exactly like provider hits.
/// See <c>docs/PLAN_GENRE_CLASSIFICATION.md §3.7</c>.
/// </summary>
public interface IGenreClassifierRunner
{
    /// <summary>
    /// Runs every enabled classifier. Returns <see cref="GenreClassifierRunResult.Empty"/> when no
    /// classifier is configured, or when none produced a candidate. Per-classifier failures are
    /// isolated in the trace; one failed classifier doesn't kill the others.
    /// </summary>
    Task<GenreClassifierRunResult> RunAsync(
        TrackFile file,
        AnalysisResult analysis,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregated output of <see cref="IGenreClassifierRunner"/>: the candidate stream that gets
/// appended to <c>LookupResult.GenreCandidates</c>, plus a per-classifier trace for the UI's
/// diagnostics popup. The trace is intentionally separate so it never makes its way into the
/// disk-backed lookup cache (latency/status data would go stale immediately).
/// </summary>
public sealed record GenreClassifierRunResult(
    IReadOnlyList<GenreCandidate> Candidates,
    IReadOnlyList<ClassifierTraceEntry> Trace)
{
    public static GenreClassifierRunResult Empty { get; } = new([], []);
}

/// <summary>
/// One classifier's contribution to a track's classification run. Captures everything the
/// "Klassifikator-Vorschläge" diagnostic surface needs without dragging the actual
/// <c>EssentiaResult</c> into the trace.
/// </summary>
public sealed record ClassifierTraceEntry(
    string ClassifierName,
    ClassifierTraceStatus Status,
    IReadOnlyList<GenreCandidate> Candidates,
    string? ErrorMessage,
    double DurationMs);

public enum ClassifierTraceStatus
{
    /// <summary>The classifier ran and produced at least one candidate.</summary>
    Ok,

    /// <summary>The classifier ran successfully but produced no candidate (e.g. all scores below the floor).</summary>
    NoHit,

    /// <summary>The classifier was instantiated but skipped this track (e.g. dependency missing).</summary>
    Disabled,

    /// <summary>The classifier threw — captured in <see cref="ClassifierTraceEntry.ErrorMessage"/>.</summary>
    Failed,
}

/// <summary>
/// Pass-through runner — always returns <see cref="GenreClassifierRunResult.Empty"/>. Registered
/// as the default <see cref="IGenreClassifierRunner"/> so the pipeline is byte-identical to
/// the pre-classifier behaviour when every flag is off.
/// </summary>
public sealed class NoopGenreClassifierRunner : IGenreClassifierRunner
{
    public static NoopGenreClassifierRunner Instance { get; } = new();

    public Task<GenreClassifierRunResult> RunAsync(
        TrackFile file, AnalysisResult analysis, CancellationToken cancellationToken = default) =>
        Task.FromResult(GenreClassifierRunResult.Empty);
}
