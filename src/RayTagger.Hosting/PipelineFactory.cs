using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup;
using RayTagger.Lookup.Caching;

namespace RayTagger.Hosting;

/// <summary>
/// Builds a fully-wired pipeline (<see cref="IAnalysisRunner"/> + <see cref="ILookupRunner"/>)
/// from a loaded <see cref="TaggerOptions"/> tree. Both the CLI's <c>ScanHandler</c> and the UI's
/// <c>ScanCoordinator</c> consume this — keeps the analyzer / provider wiring in one place.
/// </summary>
/// <remarks>
/// The factory operates on an <see cref="IServiceProvider"/> assembled by
/// <see cref="ServiceCollectionComposer.AddRayTaggerHosting"/>. It resolves the stateless
/// dependencies (probe, runner, HTTP factory, data-dirs, logger factory) and stitches the
/// per-scan stages on top. Callers receive a <see cref="PipelineBuildResult"/> with the runners
/// and a report describing which analyzers and providers actually came online — useful for
/// surfacing in a startup banner (CLI) or status panel (UI).
/// </remarks>
public sealed class PipelineFactory
{
    private readonly IServiceProvider _services;

    public PipelineFactory(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public async Task<PipelineBuildResult> BuildAsync(
        TaggerOptions options,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(statusReporter);

        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var runner = _services.GetRequiredService<NativeProcessRunner>();
        var probe = _services.GetRequiredService<IAnalysisToolProbe>();
        var httpClientFactory = _services.GetRequiredService<IHttpClientFactory>();
        var dataDirs = _services.GetRequiredService<IUserDataDirectoryProvider>();

        var resolver = NativeToolsBootstrapFactory.BuildResolver(
            options.NativeTools, probe, loggerFactory, statusReporter);

        var analysisRunner = await BuildAnalysisRunnerAsync(
            options.Analysis, runner, resolver, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        var lookupRunner = BuildLookupRunner(
            options.Lookup, httpClientFactory, dataDirs, loggerFactory, statusReporter);

        return new PipelineBuildResult(analysisRunner, lookupRunner);
    }

    private static async Task<AnalysisRunner> BuildAnalysisRunnerAsync(
        AnalysisOptions analysis,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken)
    {
        var essentiaService = await TryBuildEssentiaServiceAsync(
            analysis, runner, resolver, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        var bpm = essentiaService is not null && IsEssentiaProvider(analysis.Bpm)
            ? new EssentiaBpmAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaBpmAnalyzer>())
            : (IBpmAnalyzer?)null;

        var key = essentiaService is not null && IsEssentiaProvider(analysis.Key)
            ? new EssentiaKeyAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaKeyAnalyzer>())
            : (IKeyAnalyzer?)null;

        var energy = essentiaService is not null && IsEssentiaProvider(analysis.Energy)
            ? new EssentiaEnergyAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaEnergyAnalyzer>())
            : (IEnergyAnalyzer?)null;

        var fingerprint = await TryBuildFingerprintAsync(
            analysis.Fingerprint, runner, resolver, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        return new AnalysisRunner(bpm, key, energy, fingerprint,
            loggerFactory.CreateLogger<AnalysisRunner>());
    }

    private static async Task<EssentiaAnalysisService?> TryBuildEssentiaServiceAsync(
        AnalysisOptions analysis,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken)
    {
        var dimensions = ListEssentiaDimensions(analysis);
        if (dimensions.Count == 0) return null;

        var resolution = await resolver.ResolveAsync(EssentiaAnalysisService.Executable, cancellationToken).ConfigureAwait(false);
        if (resolution is null)
        {
            foreach (var dim in dimensions)
            {
                statusReporter.ReportMissing(dim, EssentiaAnalysisService.ProviderName,
                    "Binary not on PATH and auto-bootstrap could not provide it.");
            }
            return null;
        }

        var timeoutSeconds = MaxEnabledEssentiaTimeout(analysis);
        var service = new EssentiaAnalysisService(
            runner,
            loggerFactory.CreateLogger<EssentiaAnalysisService>(),
            TimeSpan.FromSeconds(timeoutSeconds),
            executablePath: resolution.ExecutablePath);

        foreach (var dim in dimensions)
        {
            statusReporter.ReportTool(dim, EssentiaAnalysisService.ProviderName, resolution);
        }
        return service;
    }

    private static async Task<IFingerprintAnalyzer?> TryBuildFingerprintAsync(
        AnalyzerOptions opts,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken)
    {
        if (!opts.Enabled
            || !opts.Provider.Equals(ChromaprintFingerprintAnalyzer.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var resolution = await resolver.ResolveAsync(ChromaprintFingerprintAnalyzer.Executable, cancellationToken).ConfigureAwait(false);
        if (resolution is null)
        {
            statusReporter.ReportMissing("fingerprint", ChromaprintFingerprintAnalyzer.ProviderName,
                "Binary not on PATH and auto-bootstrap could not provide it.");
            return null;
        }

        statusReporter.ReportTool("fingerprint", ChromaprintFingerprintAnalyzer.ProviderName, resolution);

        return new ChromaprintFingerprintAnalyzer(
            runner,
            loggerFactory.CreateLogger<ChromaprintFingerprintAnalyzer>(),
            TimeSpan.FromSeconds(opts.TimeoutSeconds),
            executablePath: resolution.ExecutablePath);
    }

    private static ILookupRunner BuildLookupRunner(
        LookupOptions lookupOptions,
        IHttpClientFactory httpClientFactory,
        IUserDataDirectoryProvider dataDirs,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter)
    {
        if (!lookupOptions.Enabled)
        {
            return NoopLookupRunner.Instance;
        }

        var providers = new List<IMetadataProvider>(4);

        if (!string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Acoustid))
        {
            providers.Add(new AcoustIdProvider(
                httpClientFactory.CreateClient(ServiceCollectionComposer.AcoustIdHttpClient),
                lookupOptions.ApiKeys.Acoustid,
                loggerFactory.CreateLogger<AcoustIdProvider>()));
            statusReporter.ReportLookupProvider("acoustid", available: true);
        }
        else
        {
            statusReporter.ReportLookupProvider("acoustid", available: false, detail: "no API key");
        }

        // MusicBrainz needs no API key, only a descriptive User-Agent. Always available.
        providers.Add(new MusicBrainzProvider(
            httpClientFactory.CreateClient(ServiceCollectionComposer.MusicBrainzHttpClient),
            loggerFactory.CreateLogger<MusicBrainzProvider>()));
        statusReporter.ReportLookupProvider("musicbrainz", available: true);

        if (!string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Discogs))
        {
            providers.Add(new DiscogsProvider(
                httpClientFactory.CreateClient(ServiceCollectionComposer.DiscogsHttpClient),
                lookupOptions.ApiKeys.Discogs,
                loggerFactory.CreateLogger<DiscogsProvider>()));
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
                loggerFactory.CreateLogger<LastFmProvider>()));
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

    // -------- internal helpers ----------------------------------------------------------------

    private static List<string> ListEssentiaDimensions(AnalysisOptions analysis)
    {
        var dims = new List<string>(3);
        if (IsEssentiaProvider(analysis.Bpm)) dims.Add("bpm");
        if (IsEssentiaProvider(analysis.Key)) dims.Add("key");
        if (IsEssentiaProvider(analysis.Energy)) dims.Add("energy");
        return dims;
    }

    private static int MaxEnabledEssentiaTimeout(AnalysisOptions analysis)
    {
        var max = 0;
        if (IsEssentiaProvider(analysis.Bpm)) max = Math.Max(max, analysis.Bpm.TimeoutSeconds);
        if (IsEssentiaProvider(analysis.Key)) max = Math.Max(max, analysis.Key.TimeoutSeconds);
        if (IsEssentiaProvider(analysis.Energy)) max = Math.Max(max, analysis.Energy.TimeoutSeconds);
        return max > 0 ? max : 60;
    }

    private static bool IsEssentiaProvider(AnalyzerOptions opts) =>
        opts.Enabled
        && opts.Provider.Equals(EssentiaAnalysisService.ProviderName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What <see cref="PipelineFactory.BuildAsync"/> hands back. The runners are wired and ready for
/// <see cref="TagPipeline"/>; the report says which analyzers / providers came online.
/// </summary>
public sealed record PipelineBuildResult(IAnalysisRunner AnalysisRunner, ILookupRunner LookupRunner);
