using Avalonia.Controls;
using Avalonia.Interactivity;
using RayTagger.Ui.ViewModels;
using System.Linq;

namespace RayTagger.Ui.Views;

/// <summary>
/// Code-behind for <c>MainWindow.axaml</c>. Only does the bare minimum the markup can't do
/// declaratively — forwarding the folder-picker click to the view-model with the current
/// <see cref="TopLevel"/> reference. View-model gets the StorageProvider through that.
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _subscribedVm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Subscribes to the VM's <see cref="MainWindowViewModel.JumpToRowRequested"/> event when the
    /// DataContext arrives (the VM is injected via DI after construction). Unsubscribes from any
    /// prior VM to keep this safe if the context ever swaps — in practice it's set once for the
    /// window's lifetime, but the defensive unsubscribe costs nothing.
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.JumpToRowRequested -= OnJumpToRowRequested;
            _subscribedVm = null;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            vm.JumpToRowRequested += OnJumpToRowRequested;
            _subscribedVm = vm;
        }
    }

    /// <summary>
    /// Switches the tab control back to the Scan tab (index 0) and brings the requested row into
    /// view + selection. Called from the Rule Editor's diff side-panel — the user wants to verify
    /// what the rules did to a specific file without leaving keyboard-navigation distance.
    /// </summary>
    private void OnJumpToRowRequested(object? sender, JumpToRowEventArgs e)
    {
        // Tab index 0 = "Scan-Ergebnisse" — keep this in sync with the XAML order if you ever
        // reorder the tabs.
        RootTabs.SelectedIndex = 0;
        ResultsGrid.ScrollIntoView(e.Row, column: null);
        ResultsGrid.SelectedItem = e.Row;
    }

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.PickSourceFolderAsync(this);
        }
    }

    /// <summary>
    /// Opens the folder picker for the calibration source, runs the calibration via the VM, and
    /// shows a result dialog with the before/after anchors. Lives in code-behind (not the VM)
    /// because the result dialog needs a <see cref="Window"/> owner.
    /// </summary>
    private async void OnCalibrateEnergyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var outcome = await vm.CalibrateEnergyAsync(this);
        if (outcome is null) return;

        string title;
        string body;
        if (outcome.Report.Succeeded)
        {
            title = "Energie-Profil eingefroren";
            body = BuildSuccessMessage(outcome);
        }
        else
        {
            title = "Kalibrierung fehlgeschlagen";
            body = outcome.Report.AbortReason ?? "Unbekannter Fehler.";
        }

        // Reuse ConfirmationDialog as a simple info dialog — confirm button = "Schließen", cancel
        // hidden by reusing same text. Avalonia ships no MessageBox; this is the path of least
        // resistance until we add a dedicated result dialog.
        await ConfirmationDialog.ShowAsync(this, title, body,
            confirmText: "OK", cancelText: "Schließen");
    }

    private static string BuildSuccessMessage(RayTagger.Ui.Services.EnergyCalibrationOutcome outcome)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var p = outcome.Report.Profile!;
        var prev = outcome.PreviousProfile;
        var sb = new System.Text.StringBuilder();
        var failTail = outcome.Report.FailureCount > 0
            ? $", {outcome.Report.FailureCount} fehlgeschlagen"
            : "";
        sb.AppendLine(string.Create(ci, $"Quelle: {outcome.Report.SuccessCount} Tracks erfolgreich analysiert{failTail}."));
        sb.AppendLine(string.Create(ci, $"Gespeichert: {outcome.ProfilePath}"));
        sb.AppendLine();
        sb.AppendLine("Neue Schwellen (Floor → Ceiling)" + (prev is null ? ":" : "  vs. vorher:"));
        Row("spectral_flux",    p.SpectralFlux,    prev?.SpectralFlux);
        Row("beats_loudness",   p.BeatsLoudness,   prev?.BeatsLoudness);
        Row("onset_rate",       p.OnsetRate,       prev?.OnsetRate);
        Row("danceability",     p.Danceability,    prev?.Danceability);
        Row("average_loudness", p.AverageLoudness, prev?.AverageLoudness);
        sb.AppendLine();
        sb.AppendLine("Hinweis: bereits geschriebene ENERGYLEVEL-Tags bleiben unverändert. " +
            "Erneut taggen → Scan starten und „Alle anwenden“ klicken.");
        return sb.ToString();

        void Row(string name, RayTagger.Core.Configuration.FeatureAnchor now, RayTagger.Core.Configuration.FeatureAnchor? prev)
        {
            if (prev is null)
            {
                sb.AppendLine(string.Create(ci, $"  {name,-18} {now.Floor,8:F4} → {now.Ceiling,8:F4}"));
            }
            else
            {
                sb.AppendLine(string.Create(ci,
                    $"  {name,-18} {now.Floor,8:F4} → {now.Ceiling,8:F4}   ({prev.Floor:F4} → {prev.Ceiling:F4})"));
            }
        }
    }

    /// <summary>
    /// Confirms before kicking off the batch Apply. Counting pending rows here (rather than in
    /// the VM) keeps the message specific — "47 Dateien werden geschrieben" beats "alle Änderungen
    /// werden geschrieben" — without coupling the VM to a dialog abstraction.
    /// </summary>
    private async void OnApplyAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var pending = vm.Scan.Outcomes.Count(o => o.CanApply);
        if (pending == 0)
        {
            vm.Scan.StatusMessage = "Keine ausstehenden Änderungen.";
            return;
        }

        var confirmed = await ConfirmationDialog.ShowAsync(this,
            title: "Alle Änderungen anwenden?",
            message: $"{pending} Datei(en) werden geschrieben. Vor jeder Änderung wird ein Backup-Sidecar erzeugt — über Revert pro Zeile wiederherstellbar.");

        if (confirmed)
        {
            await vm.Scan.ApplyAllChangedCommand.ExecuteAsync(parameter: null);
        }
    }

    /// <summary>
    /// Opens the modal applied-rules dialog for the clicked row. Lives here (not in the row VM)
    /// because the dialog needs a <see cref="Window"/> owner reference, which the VM shouldn't
    /// hold. Disabled-button case is already handled by IsEnabled on the button itself — this
    /// method just guards against the rare case where the sender's DataContext is null.
    /// </summary>
    private async void OnRulesButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TrackOutcomeViewModel row })
        {
            await AppliedRulesDialog.ShowAsync(this, row);
        }
    }

    /// <summary>
    /// Switching to the Regeln tab triggers a one-time auto-load of the mappings file the last
    /// scan picked up. Lives here (not in the view's AttachedToVisualTree) because TabControl
    /// instantiates its tab content eagerly — Attach fires before any scan has had a chance to
    /// run, so we'd miss the trigger every time. SelectionChanged on the other hand fires every
    /// switch, and the VM's TryAutoLoad is idempotent when a file is already loaded.
    /// </summary>
    private async void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabControl) return;
        if (tabControl.SelectedItem is not TabItem selected) return;
        if (selected.Header?.ToString() != "Regeln") return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!string.IsNullOrEmpty(vm.RuleEditor.FilePath)) return;

        await vm.RuleEditor.TryAutoLoadFromLastScanAsync();
    }
}
