using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Analysis.Internal;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using RayTagger.Core.Pipeline;
using RayTagger.Metadata;

namespace RayTagger.Hosting;

/// <summary>
/// Registers the services every RayTagger host (CLI and UI today, Avalonia + future automation
/// later) needs: stateless pipeline parts, native-tool infrastructure, and the four
/// lookup-provider <see cref="HttpClient"/>s wrapped in a Polly resilience pipeline.
/// </summary>
/// <remarks>
/// The composer is intentionally side-effect-light: it does NOT instantiate analyzers
/// (resolved per scan via <see cref="PipelineFactory"/>) and it does NOT instantiate the
/// lookup providers themselves (each needs an API key and a named HttpClient). Callers resolve
/// <see cref="IHttpClientFactory"/> and <see cref="IAnalysisToolProbe"/> from the container and
/// finish wiring at scan-start time when the loaded options are known.
/// </remarks>
public static class ServiceCollectionComposer
{
    public const string AcoustIdHttpClient = "acoustid";
    public const string MusicBrainzHttpClient = "musicbrainz";
    public const string DiscogsHttpClient = "discogs";
    public const string LastFmHttpClient = "lastfm";

    public static IServiceCollection AddRayTaggerHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Stateless metadata + pipeline services.
        services.AddSingleton<ITagReader, TagLibTagReader>();
        services.AddSingleton<BackupSidecarWriter>();
        services.AddSingleton<ITagWriter, TagLibTagWriter>();
        services.AddSingleton<ITagReaderAdapter>(sp => new TagReaderAdapter(sp.GetRequiredService<ITagReader>()));
        services.AddSingleton<ITagWriterAdapter>(sp => new TagWriterAdapter(sp.GetRequiredService<ITagWriter>()));
        services.AddSingleton<SidecarRestoreService>();
        services.AddSingleton<IFileDiscoveryService, FileDiscoveryService>();
        services.AddSingleton<IMappingRuleEngine, MappingRuleEngine>();
        services.AddSingleton<ISortService, SortService>();
        services.AddSingleton<IUserDataDirectoryProvider, UserDataDirectoryProvider>();

        // Native-tool infrastructure.
        services.AddSingleton<NativeProcessRunner>();
        services.AddSingleton<IAnalysisToolProbe, AnalysisToolProbe>();

        // Lookup HttpClients with the Polly resilience pipeline.
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
            // 15s per-request ceiling on top of the resilience pipeline's per-attempt + total
            // timeouts below. MusicBrainz/Discogs require a descriptive User-Agent.
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "RayTagger/0.1 (+https://github.com/RAYCOON/raytagger)");
        })
        // Standard resilience: retry transient errors, honour Retry-After on 429/503, circuit
        // breaker on sustained failure, total + per-attempt timeouts.
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
        });
    }
}
