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

    /// <summary>
    /// Publishes the loaded <c>lookup.user_agent_contact</c> to the singleton
    /// <see cref="UserAgentState"/>. Every <see cref="HttpClient"/> created by
    /// <see cref="IHttpClientFactory"/> reads through this state via
    /// <see cref="UserAgentHandler"/>, so calling this once per scan is enough.
    /// </summary>
    private void ApplyUserAgentContact(LookupOptions lookup)
    {
        var state = _services.GetRequiredService<UserAgentState>();
        state.SetContact(lookup.UserAgentContact);
    }

    /// <summary>
    /// Builds an <see cref="IEnergyCalibrationService"/> for the <c>calibrate-energy</c> CLI verb /
    /// the UI "Calibrate Energy" button. Reuses the same Essentia bootstrap path as
    /// <see cref="BuildAsync"/> so the service either picks up an on-PATH binary or downloads one
    /// via the native-tools manifest. Returns <c>null</c> when Essentia cannot be made available
    /// (offline + missing PATH binary) — the caller should surface that as an actionable error.
    /// </summary>
    public async Task<IEnergyCalibrationService?> BuildEnergyCalibrationServiceAsync(
        TaggerOptions options,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(statusReporter);

        ApplyUserAgentContact(options.Lookup);

        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var runner = _services.GetRequiredService<NativeProcessRunner>();
        var probe = _services.GetRequiredService<IAnalysisToolProbe>();
        var httpClientFactory = _services.GetRequiredService<IHttpClientFactory>();
        var discovery = _services.GetRequiredService<IFileDiscoveryService>();

        var bootstrapHttp = httpClientFactory.CreateClient(ServiceCollectionComposer.NativeToolsBootstrapHttpClient);
        var resolver = NativeToolsBootstrapFactory.BuildResolver(
            options.NativeTools, probe, bootstrapHttp, loggerFactory, statusReporter);

        var essentia = await TryBuildEssentiaServiceAsync(
            options.Analysis, runner, resolver, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        if (essentia is null) return null;

        return new EnergyCalibrationService(
            discovery, essentia, loggerFactory.CreateLogger<EnergyCalibrationService>());
    }

    public async Task<PipelineBuildResult> BuildAsync(
        TaggerOptions options,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(statusReporter);

        ApplyUserAgentContact(options.Lookup);

        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var runner = _services.GetRequiredService<NativeProcessRunner>();
        var probe = _services.GetRequiredService<IAnalysisToolProbe>();
        var httpClientFactory = _services.GetRequiredService<IHttpClientFactory>();
        var dataDirs = _services.GetRequiredService<IUserDataDirectoryProvider>();

        // Pooled, factory-owned HttpClient for the binary downloader — keeps the per-scan
        // socket-pool reuse behaviour the lookup clients already get.
        var bootstrapHttp = httpClientFactory.CreateClient(ServiceCollectionComposer.NativeToolsBootstrapHttpClient);
        var resolver = NativeToolsBootstrapFactory.BuildResolver(
            options.NativeTools, probe, bootstrapHttp, loggerFactory, statusReporter);

        var analysisRunner = await BuildAnalysisRunnerAsync(
            options, runner, resolver, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        var lookupRunner = BuildLookupRunner(
            options.Lookup, httpClientFactory, dataDirs, loggerFactory, statusReporter);

        return new PipelineBuildResult(analysisRunner, lookupRunner);
    }

    private static async Task<AnalysisRunner> BuildAnalysisRunnerAsync(
        TaggerOptions options,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken)
    {
        var analysis = options.Analysis;
        var essentiaService = await TryBuildEssentiaServiceAsync(
            analysis, runner, resolver, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        var bpm = essentiaService is not null && IsEssentiaProvider(analysis.Bpm)
            ? new EssentiaBpmAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaBpmAnalyzer>(), analysis.Bpm)
            : (IBpmAnalyzer?)null;

        var key = essentiaService is not null && IsEssentiaProvider(analysis.Key)
            ? new EssentiaKeyAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaKeyAnalyzer>())
            : (IKeyAnalyzer?)null;

        var energy = essentiaService is not null && IsEssentiaProvider(analysis.Energy)
            ? new EssentiaEnergyAnalyzer(
                essentiaService,
                loggerFactory.CreateLogger<EssentiaEnergyAnalyzer>(),
                analysis.Energy.LoadedCalibration)
            : (IEnergyAnalyzer?)null;

        var fingerprint = await TryBuildFingerprintAsync(
            analysis.Fingerprint, runner, resolver, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        // Resolver is wired in unconditionally — when neither per-genre ranges nor a fallback
        // are configured, Resolve() short-circuits to null and the BPM analyzer leaves the raw
        // Essentia value alone (the pipeline-level snap still cleans up drift).
        var tempoRangeResolver = new TempoRangeResolver(
            analysis.Bpm,
            options.Taxonomy.Loaded,
            loggerFactory.CreateLogger<TempoRangeResolver>());

        return new AnalysisRunner(
            bpm, key, energy, fingerprint, tempoRangeResolver,
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
        IToolStatusReporter statusReporter) =>
        LookupRunnerBuilder.Build(lookupOptions, httpClientFactory, dataDirs, loggerFactory, statusReporter);

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
/// <remarks>
/// Implements <see cref="IDisposable"/> so callers retain a stable <c>using</c>-pattern even though
/// the current build has no transient resources to release. If a future analyzer needs scoped
/// cleanup, route it through here.
/// </remarks>
public sealed class PipelineBuildResult : IDisposable
{
    public IAnalysisRunner AnalysisRunner { get; }
    public ILookupRunner LookupRunner { get; }

    public PipelineBuildResult(IAnalysisRunner analysisRunner, ILookupRunner lookupRunner)
    {
        ArgumentNullException.ThrowIfNull(analysisRunner);
        ArgumentNullException.ThrowIfNull(lookupRunner);
        AnalysisRunner = analysisRunner;
        LookupRunner = lookupRunner;
    }

    public void Dispose()
    {
        // No-op for now. Kept on IDisposable so the `using` pattern at call sites stays valid.
    }
}
