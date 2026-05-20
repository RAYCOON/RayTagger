using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Cli.Hosting;
using RayTagger.Cli.Output;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Pipeline;
using RayTagger.Hosting;
using Spectre.Console;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Implements the <c>scan</c> verb. Loads config, applies CLI overrides, builds the pipeline via
/// the shared <see cref="PipelineFactory"/>, runs it, and renders outcomes. Returns a process
/// exit code reflecting per-file failure totals.
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

        var services = new ServiceCollection();
        // Register the Serilog-backed factory via delegate so the DI container owns its lifetime
        // (and the disposal that goes with it). Hand-rolling `using var` here on top of the same
        // registration disposes the factory twice with newer Microsoft.Extensions.DI builds.
        services.AddSingleton<ILoggerFactory>(_ => SerilogSetup.Build(options.Logging, verboseOverride: verbose));
        services.AddLogging();
        services.AddRayTaggerHosting();
        await using var serviceProvider = services.BuildServiceProvider();

        var statusReporter = new SpectreToolStatusReporter(console);
        var factory = serviceProvider.GetRequiredService<PipelineFactory>();
        var built = await factory.BuildAsync(options, statusReporter, cancellationToken).ConfigureAwait(false);

        var pipeline = new TagPipeline(
            serviceProvider.GetRequiredService<IFileDiscoveryService>(),
            serviceProvider.GetRequiredService<ITagReaderAdapter>(),
            serviceProvider.GetRequiredService<ITagWriterAdapter>(),
            built.AnalysisRunner,
            built.LookupRunner,
            serviceProvider.GetRequiredService<IMappingRuleEngine>(),
            serviceProvider.GetRequiredService<ISortService>(),
            serviceProvider.GetRequiredService<ILogger<TagPipeline>>());

        var renderer = new OutcomeRenderer(console);

        console.MarkupLine($"[bold]Scanning[/] [cyan]{Markup.Escape(options.Scan.Source)}[/]" +
                          $"  [grey](dry-run={options.Write.DryRun})[/]");

        try
        {
            await foreach (var outcome in pipeline.RunAsync(options, rules, cancellationToken: cancellationToken).ConfigureAwait(false))
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

        // Pass the loaded taxonomy so the rules loader can enforce allowlists at load time
        // (when `taxonomy.enforce: true`) — a typo'd mood becomes a config error, not a silent
        // rogue value polluting the library.
        var rules = MappingRulesLoader.Load(options.Mapping.RulesFile, options.Taxonomy.Loaded);
        return (options, rules);
    }

    private static void ApplyCliOverrides(TaggerOptions options, bool dryRun, bool write)
    {
        if (dryRun) options.Write.DryRun = true;
        if (write) options.Write.DryRun = false;
    }
}
