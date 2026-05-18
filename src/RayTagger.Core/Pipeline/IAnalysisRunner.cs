using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Runs the configured analyzers for a single track and aggregates their results. Implementations
/// catch per-analyzer failures (e.g. binary not on PATH) and return a degraded
/// <see cref="AnalysisResult"/> rather than throwing — that way one bad analyzer doesn't blow up
/// the whole pipeline.
/// </summary>
public interface IAnalysisRunner
{
    Task<AnalysisResult> RunAsync(TrackFile file, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pass-through runner — emits <see cref="AnalysisResult.Empty"/> for every file. Useful for
/// dry-run scenarios, tests, and the "lookup-only" workflows that don't need audio analysis.
/// </summary>
public sealed class NoopAnalysisRunner : IAnalysisRunner
{
    public static NoopAnalysisRunner Instance { get; } = new();

    public Task<AnalysisResult> RunAsync(TrackFile file, CancellationToken cancellationToken = default) =>
        Task.FromResult(AnalysisResult.Empty);
}
