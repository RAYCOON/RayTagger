using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Analysis.Internal;
using RayTagger.Cli.Hosting;
using RayTagger.Cli.Output;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup;
using RayTagger.Lookup.Caching;
using RayTagger.Metadata;
using Spectre.Console;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Implements the <c>scan</c> verb. Loads config, applies CLI overrides, builds the pipeline by
/// hand (DI via Hosting comes later when more services need composing), runs it, and renders
/// outcomes. Returns a process exit code reflecting per-file failure totals.
/// </summary>
internal static class ScanHandler
{
    public static async Task<int> RunAsync(ParseResult parseResult, ScanCommandOptions opts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(opts);

        var configFile = parseResult.GetValue(opts.Config);
        var source = parseResult.GetValue(opts.Source);
        var dryRun = parseResult.GetValue(opts.DryRun);
        var write = parseResult.GetValue(opts.Write);
        var verbose = parseResult.GetValue(opts.Verbose);

        var console = AnsiConsole.Console;

        if (dryRun && write)
        {
            console.MarkupLine("[red]--dry-run and --write are mutually exclusive.[/]");
            return ExitCodes.InvalidArguments;
        }

        TaggerOptions options;
        MappingRuleSet rules;
        try
        {
            (options, rules) = LoadConfiguration(configFile, source);
        }
        catch (ConfigurationException ex)
        {
            console.MarkupLine("[red]Configuration error:[/]");
            console.WriteLine(ex.Message);
            return ExitCodes.InvalidConfiguration;
        }

        ApplyCliOverrides(options, dryRun: dryRun, write: write);

        using var loggerFactory = SerilogSetup.Build(options.Logging, verboseOverride: verbose);

        // The composer registers everything that doesn't depend on the loaded options. The
        // remaining wiring (analyzers, providers) needs the options-derived bits and stays here.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging();
        services.AddRayTaggerServices();
        await using var serviceProvider = services.BuildServiceProvider();

        var runner = serviceProvider.GetRequiredService<NativeProcessRunner>();
        var probe = serviceProvider.GetRequiredService<IAnalysisToolProbe>();
        var resolver = NativeToolsBootstrapFactory.BuildResolver(options.NativeTools, probe, loggerFactory, console);
        var analysisRunner = await BuildAnalysisRunnerAsync(
            options.Analysis, runner, resolver, loggerFactory, console, cancellationToken).ConfigureAwait(false);

        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var dataDirs = serviceProvider.GetRequiredService<IUserDataDirectoryProvider>();
        var lookupRunner = BuildLookupRunner(options.Lookup, httpClientFactory, dataDirs, loggerFactory, console);

        var pipeline = new TagPipeline(
            serviceProvider.GetRequiredService<IFileDiscoveryService>(),
            serviceProvider.GetRequiredService<ITagReaderAdapter>(),
            serviceProvider.GetRequiredService<ITagWriterAdapter>(),
            analysisRunner,
            lookupRunner,
            serviceProvider.GetRequiredService<IMappingRuleEngine>(),
            serviceProvider.GetRequiredService<ISortService>(),
            loggerFactory.CreateLogger<TagPipeline>());

        var renderer = new OutcomeRenderer(console);

        console.MarkupLine($"[bold]Scanning[/] [cyan]{Markup.Escape(options.Scan.Source)}[/]" +
                          $"  [grey](dry-run={options.Write.DryRun})[/]");

        try
        {
            await foreach (var outcome in pipeline.RunAsync(options, rules, cancellationToken).ConfigureAwait(false))
            {
                renderer.Add(outcome);
            }
        }
        catch (OperationCanceledException)
        {
            console.MarkupLine("[yellow]Scan cancelled.[/]");
            return ExitCodes.GenericFailure;
        }

        renderer.Flush();

        return renderer.FailureCount > 0 ? ExitCodes.GenericFailure : ExitCodes.Success;
    }

    private static (TaggerOptions Options, MappingRuleSet Rules) LoadConfiguration(FileInfo? configFile, DirectoryInfo? sourceOverride)
    {
        var configPath = configFile?.FullName ?? Path.Combine(Environment.CurrentDirectory, "tagger.yaml");
        var options = TaggerOptionsLoader.Load(configPath);

        if (sourceOverride is not null)
        {
            options.Scan.Source = sourceOverride.FullName;
        }

        var rules = MappingRulesLoader.Load(options.Mapping.RulesFile);
        return (options, rules);
    }

