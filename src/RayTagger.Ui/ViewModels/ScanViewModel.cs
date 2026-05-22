using System.Collections.ObjectModel;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Metadata;
using RayTagger.Ui.Services;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// View-model behind the "scan a folder, see what would change" view. Owns the source directory,
/// kicks off the scan via <see cref="ScanCoordinator"/>, and accumulates outcomes into an
/// <see cref="ObservableCollection{T}"/> the DataGrid binds to.
/// </summary>
public sealed partial class ScanViewModel : ObservableObject, IDisposable
{
    private readonly ScanCoordinator _coordinator;
    private readonly ITagReader _reader;
    private readonly IMappingRuleEngine _ruleEngine;
    private readonly ITrackLookupExecutor _lookupExecutor;
    private readonly ILogger<ScanViewModel> _logger;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _discoveryCts;
    private bool _disposed;

    [ObservableProperty]
    private string? _sourceDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAllChangedCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAllChangedCommand))]
    private bool _isApplying;

    /// <summary>True during the first-pass tag discovery (post folder-pick, pre-scan).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAllChangedCommand))]
    private bool _isDiscovering;

    /// <summary>True while a scan, apply, or discovery is running — drives toolbar IsEnabled bindings.</summary>
    public bool IsBusy => IsScanning || IsApplying || IsDiscovering;

    [ObservableProperty]
    private int _scannedCount;

    [ObservableProperty]
    private int _changedCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _appliedCount;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<TrackOutcomeViewModel> Outcomes { get; } = [];

    /// <summary>
    /// Per-column filter VM — owns the 17 filter strings, the <see cref="DataGridCollectionView"/>,
    /// and the ClearFilters command. The DataGrid binds <c>ItemsSource</c> to <c>Filters.View</c>
    /// and the inline header TextBoxes to <c>Filters.Genre</c>, <c>Filters.Bpm</c>, …
    /// </summary>
    public ColumnFilterViewModel Filters { get; }

    /// <summary>
    /// Alias for <c>Filters.View</c> — kept so XAML doesn't have to go through the indirection.
    /// </summary>
    public DataGridCollectionView FilteredOutcomes => Filters.View;

    /// <summary>
    /// Flat list of every row whose proposed tags differ from disk. Bound by the Rule Editor's
    /// side-panel for the "where exactly do my changes apply" view. Rebuilt after every scan-row
    /// add, after a Live-Preview rule edit, and after Apply/Revert flips a row's diff state.
    /// </summary>
    public ObservableCollection<RowDiffSummary> DiffSummary { get; } = [];

    public ScanViewModel(
        ScanCoordinator coordinator,
        ITagReader reader,
        IMappingRuleEngine ruleEngine,
        ITrackLookupExecutor lookupExecutor,
        ILogger<ScanViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(lookupExecutor);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _reader = reader;
        _ruleEngine = ruleEngine;
        _lookupExecutor = lookupExecutor;
        _logger = logger;

        Filters = new ColumnFilterViewModel(Outcomes);
        // "Alle anwenden" iterates the visible subset, so its CanExecute must re-evaluate when
        // the user's filter changes (button greys out if no pending row passes the filter).
        Filters.FilterChanged += (_, _) => ApplyAllChangedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Auto-trigger discovery whenever the source folder changes. Cancels any in-flight discovery
    /// or scan first so a quick folder-shuffle doesn't leave orphaned tasks behind. Fire-and-forget
    /// from the property setter is fine — exceptions surface via the logger / status message.
    /// </summary>
    partial void OnSourceDirectoryChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _ = DiscoverAsync(value);
    }

    /// <summary>
    /// Walks the new source folder, reads each file's existing tags, and populates the grid with
    /// preview rows (empty StatusBadge). Cancels any prior scan/discovery before starting.
    /// </summary>
    private async Task DiscoverAsync(string sourceDir)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discoveryCts = new CancellationTokenSource();
        var token = _discoveryCts.Token;

        Outcomes.Clear();
        DiffSummary.Clear();
        ScannedCount = 0;
        ChangedCount = 0;
        FailedCount = 0;
        AppliedCount = 0;
        IsDiscovering = true;
        StatusMessage = "Lese Datei-Tags …";

        try
        {
            await foreach (var preview in _coordinator.DiscoverAsync(sourceDir, token).ConfigureAwait(true))
            {
                var vm = new TrackOutcomeViewModel(preview.File, preview.Existing, preview.ErrorMessage, _lookupExecutor);
                vm.HasSidecar = _coordinator.HasSidecar(preview.File.Path);
                vm.UpdateTaxonomy(_coordinator.LastTaxonomy);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Outcomes.Add(vm);
                });
            }
            StatusMessage = $"{Outcomes.Count} Datei(en) gefunden — bereit zum Scan.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Discovery abgebrochen.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery failed");
            StatusMessage = $"Discovery-Fehler: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    /// <summary>
    /// Live-Preview entry point. Re-evaluates every already-scanned outcome against
    /// <paramref name="newRules"/> and pushes the refreshed proposed-* values back to each row's
    /// view-model. No file IO, no re-analysis, no re-lookup — purely the rule engine.
    /// </summary>
    public void UpdatePreview(MappingRuleSet newRules)
    {
        ArgumentNullException.ThrowIfNull(newRules);
        if (Outcomes.Count == 0) return;

        var taxonomy = _coordinator.LastTaxonomy;
        foreach (var row in Outcomes)
        {
            // Rows where the pre-map snapshot is missing (read failure during the original scan)
            // can't be re-evaluated — skip rather than throwing inside the loop.
            if (row.PreMapResolved is null || row.ExistingAtScan is null) continue;

            try
            {
                var result = _ruleEngine.Evaluate(
                    row.PreMapResolved,
                    row.ExistingAtScan,
                    row.SourceOutcome.File,
                    newRules,
                    taxonomy);

                var refreshed = row.SourceOutcome with
                {
                    Resolved = result.Tags,
                    AppliedRules = result.Applied,
                };
                row.UpdatePreview(refreshed, row.ExistingAtScan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live-Preview rule eval failed for {Path}", row.Path);
            }
        }

        RowDiffCollector.Rebuild(DiffSummary, Outcomes);
        StatusMessage = $"Vorschau aktualisiert ({Outcomes.Count} Zeilen, {DiffSummary.Count} mit Änderungen).";
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            StatusMessage = "Bitte zuerst einen Ordner auswählen.";
            return;
        }
        if (IsScanning) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        DiffSummary.Clear();
        ScannedCount = 0;
        ChangedCount = 0;
        FailedCount = 0;
        AppliedCount = 0;
        IsScanning = true;
        StatusMessage = "Scanne …";

        // Reset every preview row to the empty status so prior CNG/OK/ERR badges from a previous
        // scan don't bleed into this run. Pipeline outcomes will repaint them via UpdateFromOutcome.
        // Also refresh each row's taxonomy snapshot — a new scan may have loaded a different
        // taxonomy.yaml, and the dark-blue "non-taxonomy" highlight needs to track it.
        foreach (var row in Outcomes)
        {
            row.ResetStatus();
            row.UpdateTaxonomy(_coordinator.LastTaxonomy);
        }

        // Lookup table for outcome-matching. Keep separate from Outcomes because the pipeline
        // emits in completion order (not discovery order), and we don't want O(N²) per-outcome.
        var byPath = Outcomes.ToDictionary(r => r.Path, StringComparer.Ordinal);

        async ValueTask OnFileStarted(TrackFile file)
        {
            // Invoked from worker threads — marshal to UI thread before touching VM state.
            // Looking up byPath here (rather than outside the dispatch) keeps the dict access
            // single-threaded.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (byPath.TryGetValue(file.Path, out var row))
                {
                    row.BeginScan();
                }
            });
        }

        try
        {
            await foreach (var outcome in _coordinator.ScanAsync(SourceDirectory, OnFileStarted, token).ConfigureAwait(true))
            {
                // The pipeline already read the existing tags during its Read stage and threaded
                // them through on PipelineOutcome.ExistingAtScan — no need to re-read here.
                var existing = outcome.ExistingAtScan ?? RayTagger.Core.Models.TrackTags.Empty;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (byPath.TryGetValue(outcome.File.Path, out var row))
                    {
                        row.UpdateFromOutcome(outcome, existing);
                        row.HasSidecar = _coordinator.HasSidecar(outcome.File.Path);
                    }
                    else
                    {
                        // Race: file appeared on disk after discovery enumerated. Add as a new
                        // row so the user still sees it instead of silently dropping it.
                        var vm = new TrackOutcomeViewModel(outcome, existing, _lookupExecutor)
                        {
                            HasSidecar = _coordinator.HasSidecar(outcome.File.Path),
                        };
                        vm.UpdateTaxonomy(_coordinator.LastTaxonomy);
                        Outcomes.Add(vm);
                        byPath[outcome.File.Path] = vm;
                        row = vm;
                    }

                    // Bump scanned counter exactly once per outcome — regardless of OK/CNG/ERR.
                    // Runs inside the UI-thread dispatch so the increment is implicitly serialized
                    // across parallel pipeline workers (no Interlocked needed).
                    ScannedCount++;

                    if (outcome.Status == PipelineStatus.Failed) FailedCount++;
                    // "Würde ändern" counts under the Änderungen banner too — dry-run scans
                    // never reach Written, so without this the user sees "0 Änderungen" every
                    // time despite the rule engine doing meaningful work.
                    else if (row.StatusLabel is "Geschrieben" or "Würde ändern") ChangedCount++;

                    // Append-per-row keeps the side-panel populating incrementally during the
                    // scan instead of all-at-end. Full rebuilds happen only after operations
                    // that can flip diff state across multiple rows (Live-Preview, Apply, Revert).
                    var diffs = RowDiffCollector.Collect(row);
                    if (diffs.Count > 0)
                    {
                        DiffSummary.Add(new RowDiffSummary(row, diffs));
                    }
                });
            }
            StatusMessage = $"Fertig: {Outcomes.Count} Dateien, {ChangedCount} Änderungen vorgeschlagen, {FailedCount} Fehler.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan abgebrochen.";
            // Roll any row stuck on SCN back to the empty state — they didn't actually finish.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var row in Outcomes)
                {
                    if (row.StatusLabel == "Scannen") row.ResetStatus();
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Applies a single row's proposed tags to disk. Used by the per-row "Anwenden" button —
    /// no confirmation dialog because the action is already explicit (one click per row).
    /// </summary>
    [RelayCommand]
    private async Task ApplyRowAsync(TrackOutcomeViewModel? row)
    {
        if (row is null || !row.CanApply) return;

        row.BeginApply();
        try
        {
            var result = await _coordinator.ApplyAsync(row.SourceOutcome).ConfigureAwait(true);
            if (result.Success)
            {
                row.EndApplySuccess();
                // Apply just wrote a fresh sidecar — the Revert button should light up.
                row.HasSidecar = true;
                AppliedCount++;
                // Existing-* now match Proposed-*, so this row's side-panel entry collapses.
                RowDiffCollector.Rebuild(DiffSummary, Outcomes);
                StatusMessage = $"Angewendet: {row.FileName} ({result.WrittenFields.Count} Felder).";
            }
            else
            {
                row.EndApplyFailure(result.ErrorMessage ?? "Unbekannter Schreibfehler.");
                FailedCount++;
                StatusMessage = $"Fehler beim Anwenden von {row.FileName}: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply failed for {Path}", row.Path);
            row.EndApplyFailure(ex.Message);
            FailedCount++;
        }
    }

    /// <summary>
    /// Reverts a single row from its most recent backup-sidecar. Same shape as ApplyRowAsync —
    /// explicit one-click action, no confirmation. The sidecar gets deleted after success so the
    /// button greys out until another Apply produces a new backup.
    /// </summary>
    [RelayCommand]
    private async Task RevertRowAsync(TrackOutcomeViewModel? row)
    {
        if (row is null || !row.CanRevert) return;

        row.BeginRevert();
        try
        {
            var result = await _coordinator.RevertAsync(row.Path).ConfigureAwait(true);
            if (result.Success && result.RestoredSnapshot is not null)
            {
                row.EndRevertSuccess(result.RestoredSnapshot);
                // Revert flipped Existing-* to the restored values — diffs may have reshaped.
                RowDiffCollector.Rebuild(DiffSummary, Outcomes);
                StatusMessage = $"Wiederhergestellt: {row.FileName} ({result.WrittenFields.Count} Felder).";
            }
            else if (result.Success)
            {
                // Unreachable: ScanCoordinator.RevertAsync populates RestoredSnapshot on success.
                // Guard so a future contract change doesn't NRE silently.
                row.EndRevertFailure("Revert ok, aber kein Snapshot zurückgeliefert.");
                FailedCount++;
            }
            else
            {
                row.EndRevertFailure(result.ErrorMessage ?? "Unbekannter Wiederherstellungsfehler.");
                FailedCount++;
                StatusMessage = $"Fehler bei Revert von {row.FileName}: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revert failed for {Path}", row.Path);
            row.EndRevertFailure(ex.Message);
            FailedCount++;
        }
    }

    /// <summary>
    /// Applies every row whose status is "Würde ändern" sequentially. The caller (MainWindow
    /// code-behind) is responsible for confirming with the user before invoking this — the
    /// command itself trusts that the user has already agreed.
    /// Iterates <see cref="FilteredOutcomes"/> so an active column filter scopes the batch to
    /// what the user actually sees ("filter what you bulk-apply"). Rows hidden by the filter
    /// stay untouched even if they're pending.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyAllChanged))]
    private async Task ApplyAllChangedAsync()
    {
        var pending = FilteredOutcomes.OfType<TrackOutcomeViewModel>()
            .Where(o => o.CanApply).ToList();
        if (pending.Count == 0)
        {
            StatusMessage = "Keine ausstehenden Änderungen.";
            return;
        }

        IsApplying = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var applied = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var row = pending[i];
                row.BeginApply();
                StatusMessage = $"Wende an … {i + 1}/{pending.Count} ({row.FileName})";

                try
                {
                    var result = await _coordinator.ApplyAsync(row.SourceOutcome, _cts.Token).ConfigureAwait(true);
                    if (result.Success)
                    {
                        row.EndApplySuccess();
                        applied++;
                    }
                    else
                    {
                        row.EndApplyFailure(result.ErrorMessage ?? "Unbekannter Schreibfehler.");
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    row.EndApplyFailure("Abgebrochen.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Apply failed for {Path}", row.Path);
                    row.EndApplyFailure(ex.Message);
                    failed++;
                }
            }

            AppliedCount += applied;
            FailedCount += failed;
            // Every successful row in the batch collapsed its diffs — refresh the side panel once.
            RowDiffCollector.Rebuild(DiffSummary, Outcomes);
            StatusMessage = _cts.Token.IsCancellationRequested
                ? $"Abgebrochen nach {applied} angewendet, {failed} fehlgeschlagen."
                : $"Fertig: {applied} angewendet, {failed} fehlgeschlagen.";
        }
        finally
        {
            IsApplying = false;
        }
    }

    private bool CanApplyAllChanged() => !IsBusy
        && FilteredOutcomes.OfType<TrackOutcomeViewModel>().Any(o => o.CanApply);

    /// <summary>
    /// Scan can only fire once Discovery has settled — running both in parallel against the same
    /// Outcomes list would race in match-by-path. Re-scanning while a previous scan is running
    /// is also blocked (returns early from <see cref="ScanAsync"/> anyway, but this disables the
    /// button so the user sees the state).
    /// </summary>
    private bool CanScan() => !IsScanning && !IsDiscovering && !IsApplying;

    /// <summary>
    /// Disposes both CancellationTokenSources the VM allocated. The VM itself is registered as
    /// Transient but held by <c>MainWindowViewModel</c> for the window's lifetime — releasing the
    /// CTS on window-close prevents a handle leak when the app loops (e.g. preview-driven
    /// multi-run workflows).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discoveryCts = null;
        _disposed = true;
    }

    private RayTagger.Core.Models.TrackTags SafeRead(string path)
    {
        try
        {
            return _reader.Read(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-write read failed for {Path}", path);
            return RayTagger.Core.Models.TrackTags.Empty;
        }
    }
}
