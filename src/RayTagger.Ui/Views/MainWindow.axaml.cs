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
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.PickSourceFolderAsync(this);
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
