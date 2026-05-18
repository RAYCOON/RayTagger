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
    /// when lookups are disabled, when the query has no usable signals, or when every provider
    /// returned nothing — never throws on transport failures.
    /// </summary>
    Task<LookupResult> RunAsync(LookupQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pass-through runner — always returns <see cref="LookupResult.Empty"/>. Used for offline-only
/// runs, tests, and `--no-lookup` flows.
/// </summary>
public sealed class NoopLookupRunner : ILookupRunner
{
    public static NoopLookupRunner Instance { get; } = new();

    public Task<LookupResult> RunAsync(LookupQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(LookupResult.Empty);
}
