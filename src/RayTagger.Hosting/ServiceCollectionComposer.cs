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
    /// <summary>Named <see cref="HttpClient"/> for AcoustID — fingerprint → MBID handshake.</summary>
    public const string AcoustIdHttpClient = "acoustid";
    /// <summary>Named <see cref="HttpClient"/> for MusicBrainz — release / recording lookup.</summary>
    public const string MusicBrainzHttpClient = "musicbrainz";
    /// <summary>Named <see cref="HttpClient"/> for Discogs — style + genre lookup.</summary>
    public const string DiscogsHttpClient = "discogs";
    /// <summary>Named <see cref="HttpClient"/> for Last.fm — top-tag lookup.</summary>
    public const string LastFmHttpClient = "lastfm";
    /// <summary>Named <see cref="HttpClient"/> for the native-tools downloader — long timeout, no resilience handler (one-shot binary downloads, not retryable RPCs).</summary>
    public const string NativeToolsBootstrapHttpClient = "native-tools-bootstrap";

    /// <summary>
    /// Registers the metadata, pipeline, and native-tool services, the four lookup
    /// <see cref="HttpClient"/>s with their Polly resilience pipelines, and the
    /// <see cref="PipelineFactory"/> hosts use to build per-scan analyzer + lookup runners.
    /// </summary>
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
        services.AddSingleton<TaxonomyGenreResolver>();
        services.AddSingleton<IPerTrackLookupService, PerTrackLookupService>();
        services.AddSingleton<ISortService, SortService>();
        services.AddSingleton<IUserDataDirectoryProvider, UserDataDirectoryProvider>();

        // Native-tool infrastructure. INativeProcessRunner forwards to the same concrete
        // instance — interface registration only exists for unit-testability of the TF genre
        // classifier (legacy analyzers depend on the concrete type and need no changes).
        services.AddSingleton<NativeProcessRunner>();
        services.AddSingleton<INativeProcessRunner>(sp => sp.GetRequiredService<NativeProcessRunner>());
        services.AddSingleton<IAnalysisToolProbe, AnalysisToolProbe>();

        // User-Agent infrastructure. The state singleton is mutated once per scan from the
        // loaded LookupOptions.UserAgentContact; the handler is registered transient because
        // IHttpClientFactory instantiates one per named client.
        services.AddSingleton<UserAgentState>();
        services.AddTransient<UserAgentHandler>();

        // Lookup HttpClients with the Polly resilience pipeline.
        AddProviderHttpClient(services, AcoustIdHttpClient, "https://api.acoustid.org/");
        AddProviderHttpClient(services, MusicBrainzHttpClient, "https://musicbrainz.org/");
        AddProviderHttpClient(services, DiscogsHttpClient, "https://api.discogs.com/");
        AddProviderHttpClient(services, LastFmHttpClient, "https://ws.audioscrobbler.com/");

        // Native-tools downloader: 5-minute timeout (5-30 MB archives), no resilience pipeline.
        // Pooled through IHttpClientFactory so per-scan invocations don't leak socket handles.
        services.AddHttpClient(NativeToolsBootstrapHttpClient, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddHttpMessageHandler<UserAgentHandler>();

        // Shared pipeline factory — both the CLI's ScanHandler and the UI's ScanCoordinator
        // resolve and reuse this. Registering it here keeps the wiring symmetric.
        //
        // Note: IGenreClassifierRunner is intentionally NOT registered. PipelineFactory.BuildAsync
        // constructs it per-scan and hands it back via PipelineBuildResult.ClassifierRunner — the
        // same pattern as AnalysisRunner / LookupRunner. The DI container has no useful default
        // because the runner's classifier list depends on per-scan TaggerOptions.
        services.AddSingleton<PipelineFactory>();

        return services;
    }

    private static void AddProviderHttpClient(IServiceCollection services, string name, string baseAddress)
    {
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = new Uri(baseAddress);
            // 15s per-request ceiling on top of the resilience pipeline's per-attempt + total
            // timeouts below. The User-Agent is stamped per-request by UserAgentHandler so the
            // value tracks lookup.user_agent_contact updates without rebuilding the client.
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddHttpMessageHandler<UserAgentHandler>()
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
