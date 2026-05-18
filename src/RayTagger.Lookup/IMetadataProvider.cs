using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Lookup;

/// <summary>
/// Online metadata source — wraps one HTTP API (AcoustID, MusicBrainz, Discogs, Last.fm). The
/// runner orchestrates a chain of providers in user-configured order and merges their outputs.
/// </summary>
/// <remarks>
/// Per-provider failure must NOT propagate to the runner — return <see cref="LookupResult.Empty"/>
/// (or <c>null</c>) and log instead. The pipeline runs offline-first; a failing provider is just
/// "no enrichment from this source".
/// </remarks>
public interface IMetadataProvider
{
    /// <summary>Stable, lowercase identifier used in config (<c>lookup.providers</c>) and logs.</summary>
    string Name { get; }

    /// <summary>Whether this provider can answer the given query at all (e.g. AcoustID needs a fingerprint).</summary>
    bool CanHandle(LookupQuery query);

    /// <summary>
    /// Issues the HTTP call (or returns the cached response). Returns <c>null</c> when the provider
    /// has nothing to add — never throws on transport failures, only on programmer errors.
    /// </summary>
    Task<LookupResult?> LookupAsync(LookupQuery query, CancellationToken cancellationToken = default);
}
