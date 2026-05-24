using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Cli.Hosting;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Core.Validation;
using RayTagger.Hosting;
using RayTagger.Metadata;
using Spectre.Console;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Handler for <c>tagger validate &lt;reference&gt;</c>. Runs the full pipeline read-only against
/// a reference library tagged with Mixed-In-Key and reports per-dimension accuracy.
/// Genre/SubGenre truth comes from folder structure; BPM/Key/Energy truth from the comment-tag.
/// </summary>
internal static class ValidateHandler
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(
        ParseResult parseResult,
        ValidateCommandOptions opts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(opts);

        var referenceArg = parseResult.GetValue(opts.Reference);
        var configFile = parseResult.GetValue(opts.Config);
        var outputOverride = parseResult.GetValue(opts.Output);
        var take = parseResult.GetValue(opts.Take);
        var referenceVdjArg = parseResult.GetValue(opts.ReferenceVdj);
        var verbose = parseResult.GetValue(opts.Verbose);

        var console = AnsiConsole.Console;

        if (referenceArg is null || !referenceArg.Exists)
        {
            console.MarkupLine(
                $"[red]Reference folder not found:[/] {Markup.Escape(referenceArg?.FullName ?? "(none)")}");
            return ExitCodes.InvalidArguments;
        }

        if (referenceVdjArg is not null && !referenceVdjArg.Exists)
        {
            console.MarkupLine(
                $"[red]Secondary VDJ reference folder not found:[/] {Markup.Escape(referenceVdjArg.FullName)}");
            return ExitCodes.InvalidArguments;
        }

        TaggerOptions options;
        MappingRuleSet rules;
        try
        {
            (options, rules) = LoadConfiguration(configFile);
        }
        catch (ConfigurationException ex)
        {
            console.MarkupLine("[red]Configuration error:[/]");
            console.WriteLine(ex.Message);
            return ExitCodes.InvalidConfiguration;
        }

        Output.DeprecationRenderer.Render(console, options);

        // The backtest is dry-run by definition — never overwrite the reference library. Also
        // point the scan source at the reference folder so the pipeline enumerates from there.
        options.Scan.Source = referenceArg.FullName;
        options.Write.DryRun = true;
        // Force per-dimension existing_confidence = 0 so the merger surfaces Analyzer/Lookup
        // outputs even when the reference files already carry an existing tag. Without this,
        // Mixed-In-Key's existing BPM/Key/Energy would pass through verbatim and the backtest
        // would always report 100 % "match" — measuring nothing about analyzer accuracy.
        // DryRun stays true so the reference library is never written to.
        options.ForceOverwriteExistingTags();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => SerilogSetup.Build(options.Logging, verboseOverride: verbose));
        services.AddLogging();
        services.AddRayTaggerHosting();
        await using var serviceProvider = services.BuildServiceProvider();

        var statusReporter = new SpectreToolStatusReporter(console);
        var factory = serviceProvider.GetRequiredService<PipelineFactory>();
        using var built = await factory.BuildAsync(options, statusReporter, cancellationToken).ConfigureAwait(false);

        var pipeline = new TagPipeline(
            serviceProvider.GetRequiredService<IFileDiscoveryService>(),
            serviceProvider.GetRequiredService<ITagReaderAdapter>(),
            serviceProvider.GetRequiredService<ITagWriterAdapter>(),
            built.AnalysisRunner,
            built.LookupRunner,
            built.ClassifierRunner,
            serviceProvider.GetRequiredService<IMappingRuleEngine>(),
            serviceProvider.GetRequiredService<ISortService>(),
            serviceProvider.GetRequiredService<TaxonomyGenreResolver>(),
            serviceProvider.GetRequiredService<ILogger<TagPipeline>>());

        console.MarkupLine(
            $"[bold]Validating[/] [cyan]{Markup.Escape(referenceArg.FullName)}[/] " +
            $"[grey](dry-run, read-only)[/]");

        var fileResults = new List<BacktestFileResult>();
        var processed = 0;
        await console.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new SpinnerColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Comparing[/]", maxValue: 100);
                task.IsIndeterminate = true;

                try
                {
                    await foreach (var outcome in pipeline.RunAsync(options, rules, cancellationToken: cancellationToken)
                                       .ConfigureAwait(false))
                    {
                        if (take > 0 && processed >= take) break;
                        processed++;

                        var fileResult = Compare(referenceArg.FullName, referenceVdjArg?.FullName, outcome);
                        fileResults.Add(fileResult);

                        task.Description = $"[green]Comparing[/] [grey]({processed} files done)[/]";
                        task.IsIndeterminate = false;
                        task.MaxValue = Math.Max(processed, 100);
                        task.Value = processed;
                    }
                }
                catch (OperationCanceledException)
                {
                    console.MarkupLine("[yellow]Validation cancelled — partial results below.[/]");
                }

                task.Value = task.MaxValue;
            }).ConfigureAwait(false);

        console.WriteLine();

        if (fileResults.Count == 0)
        {
            console.MarkupLine("[yellow]No files matched the reference library.[/]");
            return ExitCodes.GenericFailure;
        }

        var report = BacktestReportAggregator.Build(referenceArg.FullName, fileResults, DateTime.UtcNow);
        RenderSummary(console, report);

        var outputPath = outputOverride?.FullName
            ?? Path.Combine(
                Environment.CurrentDirectory,
                "reports",
                $"backtest-{report.GeneratedAt:yyyyMMdd-HHmmss}.json");

        try
        {
            WriteReport(outputPath, report);
            console.MarkupLine($"[green]JSON report:[/] [cyan]{Markup.Escape(outputPath)}[/]");
            console.MarkupLine($"[green]Markdown summary:[/] [cyan]{Markup.Escape(Path.ChangeExtension(outputPath, ".md"))}[/]");
        }
        catch (IOException ex)
        {
            console.MarkupLine($"[red]Failed to write report:[/] {Markup.Escape(ex.Message)}");
            return ExitCodes.GenericFailure;
        }

        return ExitCodes.Success;
    }

    private static BacktestFileResult Compare(string referenceRoot, string? secondaryRoot, PipelineOutcome outcome)
    {
        var comment = CommentTagReader.Read(outcome.File.Path);
        var truth = BacktestTruthExtractor.Extract(referenceRoot, outcome.File.Path, comment);

        // Resolve secondary-root truth if --reference-vdj was supplied. The parallel file sits at
        // the same relative path under the secondary root; we read TBPM and TKEY frames directly
        // (VDJ doesn't use the MIK comment format). Standard-notation TKEY values like "Ebm" get
        // normalised to Camelot here so enharmonic spellings collapse onto the same wheel slot.
        if (secondaryRoot is not null)
        {
            var secondaryPath = ResolveSecondaryPath(referenceRoot, secondaryRoot, outcome.File.Path);
            if (secondaryPath is not null && File.Exists(secondaryPath))
            {
                var (secBpm, secKeyRaw) = CommentTagReader.ReadBpmKey(secondaryPath);
                var secCamelot = string.IsNullOrWhiteSpace(secKeyRaw)
                    ? null
                    : RayTagger.Core.Models.KeyNotationConverter.FromEither(secKeyRaw, null)?.Camelot;
                truth = truth with { SecondaryBpm = secBpm, SecondaryCamelotKey = secCamelot };
            }
        }

        var resolved = outcome.Resolved;

        var error = outcome.Errors.Count > 0
            ? string.Join("; ", outcome.Errors.Select(e => $"{e.Stage}: {e.Message}"))
            : null;

        // The taxonomy resolver appends one trace entry per inspected candidate, with the
        // matched candidate the last item when ChosenGenre is non-null. We surface the
        // winning candidate's source verbatim so the aggregator can tally per-provider /
        // per-classifier win-rates against the truth genre.
        string? winningSource = null;
        var trace = resolved.GenreLookupTrace;
        if (trace is { Count: > 0 })
        {
            var last = trace[^1];
            if (!string.IsNullOrEmpty(last.ApiSource))
            {
                winningSource = last.ApiSource;
            }
        }

        var bpmPrimary = BacktestMetrics.CompareBpm(truth.Bpm, resolved.Bpm.Value, resolved.Bpm.Source);
        var bpmSecondary = truth.SecondaryBpm is not null
            ? BacktestMetrics.CompareBpm(truth.SecondaryBpm, resolved.Bpm.Value, resolved.Bpm.Source)
            : null;
        var (bpmCombined, bpmMatchedBy) = BacktestMetrics.PromoteWithSecondary(bpmPrimary, bpmSecondary);

        var keyPrimary = BacktestMetrics.CompareKey(truth.CamelotKey, resolved.Key.Value, resolved.Key.Source);
        var keySecondary = truth.SecondaryCamelotKey is not null
            ? BacktestMetrics.CompareKey(truth.SecondaryCamelotKey, resolved.Key.Value, resolved.Key.Source)
            : null;
        var (keyCombined, keyMatchedBy) = BacktestMetrics.PromoteWithSecondary(keyPrimary, keySecondary);

        return new BacktestFileResult(
            Truth: truth,
            Genre: BacktestMetrics.CompareGenre(truth.Genre, resolved.Genre.Value, resolved.Genre.Source),
            SubGenre: BacktestMetrics.CompareSubGenre(truth.SubGenre, resolved.SubGenre.Value, resolved.SubGenre.Source),
            Bpm: bpmCombined,
            Key: keyCombined,
            Energy: BacktestMetrics.CompareEnergy(truth.Energy, resolved.Energy.Value, resolved.Energy.Source),
            Error: error,
            GenreLookupTrace: trace,
            WinningGenreSource: winningSource,
            ClassifierTrace: resolved.ClassifierTrace)
        {
            BpmTruthMatch = bpmMatchedBy,
            KeyTruthMatch = keyMatchedBy,
        };
    }

    /// <summary>
    /// Maps a primary-root file path to its sibling under the secondary root by replacing the
    /// reference-root prefix. Returns null when the primary path does not actually sit under the
    /// primary root (defensive — the pipeline always enumerates from there, so this should not
    /// fire in practice).
    /// </summary>
    private static string? ResolveSecondaryPath(string primaryRoot, string secondaryRoot, string primaryFilePath)
    {
        var normPrim = Path.GetFullPath(primaryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normFile = Path.GetFullPath(primaryFilePath);
        if (!normFile.StartsWith(normPrim + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }
        var relative = normFile[(normPrim.Length + 1)..];
        return Path.Combine(Path.GetFullPath(secondaryRoot), relative);
    }

    private static (TaggerOptions Options, MappingRuleSet Rules) LoadConfiguration(FileInfo? configFile)
    {
        var configPath = configFile?.FullName
            ?? ConfigPathDiscovery.Find(Environment.CurrentDirectory)
            ?? Path.Combine(Environment.CurrentDirectory, ConfigPathDiscovery.ConfigFileName);
        var options = TaggerOptionsLoader.Load(configPath);
        var rules = MappingRulesLoader.Load(options.Mapping.RulesFile, options.Taxonomy.Loaded);
        return (options, rules);
    }

    private static void RenderSummary(IAnsiConsole console, BacktestReport report)
    {
        // Show the truth-match column when secondary-truth was actually used. Detect that by
        // looking at any dimension's total of "matched by anyone" counters — they're all zero
        // when --reference-vdj wasn't supplied.
        var hasSecondary = report.BpmMetrics.PrimaryOnlyMatches + report.BpmMetrics.SecondaryOnlyMatches + report.BpmMetrics.BothMatches > 0
                        || report.KeyMetrics.PrimaryOnlyMatches + report.KeyMetrics.SecondaryOnlyMatches + report.KeyMetrics.BothMatches > 0;

        var table = new Table()
            .AddColumn("Dimension")
            .AddColumn("Total")
            .AddColumn("Evaluable")
            .AddColumn("Match")
            .AddColumn("Tol")
            .AddColumn("Miss")
            .AddColumn("NoPred")
            .AddColumn("Exact %")
            .AddColumn("Tol %")
            .AddColumn("Sources");
        if (hasSecondary)
        {
            table.AddColumn("MIK-only").AddColumn("VDJ-only").AddColumn("Both");
        }

        Row("Genre", report.GenreMetrics);
        Row("SubGenre", report.SubGenreMetrics);
        Row("BPM", report.BpmMetrics);
        Row("Key", report.KeyMetrics);
        Row("Energy", report.EnergyMetrics);

        console.Write(table);

        void Row(string name, DimensionMetrics m)
        {
            var cells = new List<string>
            {
                name,
                m.Total.ToString(),
                m.Evaluable.ToString(),
                m.Matches.ToString(),
                m.ToleranceMatches.ToString(),
                m.Mismatches.ToString(),
                m.NoPrediction.ToString(),
                $"{m.ExactMatchRate * 100:F1}",
                $"{m.ToleranceMatchRate * 100:F1}",
                FormatSources(m.SourceCounts),
            };
            if (hasSecondary)
            {
                // Only BPM and Key rows carry truth-match counts; other rows get blanks so the
                // table grid stays aligned without misleading zeros.
                var supports = name is "BPM" or "Key";
                cells.Add(supports ? m.PrimaryOnlyMatches.ToString() : "—");
                cells.Add(supports ? m.SecondaryOnlyMatches.ToString() : "—");
                cells.Add(supports ? m.BothMatches.ToString() : "—");
            }
            table.AddRow([.. cells]);
        }
    }

    private static string FormatSources(IReadOnlyDictionary<RayTagger.Core.Models.TagFieldSource, int> counts)
    {
        if (counts.Count == 0) return "—";
        // Stable order: Analysis, Lookup, Rules, Existing — most important first.
        return string.Join(", ",
            new[]
            {
                RayTagger.Core.Models.TagFieldSource.Analysis,
                RayTagger.Core.Models.TagFieldSource.Lookup,
                RayTagger.Core.Models.TagFieldSource.Rules,
                RayTagger.Core.Models.TagFieldSource.Existing,
            }
            .Where(s => counts.ContainsKey(s) && counts[s] > 0)
            .Select(s => $"{s}={counts[s]}"));
    }

    private static void WriteReport(string jsonPath, BacktestReport report)
    {
        var dir = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(report, ReportJsonOptions);
        File.WriteAllText(jsonPath, json);

        var mdPath = Path.ChangeExtension(jsonPath, ".md");
        File.WriteAllText(mdPath, RenderMarkdown(report));
    }

    private static string RenderMarkdown(BacktestReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Backtest Report — {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine($"Reference: `{report.ReferenceRoot}`  ");
        sb.AppendLine($"Files: **{report.Files.Count}**");
        sb.AppendLine();
        var hasSecondary = report.BpmMetrics.PrimaryOnlyMatches + report.BpmMetrics.SecondaryOnlyMatches + report.BpmMetrics.BothMatches > 0
                        || report.KeyMetrics.PrimaryOnlyMatches + report.KeyMetrics.SecondaryOnlyMatches + report.KeyMetrics.BothMatches > 0;

        sb.AppendLine("## Per-Dimension Metrics");
        sb.AppendLine();
        if (hasSecondary)
        {
            sb.AppendLine("| Dimension | Total | Evaluable | Match | Tol | Miss | NoPred | Exact % | Tol % | Sources | MIK-only | VDJ-only | Both |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        }
        else
        {
            sb.AppendLine("| Dimension | Total | Evaluable | Match | Tol | Miss | NoPred | Exact % | Tol % | Sources |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        }
        AppendMetric(sb, "Genre", report.GenreMetrics, hasSecondary, supportsTruthSplit: false);
        AppendMetric(sb, "SubGenre", report.SubGenreMetrics, hasSecondary, supportsTruthSplit: false);
        AppendMetric(sb, "BPM", report.BpmMetrics, hasSecondary, supportsTruthSplit: true);
        AppendMetric(sb, "Key", report.KeyMetrics, hasSecondary, supportsTruthSplit: true);
        AppendMetric(sb, "Energy", report.EnergyMetrics, hasSecondary, supportsTruthSplit: false);
        sb.AppendLine();
        sb.AppendLine("> **Sources column**: which pipeline stage produced the prediction.");
        sb.AppendLine("> `Analysis` = audio analyzer (Essentia BPM/Key/Energy or heuristic), `Lookup` = online provider, ");
        sb.AppendLine("> `Rules` = mapping rule `set:` block, `Existing` = tag was already on file. The backtest forces");
        sb.AppendLine("> every `existing_confidence` to 0 so most rows should NOT be `Existing` — when they are,");
        sb.AppendLine("> the analyzer/lookup stage didn't produce anything and the existing tag passed through unmasked.");
        if (hasSecondary)
        {
            sb.AppendLine("> **MIK-only / VDJ-only / Both** (BPM and Key rows only): how each match was reached.");
            sb.AppendLine("> Track is counted as match if EITHER MIK or VDJ truth agrees with the prediction (OR-rule).");
            sb.AppendLine("> `MIK-only` = MIK matched, VDJ didn't · `VDJ-only` = VDJ rescued · `Both` = unanimous.");
        }
        sb.AppendLine();
        sb.AppendLine("## Genre Confusion (per truth-genre)");
        sb.AppendLine();
        sb.AppendLine("| Truth Genre | Total | Match | Mismatch | NoPred | Match % | Top mispredictions |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (genre, c) in report.GenreConfusion.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rate = c.Total == 0 ? 0 : (double)c.Matches / c.Total * 100;
            var truthFolded = BacktestMetrics.NormaliseForCompare(genre);
            var top = c.Predicted
                .Where(p => BacktestMetrics.NormaliseForCompare(p.Key) != truthFolded)
                .OrderByDescending(p => p.Value)
                .Take(3)
                .Select(p => $"{p.Key} ({p.Value})");
            sb.AppendLine($"| {genre} | {c.Total} | {c.Matches} | {c.Mismatches} | {c.NoPrediction} | {rate:F1} | {string.Join(", ", top)} |");
        }
        sb.AppendLine();
        AppendTraceSection(sb, report.Trace);
        AppendClassifierThresholdsSection(sb, report.ClassifierThresholds);
        AppendBpmDistributionSection(sb, report.BpmByGenre);
        return sb.ToString();

        static void AppendMetric(System.Text.StringBuilder sb, string name, DimensionMetrics m, bool hasSecondary, bool supportsTruthSplit)
        {
            var core =
                $"| {name} | {m.Total} | {m.Evaluable} | {m.Matches} | {m.ToleranceMatches} | " +
                $"{m.Mismatches} | {m.NoPrediction} | {m.ExactMatchRate * 100:F1} | " +
                $"{m.ToleranceMatchRate * 100:F1} | {FormatSources(m.SourceCounts)} |";
            if (!hasSecondary)
            {
                sb.AppendLine(core);
                return;
            }
            var primary = supportsTruthSplit ? m.PrimaryOnlyMatches.ToString() : "—";
            var secondary = supportsTruthSplit ? m.SecondaryOnlyMatches.ToString() : "—";
            var both = supportsTruthSplit ? m.BothMatches.ToString() : "—";
            sb.AppendLine($"{core} {primary} | {secondary} | {both} |");
        }
    }

    /// <summary>
    /// Writes the resolver-trace aggregation: who-wins-when per candidate source, plus
    /// resolver-side fallback counts. Skips the section when nothing was traced (e.g. lookup
    /// and classifiers fully disabled — there's no signal to report on).
    /// </summary>
    private static void AppendTraceSection(System.Text.StringBuilder sb, TraceAggregate trace)
    {
        if (trace.BySource.Count == 0 && trace.ResolverCleanMatchCount == 0)
        {
            return;
        }

        sb.AppendLine("## Resolver Trace Aggregation");
        sb.AppendLine();
        sb.AppendLine("Resolver behaviour across all tracks:");
        sb.AppendLine();
        sb.AppendLine($"- **Clean taxonomy match**: {trace.ResolverCleanMatchCount} tracks");
        sb.AppendLine($"- **Fallback (no taxonomy match, top-1 used)**: {trace.ResolverFallbackCount} tracks");
        sb.AppendLine($"- **TF aggregated-fallback (diffuse output)**: {trace.ResolverAggregatedFallbackCount} tracks");
        sb.AppendLine();

        if (trace.BySource.Count > 0)
        {
            sb.AppendLine("### Per-source win/loss against truth genre");
            sb.AppendLine();
            sb.AppendLine("> `Win` = the resolver chose this source's candidate AND the genre matched truth. ");
            sb.AppendLine("> `Loss` = the resolver chose this source's candidate but the genre disagreed with truth. ");
            sb.AppendLine("> Tracks with no winning candidate (existing-tag passthrough, no candidates) are not counted.");
            sb.AppendLine();
            sb.AppendLine("| Source | Wins | Losses | Total | Win % |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var stats in trace.BySource.Values
                         .OrderByDescending(s => s.Total)
                         .ThenByDescending(s => s.Wins))
            {
                sb.AppendLine(
                    $"| {stats.Source} | {stats.Wins} | {stats.Losses} | {stats.Total} | {stats.WinRate * 100:F1} |");
            }
        }
    }

    /// <summary>
    /// Per-classifier threshold sweep — for each classifier, lists Precision/Recall/F1 at
    /// 5 % confidence steps and calls out the optimal threshold. Lets the operator read off
    /// the recommended <c>min_confidence</c> directly from the report without re-running the
    /// backtest against multiple configs.
    /// </summary>
    private static void AppendClassifierThresholdsSection(
        System.Text.StringBuilder sb,
        IReadOnlyDictionary<string, ClassifierThresholdSweep> thresholds)
    {
        if (thresholds.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Classifier Threshold Sweep (per model)");
        sb.AppendLine();
        sb.AppendLine("> For each classifier the top-confidence candidate per track is bucketed at 5 % ");
        sb.AppendLine("> intervals. **TP** = predicted matches truth genre (whitespace-folded). **FP** = predicted ");
        sb.AppendLine("> but truth differs. **FN** = no prediction at this threshold but truth was present. ");
        sb.AppendLine("> The bucket with the highest F1 is the recommended `min_confidence` for that classifier.");

        foreach (var (name, sweep) in thresholds.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine($"### `{name}`");
            sb.AppendLine();
            if (sweep.OptimalThreshold is not null)
            {
                sb.AppendLine(
                    $"**Recommended `min_confidence`: {sweep.OptimalThreshold:F2}** " +
                    $"(F1 = {sweep.OptimalF1:F3})");
                sb.AppendLine();
            }
            sb.AppendLine("| Threshold | TP | FP | FN | Precision | Recall | F1 |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var bucket in sweep.Buckets)
            {
                var marker = sweep.OptimalThreshold is not null
                             && Math.Abs(bucket.Threshold - sweep.OptimalThreshold.Value) < 1e-9
                    ? " ← best"
                    : string.Empty;
                sb.AppendLine(
                    $"| {bucket.Threshold:F2}{marker} | {bucket.TruePositives} | {bucket.FalsePositives} | " +
                    $"{bucket.FalseNegatives} | {bucket.Precision:F3} | {bucket.Recall:F3} | {bucket.F1:F3} |");
            }
        }
    }

    /// <summary>
    /// Per-genre BPM distribution + ready-to-paste <c>tempo_ranges_by_genre</c> YAML snippet.
    /// Uses (P10, P90) as the suggested range — wide enough to keep the long tail (live mixes,
    /// chopped intros that throw off Essentia's first-peak) inside the range while ignoring
    /// outliers (samples that should never have been in this folder).
    /// </summary>
    private static void AppendBpmDistributionSection(
        System.Text.StringBuilder sb,
        IReadOnlyDictionary<string, BpmDistribution> byGenre)
    {
        if (byGenre.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## BPM Distribution by Genre (Mixed-In-Key truth, MIK-corrected)");
        sb.AppendLine();
        sb.AppendLine("> Genre-corrected BPM truth (DnB×2 below 100, DubStep÷2 above 100) aggregated per ");
        sb.AppendLine("> subfolder. The recommended `tempo_ranges_by_genre` row uses (P10, P90) to catch ");
        sb.AppendLine("> the typical envelope while ignoring outliers.");
        sb.AppendLine();
        sb.AppendLine("| Genre | Count | Mean | StdDev | Min | P10 | P25 | Median | P75 | P90 | Max |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (_, dist) in byGenre.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(
                $"| {dist.Genre} | {dist.Count} | {dist.Mean:F1} | {dist.StandardDeviation:F1} | " +
                $"{dist.Min:F1} | {dist.Percentile10:F1} | {dist.Percentile25:F1} | {dist.Median:F1} | " +
                $"{dist.Percentile75:F1} | {dist.Percentile90:F1} | {dist.Max:F1} |");
        }
        sb.AppendLine();
        sb.AppendLine("### Suggested `tempo_ranges_by_genre` (paste into tagger.yaml)");
        sb.AppendLine();
        sb.AppendLine("```yaml");
        sb.AppendLine("analysis:");
        sb.AppendLine("  bpm:");
        sb.AppendLine("    tempo_ranges_by_genre:");
        foreach (var (_, dist) in byGenre.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var lo = (int)Math.Floor(dist.Percentile10);
            var hi = (int)Math.Ceiling(dist.Percentile90);
            // TempoRangeResolver normalises with OrdinalIgnoreCase, so the casing is irrelevant
            // at lookup time. Emit the genre name verbatim from the subfolder — readers can
            // lowercase it themselves if their yaml style demands it.
            sb.AppendLine(
                $"      {dist.Genre,-22} {{ min: {lo}, max: {hi} }}   # n={dist.Count}, median={dist.Median:F1}");
        }
        sb.AppendLine("```");
    }
}
