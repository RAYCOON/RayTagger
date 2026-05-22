using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Hosting;
using RayTagger.Metadata;
using FsFile = System.IO.File;

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
    private readonly TaxonomyGenreResolver _genreResolver;
    private readonly PipelineFactory _pipelineFactory;
    private readonly SidecarRestoreService _sidecarRestore;
    private readonly LibraryDiscoveryService _libraryDiscovery;
    private readonly UiToolStatusReporter _statusReporter;
    private readonly ILogger<ScanCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Cached options + write-side lock used by ApplyAsync after a successful scan. Mutating
    // `_lastOptions.Write.DryRun` under the lock keeps a single-row Apply and a Batch Apply All
    // from racing each other when the user multi-clicks.
    private TaggerOptions? _lastOptions;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <summary>Absolute path of the <c>mappings.yaml</c> in use by the last scan, or null if no scan has run.</summary>
    public string? LastMappingsFile => _lastOptions?.Mapping.RulesFile;

    /// <summary>Loaded taxonomy from the last scan — used by the Rule Editor for live validation. Defaults to empty.</summary>
    public Taxonomy LastTaxonomy => _lastOptions?.Taxonomy.Loaded ?? Taxonomy.Empty;

    /// <summary>
    /// The TaggerOptions tree from the last load (Scan or Discovery). Null until the first call
    /// to <see cref="DiscoverAsync"/> or <see cref="ScanAsync"/>. Consumed by the per-track API
    /// button (via <see cref="ITrackLookupExecutor"/>) to look up genres for one track without
    /// re-running the whole pipeline.
    /// </summary>
    public TaggerOptions? LastOptions => _lastOptions;

    /// <summary>
    /// Fires whenever <see cref="_lastOptions"/> changes (i.e. a new scan loaded a config). The
    /// <see cref="RayTagger.Ui.ViewModels.RuleEditorViewModel"/> subscribes so it can auto-load the
    /// mappings file the moment the scan picks one up, without depending on TabControl
    /// SelectionChanged timing.
    /// </summary>
    public event EventHandler<EventArgs>? OptionsLoaded;

    public ScanCoordinator(
        IFileDiscoveryService discovery,
        ITagReaderAdapter reader,
        ITagWriterAdapter writer,
        IMappingRuleEngine ruleEngine,
        ISortService sortService,
        TaxonomyGenreResolver genreResolver,
        PipelineFactory pipelineFactory,
        SidecarRestoreService sidecarRestore,
        LibraryDiscoveryService libraryDiscovery,
        UiToolStatusReporter statusReporter,
        ILogger<ScanCoordinator> logger,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(sortService);
        ArgumentNullException.ThrowIfNull(genreResolver);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        ArgumentNullException.ThrowIfNull(sidecarRestore);
        ArgumentNullException.ThrowIfNull(libraryDiscovery);
        ArgumentNullException.ThrowIfNull(statusReporter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _discovery = discovery;
        _reader = reader;
        _writer = writer;
        _ruleEngine = ruleEngine;
        _sortService = sortService;
        _genreResolver = genreResolver;
        _pipelineFactory = pipelineFactory;
        _sidecarRestore = sidecarRestore;
        _libraryDiscovery = libraryDiscovery;
        _statusReporter = statusReporter;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// First-pass library discovery: enumerates audio files + reads their existing tags so the UI
    /// can populate the grid before the (expensive) analyze/lookup/rules pipeline runs. Loads the
    /// tagger.yaml config the same way <see cref="ScanAsync"/> does so the rule editor's
    /// <see cref="OptionsLoaded"/> auto-load kicks in immediately.
    /// </summary>
    public async IAsyncEnumerable<TrackPreview> DiscoverAsync(
        string sourceDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var (options, _) = LoadOrDefaults(sourceDirectory);
        _lastOptions = options;
        OptionsLoaded?.Invoke(this, EventArgs.Empty);

        _logger.LogInformation("Starting UI discovery of {Source}", sourceDirectory);
        await foreach (var preview in _libraryDiscovery.DiscoverAsync(options.Scan, cancellationToken).ConfigureAwait(false))
        {
            yield return preview;
        }
    }

    /// <summary>
    /// Runs a dry-run scan over <paramref name="sourceDirectory"/>. Auto-loads
    /// <c>tagger.yaml</c> + <c>mappings.yaml</c> when present; falls back to default options +
    /// empty ruleset otherwise. Status entries are pushed to <see cref="UiToolStatusReporter"/>
    /// as analyzers / providers come online.
    /// </summary>
    public async IAsyncEnumerable<PipelineOutcome> ScanAsync(
        string sourceDirectory,
        Func<TrackFile, ValueTask>? onFileStarted = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        _statusReporter.Reset();

        var (options, rules) = LoadOrDefaults(sourceDirectory);
        _lastOptions = options;  // Keep for ApplyAsync — same options used for the dry-run scan.
        // Signal the Rule Editor so it can auto-load mappings.yaml the moment a scan starts —
        // independent of when (or whether) the user switches to the Regeln tab.
        OptionsLoaded?.Invoke(this, EventArgs.Empty);

        // `using` so the Essentia profile-file cache (transient YAMLs in /tmp) gets cleaned up
        // even if the caller breaks out of the async-enumerable early.
        using var built = await _pipelineFactory.BuildAsync(options, _statusReporter, cancellationToken).ConfigureAwait(false);

        var pipeline = new TagPipeline(
            _discovery,
            _reader,
            _writer,
            built.AnalysisRunner,
            built.LookupRunner,
            _ruleEngine,
            _sortService,
            _genreResolver,
            _loggerFactory.CreateLogger<TagPipeline>());

        _logger.LogInformation("Starting UI scan of {Source}", sourceDirectory);
        await foreach (var outcome in pipeline.RunAsync(options, rules, onFileStarted, cancellationToken).ConfigureAwait(false))
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
                Scan = new ScanOptions { Source = sourceDirectory, Recursive = true },
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
                Scan = new ScanOptions { Source = sourceDirectory, Recursive = true },
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
    /// Returns true if a backup-sidecar exists for the given audio file — used by the row VM
    /// to decide whether the "Revert" button is enabled at scan-time.
    /// </summary>
    public bool HasSidecar(string audioPath) => _sidecarRestore.FindLatestSidecar(audioPath) is not null;

    /// <summary>
    /// Reverts a single file from its most recent backup-sidecar. Reads the YAML snapshot,
    /// writes every field back as <see cref="TagFieldSource.Rules"/> so the writer treats it
    /// as a forced overwrite (same semantics as <c>tagger restore</c>), then deletes the sidecar.
    /// No fresh backup is taken — capturing the post-revert state would just snapshot the value
    /// we're undoing.
    /// </summary>
    public async Task<ApplyResult> RevertAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        if (_lastOptions is null)
        {
            return new ApplyResult(Success: false, WrittenFields: [], ErrorMessage:
                "Kein Scan zur Wiederherstellung verfügbar — bitte zuerst scannen.");
        }

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sidecarPath = _sidecarRestore.FindLatestSidecar(audioPath);
            if (sidecarPath is null)
            {
                return new ApplyResult(Success: false, WrittenFields: [], ErrorMessage:
                    "Keine Sicherungsdatei vorhanden.");
            }

            TrackTags snapshot;
            try
            {
                snapshot = _sidecarRestore.Read(sidecarPath);
            }
            catch (SidecarRestoreException ex)
            {
                _logger.LogWarning(ex, "Sidecar read failed for {Path}", sidecarPath);
                return new ApplyResult(Success: false, WrittenFields: [], ErrorMessage: ex.Message);
            }

            var resolved = SnapshotToResolved(snapshot);
            var options = _lastOptions;
            var savedDryRun = options.Write.DryRun;
            var savedBackup = options.Write.Backup;
            options.Write.DryRun = false;
            options.Write.Backup = false;  // No nested backup — we're undoing the previous write.
            try
            {
                var result = await Task.Run(
                    () => _writer.Write(audioPath, resolved, options),
                    cancellationToken).ConfigureAwait(false);

                TryDeleteSidecar(sidecarPath);
                _logger.LogInformation("Reverted {Path} from sidecar; {Count} fields restored",
                    audioPath, result.WrittenFields.Count);
                return new ApplyResult(
                    Success: true,
                    WrittenFields: result.WrittenFields,
                    ErrorMessage: null,
                    RestoredSnapshot: snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or MetadataException)
            {
                _logger.LogWarning(ex, "Revert failed for {Path}", audioPath);
                return new ApplyResult(Success: false, WrittenFields: [], ErrorMessage: ex.Message);
            }
            finally
            {
                options.Write.DryRun = savedDryRun;
                options.Write.Backup = savedBackup;
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private void TryDeleteSidecar(string sidecarPath)
    {
        try
        {
            FsFile.Delete(sidecarPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Restore succeeded on the audio file — sidecar leftover is harmless cosmetics.
            _logger.LogWarning(ex, "Could not remove sidecar {Path}", sidecarPath);
        }
    }

    /// <summary>
    /// Every snapshot field flows back as <see cref="TagFieldSource.Rules"/> so the writer treats
    /// it as "must write, regardless of policy" — including a null Mood / SetPosition, which the
    /// writer translates into a frame-clear. Earlier versions wrapped null in
    /// <see cref="ResolvedField.Empty{T}"/> to "protect pre-feature sidecars", but no such
    /// sidecars exist in this codebase's user history (Mood / SetPosition serialisation landed
    /// before any Apply ever ran), and the protective branch silently broke real revert on rows
    /// that legitimately had no mood at backup time.
    /// </summary>
    private static ResolvedTrackTags SnapshotToResolved(TrackTags snapshot)
    {
        var custom = snapshot.Custom.ToDictionary(
            kv => kv.Key,
            kv => new ResolvedField<string>(kv.Value, TagFieldSource.Rules, 1.0),
            StringComparer.OrdinalIgnoreCase);

        return new ResolvedTrackTags(
            Genre: new ResolvedField<string>(snapshot.Genre, TagFieldSource.Rules, 1.0),
            SubGenre: new ResolvedField<string>(snapshot.SubGenre, TagFieldSource.Rules, 1.0),
            Bpm: new ResolvedValueField<double>(snapshot.Bpm, TagFieldSource.Rules, 1.0),
            Key: new ResolvedField<MusicalKey>(snapshot.Key, TagFieldSource.Rules, 1.0),
            Energy: new ResolvedValueField<int>(snapshot.Energy, TagFieldSource.Rules, 1.0),
            Mood: new ResolvedField<string>(snapshot.Mood, TagFieldSource.Rules, 1.0),
            SetPosition: new ResolvedField<string>(snapshot.SetPosition, TagFieldSource.Rules, 1.0),
            Custom: custom);
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
/// Outcome of a single <see cref="ScanCoordinator.ApplyAsync"/> or
/// <see cref="ScanCoordinator.RevertAsync"/> call. <see cref="Success"/> is false when the file
/// was inaccessible, locked, or the writer threw an IO/permission error; the row VM surfaces
/// the message in its error badge. <see cref="RestoredSnapshot"/> is populated only by
/// <c>RevertAsync</c> so the row can lift its Existing-* values to the restored state without
/// re-reading the file.
/// </summary>
public sealed record ApplyResult(
    bool Success,
    IReadOnlyList<string> WrittenFields,
    string? ErrorMessage,
    TrackTags? RestoredSnapshot = null);