    private static void ApplyCliOverrides(TaggerOptions options, bool dryRun, bool write)
    {
        if (dryRun)
        {
            options.Write.DryRun = true;
        }
        if (write)
        {
            options.Write.DryRun = false;
        }
    }

    /// <summary>
    /// Resolves each analyzer based on <c>analysis.&lt;dim&gt;.provider</c> in the config and
    /// probes the underlying native binary on PATH up-front. A missing binary disables its
    /// dimension (analyzer set to <c>null</c>, runner treats that as "skip") and is reported in
    /// the startup banner — far less confusing than discovering it per-file at scan time.
    /// </summary>
    /// <remarks>
    /// BPM, Key and Energy all run via Essentia's <c>streaming_extractor_music</c> against a
    /// single shared cache, so we probe Essentia once and instantiate up to three lightweight
    /// analyzers against the same service. The fingerprint dimension stays on its own binary
    /// (<c>fpcalc</c>/Chromaprint), since AcoustID requires it.
    /// </remarks>
    private static async Task<AnalysisRunner> BuildAnalysisRunnerAsync(
        AnalysisOptions analysis,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        var essentiaService = await TryBuildEssentiaServiceAsync(
            analysis, runner, resolver, loggerFactory, console, cancellationToken).ConfigureAwait(false);

        IBpmAnalyzer? bpm = essentiaService is not null
            && IsEssentiaProvider(analysis.Bpm)
                ? new EssentiaBpmAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaBpmAnalyzer>())
                : null;

