using System.CommandLine;
using RayTagger.Cli.Hosting;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Metadata;
using Spectre.Console;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Implements the <c>explain</c> verb: reads tags off one file, walks the ruleset, and prints
/// which rules would fire and which fields they would touch. No analysis, no lookup, no writes —
/// rule debugging only. The user gets a table they can scan to debug a mappings.yaml change.
/// </summary>
internal static class ExplainHandler
{
    public static async Task<int> RunAsync(ParseResult parseResult, ExplainCommandOptions opts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(opts);

        var fileArg = parseResult.GetValue(opts.File);
        var configFile = parseResult.GetValue(opts.Config);
        var verbose = parseResult.GetValue(opts.Verbose);

        var console = AnsiConsole.Console;

        if (fileArg is null || !fileArg.Exists)
        {
            console.MarkupLine("[red]File not found:[/] " + Markup.Escape(fileArg?.FullName ?? "<missing>"));
            return ExitCodes.InvalidArguments;
        }

        TaggerOptions options;
        MappingRuleSet rules;
        try
        {
            var configPath = configFile?.FullName
                ?? ConfigPathDiscovery.Find(Environment.CurrentDirectory)
                ?? Path.Combine(Environment.CurrentDirectory, ConfigPathDiscovery.ConfigFileName);
            options = TaggerOptionsLoader.Load(configPath);
            // Pass the loaded taxonomy so the rules loader enforces allowlists and the engine
            // can resolve normalise_genre aliases below. Otherwise `explain` would silently
            // diverge from `scan`.
            rules = MappingRulesLoader.Load(options.Mapping.RulesFile, options.Taxonomy.Loaded);
        }
        catch (ConfigurationException ex)
        {
            console.MarkupLine("[red]Configuration error:[/]");
            console.WriteLine(ex.Message);
            return ExitCodes.InvalidConfiguration;
        }

        Output.DeprecationRenderer.Render(console, options);

        using var loggerFactory = SerilogSetup.Build(options.Logging, verboseOverride: verbose);

        var reader = new TagLibTagReader();
        TrackTags existing;
        try
        {
            existing = reader.Read(fileArg.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            console.MarkupLine("[red]Read failed:[/] " + Markup.Escape(ex.Message));
            return ExitCodes.GenericFailure;
        }

        var format = AudioFormatDetector.TryDetect(fileArg.FullName) ?? AudioFormat.Mp3;
        var trackFile = new TrackFile(fileArg.FullName, format, fileArg.Length, fileArg.LastWriteTimeUtc);

        // Map-stage only — no analysis, so synthesize an "Existing"-sourced ResolvedTrackTags from
        // the disk values. This is exactly what the user wants to see: "given these existing tags,
        // which rules fire?". Mixing in analysis would confuse the debugging story.
        var resolved = ResolvedFromExistingBuilder.Build(existing);

        var engine = new MappingRuleEngine();
        var result = engine.Evaluate(resolved, existing, trackFile, rules, options.Taxonomy.Loaded);

        RenderReport(console, fileArg, existing, result);

        await Task.CompletedTask.ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static void RenderReport(IAnsiConsole console, FileInfo file, TrackTags existing, MappingEvaluationResult result)
    {
        console.MarkupLine($"[bold]explain[/]  [cyan]{Markup.Escape(file.FullName)}[/]");

        var inputs = new Table().NoBorder();
        inputs.AddColumn("Field");
        inputs.AddColumn("Existing value");
        inputs.AddRow("artist", Markup.Escape(existing.Artist ?? "—"));
        inputs.AddRow("genre", Markup.Escape(existing.Genre ?? "—"));
        inputs.AddRow("subgenre", Markup.Escape(existing.SubGenre ?? "—"));
        inputs.AddRow("bpm", existing.Bpm?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        inputs.AddRow("key", Markup.Escape(existing.Key?.Standard ?? "—"));
        inputs.AddRow("energy", existing.Energy?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        inputs.AddRow("mood", Markup.Escape(existing.Mood ?? "—"));
        inputs.AddRow("set_position", Markup.Escape(existing.SetPosition ?? "—"));
        console.Write(inputs);

        if (result.Applied.Count == 0)
        {
            console.MarkupLine("[yellow]No rules matched.[/]");
            return;
        }

        var matches = new Table().Title("Matched rules");
        matches.AddColumn("#");
        matches.AddColumn("Rule");
        matches.AddColumn("Changed fields");
        for (var i = 0; i < result.Applied.Count; i++)
        {
            var hit = result.Applied[i];
            matches.AddRow(
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Markup.Escape(hit.RuleName),
                Markup.Escape(string.Join(", ", hit.ChangedFields)));
        }
        console.Write(matches);

        console.MarkupLine("[bold]Final values after rules[/]");
        var final = new Table().NoBorder();
        final.AddColumn("Field");
        final.AddColumn("Value");
        final.AddColumn("Source");
        final.AddRow("genre", Markup.Escape(result.Tags.Genre.Value ?? "—"), result.Tags.Genre.Source.ToString());
        final.AddRow("subgenre", Markup.Escape(result.Tags.SubGenre.Value ?? "—"), result.Tags.SubGenre.Source.ToString());
        final.AddRow("bpm",
            result.Tags.Bpm.Value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "—",
            result.Tags.Bpm.Source.ToString());
        final.AddRow("mood", Markup.Escape(result.Tags.Mood.Value ?? "—"), result.Tags.Mood.Source.ToString());
        final.AddRow("set_position",
            Markup.Escape(result.Tags.SetPosition.Value ?? "—"),
            result.Tags.SetPosition.Source.ToString());
        foreach (var (key, field) in result.Tags.Custom)
        {
            final.AddRow($"tag.{key}", Markup.Escape(field.Value ?? "—"), field.Source.ToString());
        }
        console.Write(final);
    }
}
