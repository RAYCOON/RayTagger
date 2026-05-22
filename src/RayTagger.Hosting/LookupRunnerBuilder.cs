using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup;
using RayTagger.Lookup.Caching;

namespace RayTagger.Hosting;

/// <summary>
/// Builds a fully-wired <see cref="ILookupRunner"/> from a loaded <see cref="LookupOptions"/>.
/// Pulled out of <see cref="PipelineFactory"/> so the per-track UI service can share the same
/// provider-wiring code path — keeping bulk-scan and ad-hoc per-track lookups byte-identical in
/// behaviour (provider order, rate limits, cache directory).
/// </summary>
public static class LookupRunnerBuilder
{
    /// <summary>
    /// Wires the four providers (only those with an API key, plus MusicBrainz always) and the
    /// optional disk cache, then hands back an <see cref="ILookupRunner"/>. Returns
    /// <see cref="NoopLookupRunner.Instance"/> when <c>lookup.enabled = false</c>.
    /// </summary>
    public static ILookupRunner Build(
        LookupOptions lookupOptions,
        IHttpClientFactory httpClientFactory,
        IUserDataDirectoryProvider dataDirs,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter)
    {
        ArgumentNullException.ThrowIfNull(lookupOptions);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(dataDirs);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(statusReporter);

        if (!lookupOptions.Enabled)
        {
            return NoopLookupRunner.Instance;
        }

        var providers = new List<IMetadataProvider>(4);
        var rateLimits = lookupOptions.RateLimits;

        if (!string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Acoustid))
        {
            providers.Add(new AcoustIdProvider(
                httpClientFactory.CreateClient(ServiceCollectionComposer.AcoustIdHttpClient),
                lookupOptions.ApiKeys.Acoustid,
                loggerFactory.CreateLogger<AcoustIdProvider>(),
                TimeSpan.FromMilliseconds(rateLimits.AcoustidMs)));
            statusReporter.ReportLookupProvider("acoustid", available: true);
        }
        else
        {
            statusReporter.ReportLookupProvider("acoustid", available: false, detail: "no API key");
        }

        // MusicBrainz: no API key needed, only a descriptive User-Agent. Always available.
        providers.Add(new MusicBrainzProvider(
            httpClientFactory.CreateClient(ServiceCollectionComposer.MusicBrainzHttpClient),
            loggerFactory.CreateLogger<MusicBrainzProvider>(),
            TimeSpan.FromMilliseconds(rateLimits.MusicbrainzMs)));
        statusReporter.ReportLookupProvider("musicbrainz", available: true);

        if (!string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Discogs))
        {
            providers.Add(new DiscogsProvider(
                httpClientFactory.CreateClient(ServiceCollectionComposer.DiscogsHttpClient),
                lookupOptions.ApiKeys.Discogs,
                loggerFactory.CreateLogger<DiscogsProvider>(),
                TimeSpan.FromMilliseconds(rateLimits.DiscogsMs)));
            statusReporter.ReportLookupProvider("discogs", available: true);
        }
        else
        {
            statusReporter.ReportLookupProvider("discogs", available: false, detail: "no API key");
        }

        if (!string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Lastfm))
        {
            providers.Add(new LastFmProvider(
                httpClientFactory.CreateClient(ServiceCollectionComposer.LastFmHttpClient),
                lookupOptions.ApiKeys.Lastfm,
                loggerFactory.CreateLogger<LastFmProvider>(),
                TimeSpan.FromMilliseconds(rateLimits.LastfmMs)));
            statusReporter.ReportLookupProvider("lastfm", available: true);
        }
        else
        {
            statusReporter.ReportLookupProvider("lastfm", available: false, detail: "no API key");
        }

        ILookupCache? cache = null;
        if (lookupOptions.Cache.Enabled)
        {
            var cacheDir = !string.IsNullOrWhiteSpace(lookupOptions.Cache.Directory)
                ? lookupOptions.Cache.Directory
                : Path.Combine(dataDirs.GetCacheDirectory(), "lookup");
            cache = new FileLookupCache(cacheDir, loggerFactory.CreateLogger<FileLookupCache>());
        }

        return new LookupRunner(providers, lookupOptions, cache, loggerFactory.CreateLogger<LookupRunner>());
    }

    /// <summary>
    /// Returns true when at least one provider in <paramref name="lookupOptions"/> can fire —
    /// MusicBrainz always qualifies (no key required), the others need their API key set.
    /// Used by the UI to decide whether the per-track "API" button is operational, independent
    /// of <see cref="LookupOptions.Enabled"/> (which only gates the automatic bulk-scan lookup).
    /// </summary>
    public static bool HasAnyAvailableProvider(LookupOptions lookupOptions)
    {
        ArgumentNullException.ThrowIfNull(lookupOptions);
        if (lookupOptions.Providers.Count == 0) return false;
        foreach (var p in lookupOptions.Providers)
        {
            if (string.Equals(p, "musicbrainz", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(p, "acoustid", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Acoustid)) return true;
            if (string.Equals(p, "discogs", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Discogs)) return true;
            if (string.Equals(p, "lastfm", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Lastfm)) return true;
        }
        return false;
    }
}
