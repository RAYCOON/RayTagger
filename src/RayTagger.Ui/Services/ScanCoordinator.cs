using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Hosting;
using RayTagger.Metadata;

namespace RayTagger.Ui.Services;

/// <summary>
/// Wraps the full pipeline for the UI: auto-discovers <c>tagger.yaml</c> next to the source
/// folder, builds the analyzer + lookup chain via <see cref="PipelineFactory"/>, and streams
/// <see cref="PipelineOutcome"/>s back to the view-model. Falls back to in-memory defaults when
/// no config is found so the UI still shows existing tags instead of dying with an error.
/// </summary>
public sealed class ScanCoordinator
{
    private readonly IFileDiscoveryService _discovery;
    private readonly ITagReaderAdapter _reader;
    private readonly ITagWriterAdapter _writer;
    private readonly IMappingRuleEngine _ruleEngine;
    private readonly ISortService _sortService;
    private readonly PipelineFactory _pipelineFactory;
    private readonly UiToolStatusReporter _statusReporter;
    private readonly ILogger<ScanCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Cached options + write-side lock used by ApplyAsync after a successful scan. Mutating
    // `_lastOptions.Write.DryRun` under the lock keeps a single-row Apply and a Batch Apply All
    // from racing each other when the user multi-clicks.
    private TaggerOptions? _lastOptions;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public ScanCoordinator(
        IFileDiscoveryService discovery,
        ITagReaderAdapter reader,
        ITagWriterAdapter writer,
        IMappingRuleEngine ruleEngine,
        ISortService sortService,
        PipelineFactory pipelineFactory,
        UiToolStatusReporter statusReporter,
        ILogger<ScanCoordinator> logger,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(sortService);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        ArgumentNullException.ThrowIfNull(statusReporter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _discovery = discovery;
        _reader = reader;
        _writer = writer;
        _ruleEngine = ruleEngine;
        _sortService = sortService;
        _pipelineFactory = pipelineFactory;
        _statusReporter = statusReporter;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Runs a dry-run scan over <paramref name="sourceDirectory"/>. Auto-loads
    /// <c>tagger.yaml</c> + <c>mappings.yaml</c> when present; falls back to default options +
    /// empty ruleset otherwise. Status entries are pushed to <see cref="UiToolStatusReporter"/>
    /// as analyzers / providers come online.
    /// </summary>
    public async IAsyncEnumerable<PipelineOutcome> ScanAsync(
        string sourceDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        _statusReporter.Reset();

        var (options, rules) = LoadOrDefaults(sourceDirectory);
        _lastOptions = options;  // Keep for ApplyAsync — same options used for the dry-run scan.

        var built = await _pipelineFactory.BuildAsync(options, _statusReporter, cancellationToken).ConfigureAwait(false);

        var pipeline = new TagPipeline(
            _discovery,
            _reader,
            _writer,
            built.AnalysisRunner,
            built.LookupRunner,
            _ruleEngine,
            _sortService,
            _loggerFactory.CreateLogger<TagPipeline>());

        _logger.LogInformation("Starting UI scan of {Source}", sourceDirectory);
        await foreach (var outcome in pipeline.RunAsync(options, rules, cancellationToken).ConfigureAwait(false))
        {
            yield return outcome;
        }
    }

    /// <summary>
    /// Looks for <c>tagger.yaml</c> in the source folder, then one level up. When found, loads
    /// it (and the mapping rules it points at). When not, returns sensible defaults pointing at
    /// the source dir so the pipeline can still run.
    /// </summary>
    private (TaggerOptions Options, MappingRuleSet Rules) LoadOrDefaults(string sourceDirectory)
    {
        var configPath = FindConfig(sourceDirectory);
        if (configPath is null)
        {
            _statusReporter.ReportNote("No tagger.yaml found next to the source folder — running with defaults.");
            var options = new TaggerOptions
            {
                Scan = new ScanOptions { Source = sourceDirectory, Recursive = true, Parallelism = 4 },
                Write = new WriteOptions { DryRun = true, Backup = false },
            };
            return (options, new MappingRuleSet());
        }

        TaggerOptions loaded;
        MappingRuleSet rules;
        try
        {
            loaded = TaggerOptionsLoader.Load(configPath);
        }
        catch (ConfigurationException ex)
        {
            _statusReporter.ReportNote($"tagger.yaml at {configPath} is invalid — running with defaults. ({ex.Message})");
            loaded = new TaggerOptions
            {
                Scan = new ScanOptions { Source = sourceDirectory, Recursive = true, Parallelism = 4 },
            };
            rules = new MappingRuleSet();
            ForceUiDryRun(loaded);
            return (loaded, rules);
        }

        // The picked folder always wins over the config's `scan.source` — the user chose it via
        // the picker, after all. Same as the CLI's `--source` override.
        loaded.Scan.Source = sourceDirectory;
        ForceUiDryRun(loaded);

        try
        {
            rules = MappingRulesLoader.Load(loaded.Mapping.RulesFile, loaded.Taxonomy.Loaded);
        }
        catch (Exception ex) when (ex is ConfigurationException or FileNotFoundException)
        {
            _statusReporter.ReportNote($"mappings.yaml not loaded ({ex.Message}). Running without rules.");
            rules = new MappingRuleSet();
        }

        _statusReporter.ReportNote($"Using config: {configPath}");
        return (loaded, rules);
    }

    private static void ForceUiDryRun(TaggerOptions options)
    {
        // The UI is exploratory by design — never write on a scan that the user kicked off
        // implicitly by picking a folder. The Apply / Apply-All workflow explicitly flips
        // DryRun=false inside <see cref="ApplyAsync"/> under the apply gate.
        options.Write.DryRun = true;
    }

    /// <summary>
    /// Writes the proposed tags for a single <see cref="PipelineOutcome"/> back to disk. Reuses
    /// the resolved tag values from the dry-run scan — no re-read, no re-analyze, no re-lookup.
    /// </summary>
    /// <remarks>
    /// Honours <c>write.backup</c> from the loaded config (sidecar YAML written before the tag
    /// file is touched). Per-call <see cref="SemaphoreSlim"/> gate serialises concurrent Apply
    /// calls so the temporary <c>DryRun</c> flip on the shared options instance is safe.
    /// </remarks>
    public async Task<ApplyResult> ApplyAsync(PipelineOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (_lastOptions is null)
        {
            return new ApplyResult(Success: false, WrittenFields: [], ErrorMessage:
                "Kein Scan zur Anwendung verfügbar — bitte zuerst scannen.");
        }

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = _lastOptions;
            var savedDryRun = options.Write.DryRun;
            options.Write.DryRun = false;
            try
            {
                var result = await Task.Run(
                    () => _writer.Write(outcome.File.Path, outcome.Resolved, options),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Applied {Count} fields to {Path}", result.WrittenFields.Count, outcome.File.Path);
                return new ApplyResult(Success: true, WrittenFields: result.WrittenFields, ErrorMessage: null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Apply failed for {Path}", outcome.File.Path);
                return new ApplyResult(Success: false, WrittenFields: [], ErrorMessage: ex.Message);
            }
            finally
            {
                options.Write.DryRun = savedDryRun;
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Locates a <c>tagger.yaml</c> for the picked folder. Priority: the folder itself first
    /// (a per-collection config "lives next to the music"), then the parent folder (handy when
    /// the user picks a sub-album from a library that has one central config). Returns null when
    /// neither is present so the caller can fall back to in-memory defaults.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="DirectoryInfo.Parent"/> for the upward walk — handles mixed
    /// <c>\</c>/<c>/</c> separators correctly on Windows where <see cref="Path.GetDirectoryName(string)"/>
    /// + a single trailing-char trim can miss the parent.
    /// </remarks>
    private static string? FindConfig(string sourceDirectory)
    {
        var inSource = Path.Combine(sourceDirectory, "tagger.yaml");
        if (File.Exists(inSource)) return inSource;

        var parent = new DirectoryInfo(sourceDirectory).Parent;
        if (parent is not null)
        {
            var inParent = Path.Combine(parent.FullName, "tagger.yaml");
            if (File.Exists(inParent)) return inParent;
        }
        return null;
    }
}

/// <summary>
/// Outcome of a single <see cref="ScanCoordinator.ApplyAsync"/> call. <see cref="Success"/> is
/// false when the file was inaccessible, locked, or the writer threw an IO/permission error; the
/// row VM surfaces the message in its error badge.
/// </summary>
public sealed record ApplyResult(bool Success, IReadOnlyList<string> WrittenFields, string? ErrorMessage);