        IKeyAnalyzer? key = essentiaService is not null
            && IsEssentiaProvider(analysis.Key)
                ? new EssentiaKeyAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaKeyAnalyzer>())
                : null;

        IEnergyAnalyzer? energy = essentiaService is not null
            && IsEssentiaProvider(analysis.Energy)
                ? new EssentiaEnergyAnalyzer(essentiaService, loggerFactory.CreateLogger<EssentiaEnergyAnalyzer>())
                : null;

        IFingerprintAnalyzer? fingerprint = await TryBuildFingerprintAsync(
            analysis.Fingerprint, runner, resolver, loggerFactory, console, cancellationToken)
            .ConfigureAwait(false);

        return new AnalysisRunner(bpm, key, energy, fingerprint,
            loggerFactory.CreateLogger<AnalysisRunner>());
    }

    private static async Task<EssentiaAnalysisService?> TryBuildEssentiaServiceAsync(
        AnalysisOptions analysis,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        var dimensionsNeedingEssentia = ListEssentiaDimensions(analysis);
        if (dimensionsNeedingEssentia.Count == 0)
        {
            return null;
        }

        var resolution = await resolver.ResolveAsync(EssentiaAnalysisService.Executable, cancellationToken).ConfigureAwait(false);
        if (resolution is null)
        {
            foreach (var dim in dimensionsNeedingEssentia)
            {
                ReportMissing(console, dim, EssentiaAnalysisService.ProviderName,
                    new ToolProbeResult(EssentiaAnalysisService.Executable, IsAvailable: false, Version: null,
                        ErrorMessage: "Binary not on PATH and auto-bootstrap could not provide it."));
            }
            return null;
        }

        // Use the longest configured timeout across BPM/Key/Energy — Essentia runs once per
        // track and the service can't know which dimension's threshold matters most.
        var timeoutSeconds = MaxEnabledEssentiaTimeout(analysis);
        var service = new EssentiaAnalysisService(
            runner,
            loggerFactory.CreateLogger<EssentiaAnalysisService>(),
            TimeSpan.FromSeconds(timeoutSeconds),
            executablePath: resolution.ExecutablePath);

        foreach (var dim in dimensionsNeedingEssentia)
        {
            ReportTool(console, dim, EssentiaAnalysisService.ProviderName, resolution);
        }

        return service;
    }

    private static async Task<IFingerprintAnalyzer?> TryBuildFingerprintAsync(
        AnalyzerOptions opts,
        NativeProcessRunner runner,
        NativeToolResolver resolver,
        ILoggerFactory loggerFactory,
        IAnsiConsole console,
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
            ReportMissing(console, "fingerprint", ChromaprintFingerprintAnalyzer.ProviderName,
                new ToolProbeResult(ChromaprintFingerprintAnalyzer.Executable, IsAvailable: false, Version: null,
                    ErrorMessage: "Binary not on PATH and auto-bootstrap could not provide it."));
            return null;
        }

        ReportTool(console, "fingerprint", ChromaprintFingerprintAnalyzer.ProviderName, resolution);

        return new ChromaprintFingerprintAnalyzer(
            runner,
            loggerFactory.CreateLogger<ChromaprintFingerprintAnalyzer>(),
            TimeSpan.FromSeconds(opts.TimeoutSeconds),
            executablePath: resolution.ExecutablePath);
    }

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

    private static void ReportTool(IAnsiConsole console, string dimension, string provider, NativeToolResolution resolution)
    {
        var version = string.IsNullOrWhiteSpace(resolution.Probe.Version) ? "(version unknown)" : resolution.Probe.Version;
        var sourceTag = resolution.Source switch
        {
            NativeToolResolutionSource.Path => "[grey](PATH)[/]",
            NativeToolResolutionSource.Cache => "[grey](cached)[/]",
            NativeToolResolutionSource.Downloaded => "[yellow](downloaded)[/]",
            _ => string.Empty,
        };
        console.MarkupLine(
            $"[green]✓[/] [bold]{dimension}[/] via [cyan]{Markup.Escape(provider)}[/] [grey]{Markup.Escape(version)}[/] {sourceTag}");
    }

    private static void ReportMissing(IAnsiConsole console, string dimension, string provider, ToolProbeResult probe)
    {
        var detail = string.IsNullOrWhiteSpace(probe.ErrorMessage) ? "not on PATH" : probe.ErrorMessage;
        console.MarkupLine(
            $"[yellow]✗[/] [bold]{dimension}[/] via [cyan]{Markup.Escape(provider)}[/] disabled — [grey]{Markup.Escape(detail)}[/]");
    }

    /// <summary>
    /// Builds the online-lookup chain from config. Only providers with usable credentials (or
    /// MusicBrainz, which is keyless) are instantiated. When <c>lookup.enabled = false</c> or no
    /// provider is configured, returns the no-op runner so the pipeline stays online-optional.
    /// </summary>
    /// <remarks>
    /// HttpClients now come from <see cref="IHttpClientFactory"/>; the
    /// <see cref="ServiceCollectionComposer"/> registers each provider's named client with a
    /// Polly resilience pipeline (retry, circuit-breaker, rate-limit-aware on 429/503 with
    /// Retry-After honoured). Factory-managed handlers get pooled across calls, fix the
    /// previous CA2000 dispose-leak concern, and prepare the lookup chain for Phase-6 watch
    /// mode where HttpClients would otherwise leak per scan.
    /// </remarks>
    private static ILookupRunner BuildLookupRunner(
        LookupOptions lookupOptions,
        IHttpClientFactory httpClientFactory,
        IUserDataDirectoryProvider dataDirs,
        ILoggerFactory loggerFactory,
        IAnsiConsole console)
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
            console.MarkupLine("[green]✓[/] [bold]lookup[/] via [cyan]acoustid[/]");
        }

        // MusicBrainz needs no API key, only a descriptive User-Agent. Always available.
        providers.Add(new MusicBrainzProvider(
            httpClientFactory.CreateClient(ServiceCollectionComposer.MusicBrainzHttpClient),
            loggerFactory.CreateLogger<MusicBrainzProvider>()));
        console.MarkupLine("[green]✓[/] [bold]lookup[/] via [cyan]musicbrainz[/]");

        if (!string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Discogs))
        {
            providers.Add(new DiscogsProvider(
                httpClientFactory.CreateClient(ServiceCollectionComposer.DiscogsHttpClient),
                lookupOptions.ApiKeys.Discogs,
                loggerFactory.CreateLogger<DiscogsProvider>()));
            console.MarkupLine("[green]✓[/] [bold]lookup[/] via [cyan]discogs[/]");
        }

        if (!string.IsNullOrWhiteSpace(lookupOptions.ApiKeys.Lastfm))
        {
            providers.Add(new LastFmProvider(
                httpClientFactory.CreateClient(ServiceCollectionComposer.LastFmHttpClient),
                lookupOptions.ApiKeys.Lastfm,
                loggerFactory.CreateLogger<LastFmProvider>()));
            console.MarkupLine("[green]✓[/] [bold]lookup[/] via [cyan]lastfm[/]");
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
}
