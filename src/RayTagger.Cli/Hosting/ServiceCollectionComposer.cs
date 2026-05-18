using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup;
using RayTagger.Lookup.Caching;
using RayTagger.Metadata;

namespace RayTagger.Cli.Hosting;

/// <summary>
/// Registers everything the CLI verb handlers (and the future Phase-6 UI) both need from the DI
/// container — pipeline services, native-tool resolver, and the four lookup-provider HttpClients
/// wrapped in a Polly resilience pipeline (retry, circuit-breaker, rate-limit-aware on 429/503).
/// </summary>
/// <remarks>
/// The composer is intentionally minimal: it does NOT instantiate analyzers (those depend on
/// the resolved native-tool paths discovered at scan startup) and it does NOT instantiate the
/// lookup providers themselves (each needs its own configured HttpClient by name and an API
/// key). The verb handler resolves an HttpClientFactory and the probe from the container and
/// finishes the wiring once it knows the loaded options.
/// </remarks>
internal static class ServiceCollectionComposer
{
    public const string AcoustIdHttpClient = "acoustid";
    public const string MusicBrainzHttpClient = "musicbrainz";
    public const string DiscogsHttpClient = "discogs";
    public const string LastFmHttpClient = "lastfm";

    public static IServiceCollection AddRayTaggerServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton-by-nature, stateless metadata helpers.
        services.AddSingleton<ITagReader, TagLibTagReader>();
        services.AddSingleton<BackupSidecarWriter>();
        services.AddSingleton<ITagWriter, TagLibTagWriter>();
        services.AddSingleton<ITagReaderAdapter>(sp => new TagReaderAdapter(sp.GetRequiredService<ITagReader>()));
        services.AddSingleton<ITagWriterAdapter>(sp => new TagWriterAdapter(sp.GetRequiredService<ITagWriter>()));
        services.AddSingleton<SidecarRestoreService>();

        // Pipeline-stage services that don't need per-scan configuration.
        services.AddSingleton<IFileDiscoveryService, FileDiscoveryService>();
        services.AddSingleton<IMappingRuleEngine, MappingRuleEngine>();
        services.AddSingleton<ISortService, SortService>();
        services.AddSingleton<IUserDataDirectoryProvider, UserDataDirectoryProvider>();

        // Native-tool infrastructure.
        services.AddSingleton<NativeProcessRunner>();
        services.AddSingleton<IAnalysisToolProbe, AnalysisToolProbe>();

        // Lookup provider HttpClients each get the standard resilience pipeline + a base address
        // + the descriptive User-Agent MusicBrainz / Discogs require.
        AddProviderHttpClient(services, AcoustIdHttpClient, "https://api.acoustid.org/");
        AddProviderHttpClient(services, MusicBrainzHttpClient, "https://musicbrainz.org/");
        AddProviderHttpClient(services, DiscogsHttpClient, "https://api.discogs.com/");
        AddProviderHttpClient(services, LastFmHttpClient, "https://ws.audioscrobbler.com/");

        return services;
    }

    private static void AddProviderHttpClient(IServiceCollection services, string name, string baseAddress)
    {
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = new Uri(baseAddress);
            // 15s per-attempt timeout (vs the 100s .NET default). The resilience pipeline below
            // adds its own per-attempt + total-request timeouts that compose with this; this
            // value is the absolute ceiling on a single HTTP request before we abort.
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "RayTagger/0.1 (+https://github.com/RAYCOON/raytagger)");
        })
        // AddStandardResilienceHandler delivers retry (transient errors only — 5xx / 408 / network
        // failures; never 4xx), rate-limit-aware backoff on 429/503 with Retry-After honoured,
        // circuit breaker, total-request and per-attempt timeouts. This is the contract review
        // #8 demanded — replaces the previous "15s timeout + no retry" with a real resilience
        // pipeline that respects the upstream provider's load signals.
        .AddStandardResilienceHandler(options =>
        {
            // MusicBrainz hard-limits to ~1 req/s and replies 503 with Retry-After=N when
            // exceeded — make the retry budget generous enough that we ride out the wait rather
            // than failing the scan.
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
        });
    }
}
