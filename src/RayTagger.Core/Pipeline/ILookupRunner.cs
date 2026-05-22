using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Pipeline stage entry-point for online lookups. Runs the configured provider chain, merges
/// their <see cref="LookupResult"/>s into a single ranked output, and caches the per-track result
/// on disk so a re-scan of the same library doesn't hammer the APIs.
/// </summary>
public interface ILookupRunner
{
    /// <summary>
    /// Runs the configured provider chain for one query. Returns <see cref="LookupResult.Empty"/>
    /// for the result (with a possibly populated provider trace) when lookups are disabled, when
    /// the query has no usable signals, or when every provider returned nothing — never throws
    /// on transport failures.
    /// </summary>
    /// <returns>
    /// Tuple of the aggregated <see cref="LookupResult"/> (which is the only piece that's
    /// cached) and a per-provider <see cref="ProviderTraceEntry"/> list (transient audit trail
    /// for the UI's "Regeln"-popup — never persisted).
    /// </returns>
    Task<LookupRunResult> RunAsync(LookupQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// What <see cref="ILookupRunner.RunAsync"/> hands back: the aggregated result that the
/// downstream merger consumes plus a transient per-provider trace. The trace is intentionally a
/// separate field so it doesn't get serialized into the disk-backed <c>ILookupCache</c> —
/// caching the trace would surface stale latency / status data on the next re-scan.
/// </summary>
public sealed record LookupRunResult(
    LookupResult Result,
    IReadOnlyList<ProviderTraceEntry> Trace)
{
    public static LookupRunResult Empty { get; } = new(LookupResult.Empty, []);
}

/// <summary>
/// Pass-through runner — always returns <see cref="LookupRunResult.Empty"/>. Used for
/// offline-only runs, tests, and `--no-lookup` flows.
/// </summary>
public sealed class NoopLookupRunner : ILookupRunner
{
    public static NoopLookupRunner Instance { get; } = new();

    public Task<LookupRunResult> RunAsync(LookupQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(LookupRunResult.Empty);
}
