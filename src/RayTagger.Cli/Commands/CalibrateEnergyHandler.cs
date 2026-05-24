using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Cli.Hosting;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Hosting;
using Spectre.Console;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Handler for <c>tagger calibrate-energy &lt;folder&gt;</c>. Loads the config, builds the
/// calibration service via <see cref="PipelineFactory"/>, runs it with live Spectre progress,
/// and freezes the resulting profile to <c>analysis.energy.calibration_file</c> (or the
/// <c>--output</c> override). Re-tagging existing files is left to the user.
/// </summary>
internal static class CalibrateEnergyHandler
{
    public static async Task<int> RunAsync(
        ParseResult parseResult,
        CalibrateEnergyCommandOptions opts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(opts);

        var folderArg = parseResult.GetValue(opts.Folder);
        var configFile = parseResult.GetValue(opts.Config);
        var outputOverride = parseResult.GetValue(opts.Output);
        var verbose = parseResult.GetValue(opts.Verbose);

        var console = AnsiConsole.Console;

        if (folderArg is null || !folderArg.Exists)
        {
            console.MarkupLine($"[red]Folder not found:[/] {Markup.Escape(folderArg?.FullName ?? "(none)")}");
            return ExitCodes.InvalidArguments;
        }

        TaggerOptions options;
        try
        {
            var configPath = configFile?.FullName
                ?? ConfigPathDiscovery.Find(Environment.CurrentDirectory)
                ?? Path.Combine(Environment.CurrentDirectory, ConfigPathDiscovery.ConfigFileName);
            options = TaggerOptionsLoader.Load(configPath);
        }
        catch (ConfigurationException ex)
        {
            console.MarkupLine("[red]Configuration error:[/]");
            console.WriteLine(ex.Message);
            return ExitCodes.InvalidConfiguration;
        }

        Output.DeprecationRenderer.Render(console, options);

        var outputPath = outputOverride?.FullName
            ?? options.Analysis.Energy.CalibrationFile;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            console.MarkupLine(
                "[red]No output path:[/] analysis.energy.calibration_file is empty and no --output specified.");
            return ExitCodes.InvalidConfiguration;
        }

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => SerilogSetup.Build(options.Logging, verboseOverride: verbose));
        services.AddLogging();
        services.AddRayTaggerHosting();
        await using var serviceProvider = services.BuildServiceProvider();

        var statusReporter = new SpectreToolStatusReporter(console);
        var factory = serviceProvider.GetRequiredService<PipelineFactory>();

        var service = await factory.BuildEnergyCalibrationServiceAsync(
            options, statusReporter, cancellationToken).ConfigureAwait(false);

        if (service is null)
        {
            console.MarkupLine(
                "[red]Essentia is not available.[/] Run [yellow]tagger setup[/] to auto-install, or place " +
                "`essentia_streaming_extractor_music` on PATH (see docs/INSTALL.md).");
            return ExitCodes.GenericFailure;
        }

        var parallelism = options.Scan.EffectiveParallelism;
        console.MarkupLine($"[bold]Calibrating energy[/] from [cyan]{Markup.Escape(folderArg.FullName)}[/]");
        console.MarkupLine(
            $"Parallelism: [yellow]{parallelism}[/] (von scan.parallelism). " +
            $"Min. Samples: [yellow]{service.MinimumSampleCount}[/] — drunter bricht die Kalibrierung ohne Profil ab.");
        console.WriteLine();

        EnergyCalibrationReport report = null!;
        await console.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new SpinnerColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Scanning + analysing[/]", maxValue: 100);
                task.IsIndeterminate = true;

                var progress = new Progress<EnergyCalibrationProgress>(p =>
                {
                    task.Description = $"[green]Analysing[/] [grey]({p.TracksProcessed} done, {p.FailureCount} failed)[/]";
                    if (p.TracksTotal > 0)
                    {
                        task.IsIndeterminate = false;
                        task.MaxValue = p.TracksTotal;
                        task.Value = p.TracksProcessed;
                    }
                });

                report = await service.CalibrateAsync(folderArg.FullName, parallelism, progress, cancellationToken)
                    .ConfigureAwait(false);
                task.Value = task.MaxValue;
            }).ConfigureAwait(false);

        console.WriteLine();

        if (!report.Succeeded)
        {
            console.MarkupLine($"[red]Calibration aborted:[/] {Markup.Escape(report.AbortReason ?? "unknown reason")}");
            console.MarkupLine(
                $"[grey]{report.SuccessCount} analysed, {report.FailureCount} failed. " +
                "No profile written.[/]");
            return ExitCodes.GenericFailure;
        }

        var profile = report.Profile!;

        // Surface previous profile so the user can see the delta. Best-effort: a missing/malformed
        // previous file is silently ignored, the new profile still gets written.
        EnergyCalibrationProfile? previous = null;
        if (File.Exists(outputPath))
        {
            try
            {
                previous = EnergyCalibrationProfileLoader.Load(outputPath);
            }
            catch (ConfigurationException) { /* old file unreadable; just overwrite */ }
        }

        RenderAnchors(console, profile, previous);

        try
        {
            EnergyCalibrationProfileLoader.Save(outputPath, profile);
        }
        catch (IOException ex)
        {
            console.MarkupLine($"[red]Failed to write profile to {Markup.Escape(outputPath)}:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GenericFailure;
        }

        console.MarkupLine($"[green]Profile written to[/] [cyan]{Markup.Escape(outputPath)}[/]");
        console.MarkupLine(
            $"[grey]{profile.SampleCount} tracks contributed, {report.FailureCount} failed.[/]");
        console.MarkupLine(
            "[yellow]Re-tag your library to apply the new buckets:[/] [white]tagger scan --write[/]");

        return ExitCodes.Success;
    }

    private static void RenderAnchors(IAnsiConsole console, EnergyCalibrationProfile current, EnergyCalibrationProfile? previous)
    {
        var table = new Table().AddColumn("Feature").AddColumn("Floor").AddColumn("Ceiling");
        if (previous is not null)
        {
            table.AddColumn("Prev floor").AddColumn("Prev ceiling");
        }

        Row("spectral_flux",    current.SpectralFlux,    previous?.SpectralFlux);
        Row("beats_loudness",   current.BeatsLoudness,   previous?.BeatsLoudness);
        Row("onset_rate",       current.OnsetRate,       previous?.OnsetRate);
        Row("danceability",     current.Danceability,    previous?.Danceability);
        Row("average_loudness", current.AverageLoudness, previous?.AverageLoudness);

        console.Write(table);

        void Row(string name, FeatureAnchor now, FeatureAnchor? prev)
        {
            if (prev is null)
            {
                table.AddRow(name, $"{now.Floor:F4}", $"{now.Ceiling:F4}");
            }
            else
            {
                table.AddRow(name, $"{now.Floor:F4}", $"{now.Ceiling:F4}",
                    $"{prev.Floor:F4}", $"{prev.Ceiling:F4}");
            }
        }
    }
}

