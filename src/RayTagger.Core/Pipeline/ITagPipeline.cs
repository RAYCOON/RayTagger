using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Orchestrates a full scan run. Yields outcomes lazily so callers can stream progress to a UI.
/// Phase 1b implementation is intentionally simple: discover → read → (placeholder analyse/lookup/map) →
/// optional write. Real analyzers and rule evaluation arrive in Phase 2/3 — when they do, this
/// interface stays stable.
/// </summary>
public interface ITagPipeline
{
    IAsyncEnumerable<PipelineOutcome> RunAsync(
        TaggerOptions options,
        MappingRuleSet rules,
        Func<TrackFile, ValueTask>? onFileStarted = null,
        CancellationToken cancellationToken = default);
}
