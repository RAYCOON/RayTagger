using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
public sealed partial class ScanViewModel : ObservableObject
{
    private readonly ScanCoordinator _coordinator;
    private readonly ITagReader _reader;
    private readonly ILogger<ScanViewModel> _logger;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string? _sourceDirectory;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _scannedCount;

    [ObservableProperty]
    private int _changedCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<TrackOutcomeViewModel> Outcomes { get; } = [];

    public ScanViewModel(ScanCoordinator coordinator, ITagReader reader, ILogger<ScanViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _reader = reader;
        _logger = logger;
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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Outcomes.Add(vm);
                    ScannedCount = Outcomes.Count;
                    if (outcome.Status == PipelineStatus.Failed) FailedCount++;
                    // "Würde ändern" counts under the Änderungen banner too — dry-run scans
                    // never reach Written, so without this the user sees "0 Änderungen" every
                    // time despite the rule engine doing meaningful work.
                    else if (vm.StatusLabel is "Geschrieben" or "Würde ändern") ChangedCount++;
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
