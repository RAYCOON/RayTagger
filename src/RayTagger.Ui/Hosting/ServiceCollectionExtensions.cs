using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Analysis.Internal;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using RayTagger.Core.Pipeline;
using RayTagger.Metadata;
using RayTagger.Ui.Services;
using RayTagger.Ui.ViewModels;
using Serilog;
using Serilog.Events;

namespace RayTagger.Ui.Hosting;

/// <summary>
/// DI registration for the Avalonia UI. Mirrors the CLI's <c>ServiceCollectionComposer</c> for
/// the shared pipeline + native-tool services, then layers UI-specific bits (view-models,
/// <see cref="ScanCoordinator"/>) on top.
/// </summary>
/// <remarks>
/// We don't share the CLI composer directly because the CLI project is an exe (referencing it
/// from another exe is messy), and the registration list is small enough that duplication is
/// cheaper than introducing a shared <c>RayTagger.Hosting</c> library this early. Once the UI
/// stops being scaffold-grade, both can collapse onto a shared composer.
/// </remarks>
public static class ServiceCollectionExtensions
{
    public const string AcoustIdHttpClient = "acoustid";
    public const string MusicBrainzHttpClient = "musicbrainz";
    public const string DiscogsHttpClient = "discogs";
    public const string LastFmHttpClient = "lastfm";

    public static IServiceCollection AddRayTaggerUiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Serilog → Microsoft.Extensions.Logging. The UI uses the file sink (logs are easier to
        // share than the in-app console) plus a debug console for the dev loop.
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(Path.GetTempPath(), "raytagger-ui", "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
        services.AddLogging(b => b.AddSerilog(logger, dispose: true));

        // Shared pipeline services (mirror CLI composer).
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
        services.AddSingleton<NativeProcessRunner>();
        services.AddSingleton<IAnalysisToolProbe, AnalysisToolProbe>();

        // Lookup HttpClients with resilience pipeline.
        AddProviderHttpClient(services, AcoustIdHttpClient, "https://api.acoustid.org/");
        AddProviderHttpClient(services, MusicBrainzHttpClient, "https://musicbrainz.org/");
        AddProviderHttpClient(services, DiscogsHttpClient, "https://api.discogs.com/");
        AddProviderHttpClient(services, LastFmHttpClient, "https://ws.audioscrobbler.com/");

        // UI-specific services and view-models. View-models are transient — each MainWindow has
        // its own, and child VMs are created on demand.
        services.AddSingleton<ScanCoordinator>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<ScanViewModel>();

        return services;
    }

    private static void AddProviderHttpClient(IServiceCollection services, string name, string baseAddress)
    {
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = new Uri(baseAddress);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "RayTagger.Ui/0.1 (+https://github.com/RAYCOON/raytagger)");
        })
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
