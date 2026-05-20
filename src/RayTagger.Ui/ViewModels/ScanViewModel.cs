using System.Collections.ObjectModel;
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
    private readonly ILogger<ScanViewModel> _logger;
    private CancellationTokenSource? _cts;
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

    /// <summary>True while either a scan or apply is running — drives toolbar IsEnabled bindings.</summary>
    public bool IsBusy => IsScanning || IsApplying;

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
    /// Flat list of every row whose proposed tags differ from disk. Bound by the Rule Editor's
    /// side-panel for the "where exactly do my changes apply" view. Rebuilt after every scan-row
    /// add, after a Live-Preview rule edit, and after Apply/Revert flips a row's diff state.
    /// </summary>
    public ObservableCollection<RowDiffSummary> DiffSummary { get; } = [];

    public ScanViewModel(ScanCoordinator coordinator, ITagReader reader, IMappingRuleEngine ruleEngine, ILogger<ScanViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _reader = reader;
        _ruleEngine = ruleEngine;
        _logger = logger;
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

    [RelayCommand]
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
        Outcomes.Clear();
        DiffSummary.Clear();
        ScannedCount = 0;
        ChangedCount = 0;
        FailedCount = 0;
        IsScanning = true;
        StatusMessage = "Scanne …";

        try
        {
            await foreach (var outcome in _coordinator.ScanAsync(SourceDirectory, cancellationToken: _cts.Token).ConfigureAwait(true))
            {
                // The reader is cheap (already in the cache for non-changed files via the same
                // pipeline), and we need the pre-write tag values to render the diff column.
                var existing = SafeRead(outcome.File.Path);
                var vm = new TrackOutcomeViewModel(outcome, existing);

                // Marshal back to the UI thread — ObservableCollection only allows mutation on the
                // dispatcher thread. Without this, large parallel scans hit cross-thread asserts.
                // DispatcherOperation doesn't expose ConfigureAwait; awaiting raw is fine here
                // because the continuation already runs on the UI thread after Invoke completes.
                // Seed HasSidecar so the Revert button is enabled out of the gate for any
                // file whose previous Apply left a sidecar behind (incl. across scan runs).
                vm.HasSidecar = _coordinator.HasSidecar(outcome.File.Path);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Outcomes.Add(vm);
                    ScannedCount = Outcomes.Count;
                    if (outcome.Status == PipelineStatus.Failed) FailedCount++;
                    // "Würde ändern" counts under the Änderungen banner too — dry-run scans
                    // never reach Written, so without this the user sees "0 Änderungen" every
                    // time despite the rule engine doing meaningful work.
                    else if (vm.StatusLabel is "Geschrieben" or "Würde ändern") ChangedCount++;

                    // Append-per-row keeps the side-panel populating incrementally during the
                    // scan instead of all-at-end. Full rebuilds happen only after operations
                    // that can flip diff state across multiple rows (Live-Preview, Apply, Revert).
                    var diffs = RowDiffCollector.Collect(vm);
                    if (diffs.Count > 0)
                    {
                        DiffSummary.Add(new RowDiffSummary(vm, diffs));
                    }
                });
            }
            StatusMessage = $"Fertig: {ScannedCount} Dateien, {ChangedCount} Änderungen vorgeschlagen, {FailedCount} Fehler.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan abgebrochen.";
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
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyAllChanged))]
    private async Task ApplyAllChangedAsync()
    {
        var pending = Outcomes.Where(o => o.CanApply).ToList();
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

    private bool CanApplyAllChanged() => !IsBusy && Outcomes.Any(o => o.CanApply);

    /// <summary>
    /// Disposes the last <see cref="CancellationTokenSource"/> the scan command allocated. The
    /// VM itself is registered as Transient but held by <c>MainWindowViewModel</c> for the
    /// window's lifetime — releasing the CTS on window-close prevents a handle leak when the
    /// app loops (e.g. preview-driven multi-run workflows).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
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
