using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Analysis.Genre;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
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

        var classifierRunner = await BuildClassifierRunnerAsync(
            options, runner, probe, resolver, dataDirs, loggerFactory, statusReporter, cancellationToken)
            .ConfigureAwait(false);

        return new PipelineBuildResult(analysisRunner, lookupRunner, classifierRunner);
    }

    /// <summary>
    /// Builds the audio-based genre classifier runner. When every flag is off (the default), this
    /// returns <see cref="NoopGenreClassifierRunner.Instance"/> — the pipeline behaves byte-for-byte
    /// like the pre-classifier code. As classifiers come on we add their instances to the
    /// composite <see cref="GenreClassifierRunner"/>; per-classifier dependency probing (Essentia
    /// for the heuristic, Python+models for the TF tier) decides whether each one actually runs.
    /// </summary>
    private static async Task<IGenreClassifierRunner> BuildClassifierRunnerAsync(
        TaggerOptions options,
        NativeProcessRunner runner,
        IAnalysisToolProbe probe,
        NativeToolResolver resolver,
        IUserDataDirectoryProvider dataDirs,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken)
    {
        var gc = options.Analysis.GenreClassifier;
        if (!gc.Heuristic.Enabled
            && !gc.Tensorflow.GenreElectronic.Enabled
            && !gc.Tensorflow.MtgJamendo.Enabled
            && !gc.Tensorflow.DiscogsEffnet.Enabled)
        {
            return NoopGenreClassifierRunner.Instance;
        }

        var classifiers = new List<IGenreClassifier>();

        if (gc.Heuristic.Enabled)
        {
            // Register the heuristic as a virtual Essentia dimension so the startup banner
            // surfaces it explicitly (missing-binary OR available). Status reporting then
            // matches the BPM / Key / Energy lines the user already knows.
            var essentia = await TryBuildEssentiaServiceAsync(
                options.Analysis, runner, resolver, loggerFactory, statusReporter, cancellationToken,
                extraDimensions: ["genre-heuristic"])
                .ConfigureAwait(false);
            if (essentia is not null)
            {
                classifiers.Add(new HeuristicGenreClassifier(
                    essentia,
                    gc.Heuristic,
                    loggerFactory.CreateLogger<HeuristicGenreClassifier>()));
            }
            // Failure case is already reported by TryBuildEssentiaServiceAsync via ReportMissing
            // for the "genre-heuristic" dimension — no extra noise here.
        }

        if (gc.Tensorflow.GenreElectronic.Enabled
            || gc.Tensorflow.MtgJamendo.Enabled
            || gc.Tensorflow.DiscogsEffnet.Enabled)
        {
            var tfClassifiers = await BuildTensorflowClassifiersAsync(
                options, runner, probe, resolver, dataDirs,
                loggerFactory, statusReporter, cancellationToken).ConfigureAwait(false);
            classifiers.AddRange(tfClassifiers);
        }

        if (classifiers.Count == 0)
        {
            return NoopGenreClassifierRunner.Instance;
        }

        return new GenreClassifierRunner(
            classifiers,
            loggerFactory.CreateLogger<GenreClassifierRunner>());
    }

    /// <summary>
    /// Builds the TF-tier classifiers — one per enabled model. Each model independently probes
    /// its dependencies (shared Python + script, model-specific files via the bootstrapper);
    /// failures are reported via <see cref="IToolStatusReporter.ReportMissing"/> for that specific
    /// model and don't affect the others. Returns the successfully constructed classifiers.
    /// </summary>
    private static async Task<List<IGenreClassifier>> BuildTensorflowClassifiersAsync(
        TaggerOptions options,
        NativeProcessRunner runner,
        IAnalysisToolProbe probe,
        NativeToolResolver resolver,
        IUserDataDirectoryProvider dataDirs,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken)
    {
        var tf = options.Analysis.GenreClassifier.Tensorflow;
        var built = new List<IGenreClassifier>();

        // 1) Python interpreter — shared by every TF model. If it's missing, every TF flag
        //    fails the same way; report once per enabled model so the user sees each line in
        //    the status banner where they expect it.
        var pythonExe = string.IsNullOrWhiteSpace(tf.PythonExecutable) ? "python3" : tf.PythonExecutable;
        var pythonProbe = await probe.ProbeAsync(pythonExe, ["--version"], cancellationToken).ConfigureAwait(false);
        if (!pythonProbe.IsAvailable)
        {
            ReportTfMissing(tf, statusReporter,
                $"Python interpreter '{pythonExe}' not available — install Python 3.9+ and `pip install essentia-tensorflow`.");
            return built;
        }

        // 2) Bridge script — discovered next to the running executable, or via tagger.yaml override.
        var scriptPath = ResolveTfScriptPath(tf.ScriptPath);
        if (scriptPath is null)
        {
            ReportTfMissing(tf, statusReporter,
                "Python bridge script not found. Set tensorflow.script_path in tagger.yaml or "
                + "ship `tools/raytagger-genre-classifier/raytagger_genre_classifier.py` next to the Tagger binary.");
            return built;
        }

        // 3) Models directory — defaults to the same root the bootstrapper uses for cache writes.
        var modelsBase = string.IsNullOrWhiteSpace(tf.ModelsDirectory)
            ? Path.Combine(dataDirs.GetDataDirectory(), "models")
            : tf.ModelsDirectory;

        // 4) Per-enabled-model: bootstrap files, then construct the classifier. The bootstrapper
        //    may be null when no native-tools.yaml is reachable — we still try to use cached files
        //    if any exist (lets users point models_directory at a hand-managed location without
        //    needing an auto-bootstrap manifest).
        var bootstrapper = resolver.Bootstrapper;

        var taxonomy = options.Taxonomy.Loaded;
        if (tf.GenreElectronic.Enabled)
        {
            var clf = await TryBuildTfClassifierAsync(
                "electronic", tf.GenreElectronic, defaultTopK: 5, pythonExe, scriptPath, modelsBase,
                taxonomy, bootstrapper, runner, loggerFactory, statusReporter, cancellationToken).ConfigureAwait(false);
            if (clf is not null) built.Add(clf);
        }
        if (tf.MtgJamendo.Enabled)
        {
            var clf = await TryBuildTfClassifierAsync(
                "jamendo", tf.MtgJamendo, defaultTopK: 5, pythonExe, scriptPath, modelsBase,
                taxonomy, bootstrapper, runner, loggerFactory, statusReporter, cancellationToken).ConfigureAwait(false);
            if (clf is not null) built.Add(clf);
        }
        if (tf.DiscogsEffnet.Enabled)
        {
            var clf = await TryBuildTfClassifierAsync(
                "discogs-effnet", tf.DiscogsEffnet, defaultTopK: 10, pythonExe, scriptPath, modelsBase,
                taxonomy, bootstrapper, runner, loggerFactory, statusReporter, cancellationToken).ConfigureAwait(false);
            if (clf is not null) built.Add(clf);
        }

        // 5) Surface the §4.0b subprocess-cost estimate when at least one TF classifier is active.
        //    Lets users with large libraries see the tradeoff before the scan starts.
        if (built.Count > 0)
        {
            statusReporter.ReportNote(
                $"Genre-Klassifikator: {built.Count} TF-Modell(e) aktiv. ~3 sec Subprocess-Overhead pro Track pro Modell — "
                + "siehe docs/PLAN_GENRE_CLASSIFICATION.md §4.0b.");
        }

        return built;
    }

    private static async Task<IGenreClassifier?> TryBuildTfClassifierAsync(
        string modelKey,
        TensorflowModelOptions modelOpts,
        int defaultTopK,
        string pythonExe,
        string scriptPath,
        string modelsBase,
        Taxonomy taxonomy,
        INativeToolBootstrapper? bootstrapper,
        NativeProcessRunner runner,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken)
    {
        var displayName = TensorflowGenreClassifier.NameFor(modelKey);

        // No bootstrapper = no auto-download. Fall back to a cached / manually-staged copy if
        // the model directory already exists with the expected files; otherwise the classifier
        // itself will exit code 2 on the first invocation and we'd see a worse failure mode.
        if (bootstrapper is null)
        {
            var existingDir = Path.Combine(modelsBase, modelKey);
            if (!Directory.Exists(existingDir))
            {
                statusReporter.ReportMissing("genre-classifier", displayName,
                    "native-tools.yaml unavailable and no pre-staged model directory at " + existingDir);
                return null;
            }
        }
        else
        {
            try
            {
                await bootstrapper.EnsureModelAsync(modelKey, cancellationToken).ConfigureAwait(false);
            }
            catch (NativeToolBootstrapException ex)
            {
                statusReporter.ReportMissing("genre-classifier", displayName,
                    $"Model bootstrap failed: {ex.Message}");
                return null;
            }
        }

        // Build the aggregation config (§4.0c) when the model opts in AND a non-empty taxonomy
        // is loaded — without a taxonomy the matcher can't resolve any candidate to a parent
        // and aggregation would no-op anyway. Falls through silently to non-aggregated mode.
        AggregationConfig? aggregation = null;
        if (modelOpts.AggregateTopK && taxonomy.Genres.Count > 0)
        {
            aggregation = new AggregationConfig(
                Matcher: new TaxonomyGenreMatcher(taxonomy),
                PerCandidateFloor: modelOpts.AggregatePerCandidateFloor,
                MinTotal: modelOpts.AggregateMinTotal,
                FallbackOnDiffuse: modelOpts.AggregateFallbackOnDiffuse);
        }

        var minConfStr = modelOpts.MinConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var aggSuffix = aggregation is null
            ? string.Empty
            : $", aggregate=on (min_total={modelOpts.AggregateMinTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})";
        statusReporter.ReportNote(
            $"Genre-Klassifikator '{displayName}' aktiv (min_confidence={minConfStr}, top_k={defaultTopK}{aggSuffix}).");

        // TF cold-start is slow (3-5s for first inference). A 2-minute per-track timeout absorbs
        // worst case (large file, slow machine) without letting wedged scripts pile up.
        return new TensorflowGenreClassifier(
            modelKey,
            pythonExe,
            scriptPath,
            modelsBase,
            modelOpts.MinConfidence,
            defaultTopK,
            timeout: TimeSpan.FromMinutes(2),
            runner: runner,
            logger: loggerFactory.CreateLogger<TensorflowGenreClassifier>(),
            aggregation: aggregation);
    }

    private static void ReportTfMissing(TensorflowClassifierOptions tf, IToolStatusReporter statusReporter, string reason)
    {
        if (tf.GenreElectronic.Enabled)
        {
            statusReporter.ReportMissing("genre-classifier", TensorflowGenreClassifier.NameFor("electronic"), reason);
        }
        if (tf.MtgJamendo.Enabled)
        {
            statusReporter.ReportMissing("genre-classifier", TensorflowGenreClassifier.NameFor("jamendo"), reason);
        }
        if (tf.DiscogsEffnet.Enabled)
        {
            statusReporter.ReportMissing("genre-classifier", TensorflowGenreClassifier.NameFor("discogs-effnet"), reason);
        }
    }

    /// <summary>
    /// Locates <c>raytagger_genre_classifier.py</c>. Order of resolution:
    /// <list type="number">
    ///   <item>Explicit <c>tensorflow.script_path</c> in tagger.yaml (must exist).</item>
    ///   <item>Walk upward from <see cref="AppContext.BaseDirectory"/> looking for
    ///         <c>tools/raytagger-genre-classifier/raytagger_genre_classifier.py</c>. Covers both
    ///         dev (`dotnet run` from <c>bin/Debug/net10.0/</c> several levels below the repo root)
    ///         AND published self-contained binaries that ship the tools directory next to the exe.</item>
    /// </list>
    /// Returns <c>null</c> when nothing matches — caller reports as missing.
    /// </summary>
    private static string? ResolveTfScriptPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        const string scriptRel = "tools/raytagger-genre-classifier/raytagger_genre_classifier.py";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, scriptRel);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
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
            ? new EssentiaKeyAnalyzer(essentiaService, analysis.Key, loggerFactory.CreateLogger<EssentiaKeyAnalyzer>())
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

    /// <summary>
    /// <paramref name="extraDimensions"/> lets non-analyzer consumers (e.g. the heuristic genre
    /// classifier) register themselves as Essentia users so the status banner reports them
    /// alongside the BPM / Key / Energy dimensions. Without that, "heuristic enabled but no
    /// Essentia analyzer configured" silently fell through and emitted a misleading
    /// "binary missing" line from the classifier wiring later. Pass <c>null</c> for the original
    /// analyzers-only behaviour.
    /// </summary>
    private static async Task<EssentiaAnalysisService?> TryBuildEssentiaServiceAsync(
        AnalysisOptions analysis,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? extraDimensions = null)
    {
        var dimensions = ListEssentiaDimensions(analysis);
        if (extraDimensions is not null)
        {
            foreach (var dim in extraDimensions)
            {
                if (!dimensions.Contains(dim, StringComparer.Ordinal))
                {
                    dimensions.Add(dim);
                }
            }
        }
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
    public IGenreClassifierRunner ClassifierRunner { get; }

    public PipelineBuildResult(
        IAnalysisRunner analysisRunner,
        ILookupRunner lookupRunner,
        IGenreClassifierRunner classifierRunner)
    {
        ArgumentNullException.ThrowIfNull(analysisRunner);
        ArgumentNullException.ThrowIfNull(lookupRunner);
        ArgumentNullException.ThrowIfNull(classifierRunner);
        AnalysisRunner = analysisRunner;
        LookupRunner = lookupRunner;
        ClassifierRunner = classifierRunner;
    }

    public void Dispose()
    {
        // No-op for now. Kept on IDisposable so the `using` pattern at call sites stays valid.
    }
}
