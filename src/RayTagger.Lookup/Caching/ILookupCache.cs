using RayTagger.Core.Models;

namespace RayTagger.Lookup.Caching;

/// <summary>
/// Persistence layer for aggregated lookup results. Keyed by a stable hash derived from
/// fingerprint (preferred) or normalised <c>artist|title|album</c> — see <see cref="LookupCacheKey"/>.
/// </summary>
public interface ILookupCache
{
    Task<LookupResult?> GetAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task SetAsync(string key, LookupResult result, CancellationToken cancellationToken = default);
}
