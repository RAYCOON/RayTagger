using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Analysis.Internal;
using RayTagger.Cli.Hosting;
using RayTagger.Core.Configuration;
using RayTagger.Hosting;
using Spectre.Console;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Implements the <c>setup</c> verb. Walks every tool declared in <c>native-tools.yaml</c> and
/// makes sure each one is available locally, downloading from the manifest URLs when necessary.
/// Designed to be run once after install (or in CI / offline-prep scripts) so subsequent scans
/// never block on a download.
/// </summary>
internal static class SetupHandler
{
    public static async Task<int> RunAsync(ParseResult parseResult, SetupCommandOptions opts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(opts);

        var configFile = parseResult.GetValue(opts.Config);
        var force = parseResult.GetValue(opts.Force);
        var verbose = parseResult.GetValue(opts.Verbose);

        var console = AnsiConsole.Console;

        TaggerOptions options;
        try
        {
            options = LoadOptions(configFile);
        }
        catch (ConfigurationException ex)
        {
            console.MarkupLine("[red]Configuration error:[/]");
            console.WriteLine(ex.Message);
            return ExitCodes.InvalidConfiguration;
        }

        // Same DI pattern as ScanHandler: container owns the Serilog factory, and pools the
        // bootstrap HttpClient through IHttpClientFactory.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => SerilogSetup.Build(options.Logging, verboseOverride: verbose));
        services.AddLogging();
        services.AddRayTaggerHosting();
        await using var serviceProvider = services.BuildServiceProvider();

        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var bootstrapHttp = httpClientFactory.CreateClient(ServiceCollectionComposer.NativeToolsBootstrapHttpClient);

        var statusReporter = new SpectreToolStatusReporter(console);
        var bootstrapper = NativeToolsBootstrapFactory.BuildBootstrapper(
            options.NativeTools, bootstrapHttp, loggerFactory, statusReporter);
        if (bootstrapper is null)
        {
            console.MarkupLine("[red]Cannot run setup without a usable native-tools manifest. Copy samples/native-tools.example.yaml next to tagger.yaml and fill in URLs + SHA-256 hashes.[/]");
            return ExitCodes.InvalidConfiguration;
        }

        if (force)
        {
            ClearCacheForKnownTools(bootstrapper, console);
        }

        var probe = new AnalysisToolProbe(serviceProvider.GetRequiredService<NativeProcessRunner>());

        var failures = 0;
        foreach (var tool in bootstrapper.KnownTools)
        {
            console.MarkupLine($"[bold]→ {Markup.Escape(tool)}[/]");
            try
            {
                var pathProbe = await probe.ProbeAsync(tool, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (pathProbe.IsAvailable && !force)
                {
                    console.MarkupLine($"  [green]✓ already on PATH[/] [grey]{Markup.Escape(pathProbe.Version ?? "(version unknown)")}[/]");
                    continue;
                }

                var path = await bootstrapper.EnsureAsync(tool, cancellationToken).ConfigureAwait(false);
                console.MarkupLine($"  [green]✓ ready[/] [grey]{Markup.Escape(path)}[/]");
            }
            catch (NativeToolBootstrapException ex)
            {
                console.MarkupLine($"  [red]✗ {Markup.Escape(ex.Message)}[/]");
                failures++;
            }
            catch (OperationCanceledException)
            {
                console.MarkupLine("[yellow]Setup cancelled.[/]");
                return ExitCodes.GenericFailure;
            }
        }

        if (failures > 0)
        {
            console.MarkupLine($"[yellow]{failures} tool(s) could not be bootstrapped. Tagger will run with those dimensions disabled.[/]");
            return ExitCodes.GenericFailure;
        }

        console.MarkupLine("[green]All native tools are ready.[/]");
        return ExitCodes.Success;
    }

    private static TaggerOptions LoadOptions(FileInfo? configFile)
    {
        var configPath = configFile?.FullName ?? Path.Combine(Environment.CurrentDirectory, "tagger.yaml");
        return TaggerOptionsLoader.Load(configPath);
    }

    private static void ClearCacheForKnownTools(INativeToolBootstrapper bootstrapper, IAnsiConsole console)
    {
        foreach (var tool in bootstrapper.KnownTools)
        {
            var cached = bootstrapper.TryResolveCached(tool);
            if (cached is null)
            {
                continue;
            }

            try
            {
                File.Delete(cached);
                console.MarkupLine($"  [grey]cleared cached {Markup.Escape(cached)}[/]");
            }
            catch (IOException ex)
            {
                console.MarkupLine($"  [yellow]could not clear {Markup.Escape(cached)}: {Markup.Escape(ex.Message)}[/]");
            }
            catch (UnauthorizedAccessException ex)
            {
                console.MarkupLine($"  [yellow]could not clear {Markup.Escape(cached)}: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }
}
