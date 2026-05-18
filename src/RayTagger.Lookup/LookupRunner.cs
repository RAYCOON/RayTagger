using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup.Caching;

namespace RayTagger.Lookup;

/// <summary>
/// Default <see cref="ILookupRunner"/>. Runs the configured providers in declared order, threads
/// AcoustID's MBID forward into MusicBrainz's MBID-anchored query, and merges every provider's
/// <see cref="GenreCandidate"/> list into a single ranked aggregate. Caches the aggregate per
/// track so a re-scan doesn't hammer the upstream APIs.
/// </summary>
/// <remarks>
/// Fail-open is the contract: an offline scan or a single provider's HTTP failure must produce
/// <see cref="LookupResult.Empty"/>, never an exception. The pipeline's design assumes online
/// enrichment is optional.
/// </remarks>
public sealed class LookupRunner : ILookupRunner
{
    private readonly List<IMetadataProvider> _orderedProviders;
    private readonly ILookupCache? _cache;
    private readonly TimeSpan _cacheTtl;
    private readonly bool _enabled;
    private readonly ILogger<LookupRunner> _logger;

    public LookupRunner(
        IEnumerable<IMetadataProvider> providers,
        LookupOptions options,
        ILookupCache? cache,
        ILogger<LookupRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _enabled = options.Enabled;
        _cache = options.Cache.Enabled ? cache : null;
        _cacheTtl = TimeSpan.FromDays(Math.Max(1, options.Cache.TtlDays));
        _logger = logger;

        // Filter + order the registered providers according to `lookup.providers` in config. An
        // unknown name in config logs a warning but doesn't fail the scan — the user might have
        // added a provider name in anticipation of a future build.
        var byName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IMetadataProvider>(options.Providers.Count);
        foreach (var name in options.Providers)
        {
            if (byName.TryGetValue(name, out var provider))
            {
                ordered.Add(provider);
            }
            else
            {
                logger.LogWarning("Lookup provider '{Name}' configured but not registered.", name);
            }
        }
        _orderedProviders = ordered;
    }

    public async Task<LookupResult> RunAsync(LookupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_enabled || _orderedProviders.Count == 0 || !query.HasAnySignal)
        {
            return LookupResult.Empty;
        }

        var cacheKey = LookupCacheKey.From(query);
        if (_cache is not null)
        {
            var cached = await _cache.GetAsync(cacheKey, _cacheTtl, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _logger.LogDebug("Lookup cache hit {Key}", cacheKey);
                return cached;
            }
        }

        var currentQuery = query;
        var allGenres = new List<GenreCandidate>();
        var allSubGenres = new List<GenreCandidate>();
        Guid? releaseMbid = query.ReleaseMbid;
        Guid? recordingMbid = query.RecordingMbid;

        foreach (var provider in _orderedProviders)
        {
            if (!provider.CanHandle(currentQuery)) continue;

            LookupResult? providerResult;
            try
            {
                providerResult = await provider.LookupAsync(currentQuery, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Provider implementations promise not to throw on transport errors. If one does
                // anyway, isolate the failure here — the rest of the chain still runs.
                _logger.LogWarning(ex, "Provider {Name} threw unexpectedly: {Message}", provider.Name, ex.Message);
                continue;
            }
            if (providerResult is null) continue;

            allGenres.AddRange(providerResult.GenreCandidates);
            allSubGenres.AddRange(providerResult.SubGenreCandidates);
            releaseMbid ??= providerResult.MbReleaseId;
            recordingMbid ??= providerResult.MbRecordingId;

            // Propagate any newly discovered MBID forward so the next provider can hit the
            // higher-precision MBID path (this is the AcoustID → MusicBrainz handshake).
            currentQuery = currentQuery with
            {
                ReleaseMbid = releaseMbid,
                RecordingMbid = recordingMbid,
            };
        }

        var aggregate = new LookupResult(
            GenreCandidates: MergeRanked(allGenres),
            SubGenreCandidates: MergeRanked(allSubGenres),
            MbReleaseId: releaseMbid,
            MbRecordingId: recordingMbid);

        if (_cache is not null)
        {
            try
            {
                await _cache.SetAsync(cacheKey, aggregate, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to write lookup cache {Key}: {Message}", cacheKey, ex.Message);
            }
        }

        return aggregate;
    }

    private static List<GenreCandidate> MergeRanked(IEnumerable<GenreCandidate> candidates)
    {
        var byValue = new Dictionary<string, GenreCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (byValue.TryGetValue(c.Value, out var existing))
            {
                if (c.Confidence > existing.Confidence) byValue[c.Value] = c;
            }
            else
            {
                byValue[c.Value] = c;
            }
        }
        return byValue.Values.OrderByDescending(c => c.Confidence).ToList();
    }
}
