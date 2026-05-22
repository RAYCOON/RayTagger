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

    public async Task<LookupRunResult> RunAsync(LookupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_enabled || _orderedProviders.Count == 0 || !query.HasAnySignal)
        {
            return LookupRunResult.Empty;
        }

        var cacheKey = LookupCacheKey.From(query);
        if (_cache is not null)
        {
            var cached = await _cache.GetAsync(cacheKey, _cacheTtl, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _logger.LogDebug("Lookup cache hit {Key}", cacheKey);
                // Cache hit → no provider was actually called this run, so the trace is empty.
                // The UI tooltip will show "(aus Cache)" elsewhere once we surface this state.
                return new LookupRunResult(cached, []);
            }
        }

        var currentQuery = query;
        var allGenres = new List<GenreCandidate>();
        var allSubGenres = new List<GenreCandidate>();
        Guid? releaseMbid = query.ReleaseMbid;
        Guid? recordingMbid = query.RecordingMbid;
        var trace = new List<ProviderTraceEntry>(_orderedProviders.Count);

        foreach (var provider in _orderedProviders)
        {
            if (!provider.CanHandle(currentQuery))
            {
                trace.Add(new ProviderTraceEntry(
                    provider.Name, ProviderTraceStatus.Skipped, [], [], null, null,
                    ErrorMessage: null, DurationMs: 0));
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            LookupResult? providerResult;
            try
            {
                providerResult = await provider.LookupAsync(currentQuery, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Provider implementations promise not to throw on transport errors. If one does
                // anyway, isolate the failure here — the rest of the chain still runs.
                sw.Stop();
                _logger.LogWarning(ex, "Provider {Name} threw unexpectedly: {Message}", provider.Name, ex.Message);
                trace.Add(new ProviderTraceEntry(
                    provider.Name, ProviderTraceStatus.Failed, [], [], null, null,
                    ErrorMessage: ex.Message, DurationMs: sw.Elapsed.TotalMilliseconds));
                continue;
            }
            sw.Stop();

            if (providerResult is null)
            {
                trace.Add(new ProviderTraceEntry(
                    provider.Name, ProviderTraceStatus.NoHit, [], [], null, null,
                    ErrorMessage: null, DurationMs: sw.Elapsed.TotalMilliseconds));
                continue;
            }

            allGenres.AddRange(providerResult.GenreCandidates);
            allSubGenres.AddRange(providerResult.SubGenreCandidates);
            releaseMbid ??= providerResult.MbReleaseId;
            recordingMbid ??= providerResult.MbRecordingId;

            var hasAnyData =
                providerResult.GenreCandidates.Count > 0
                || providerResult.SubGenreCandidates.Count > 0
                || providerResult.MbRecordingId is not null
                || providerResult.MbReleaseId is not null;
            trace.Add(new ProviderTraceEntry(
                provider.Name,
                hasAnyData ? ProviderTraceStatus.Ok : ProviderTraceStatus.NoHit,
                Genres: providerResult.GenreCandidates.Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList(),
                Subgenres: providerResult.SubGenreCandidates.Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList(),
                MbRecordingId: providerResult.MbRecordingId,
                MbReleaseId: providerResult.MbReleaseId,
                ErrorMessage: null,
                DurationMs: sw.Elapsed.TotalMilliseconds));

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

        // Don't cache a result that carries no useful signal — that would freeze a transient
        // "no hit" (MB API was slow, our query was off, fingerprint was missing) into a 30-day
        // empty answer. A subsequent button click should be free to re-query. MBIDs alone count
        // as a useful signal because they enable a higher-precision MB lookup next time.
        var hasUsefulSignal =
            aggregate.GenreCandidates.Count > 0
            || aggregate.SubGenreCandidates.Count > 0
            || aggregate.MbReleaseId is not null
            || aggregate.MbRecordingId is not null;

        if (_cache is not null && hasUsefulSignal)
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

        return new LookupRunResult(aggregate, trace);
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
