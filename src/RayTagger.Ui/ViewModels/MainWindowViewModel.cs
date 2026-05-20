using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RayTagger.Ui.Services;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Root view-model. Currently a thin shell owning the <see cref="ScanViewModel"/>, the
/// <see cref="UiToolStatusReporter"/> the status panel binds to, and the folder picker.
/// Will grow rule-editor / settings child VMs in later iterations.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;

    public ScanViewModel Scan { get; }
    public RuleEditorViewModel RuleEditor { get; }
    public UiToolStatusReporter ToolStatus { get; }

    public MainWindowViewModel(
        ScanViewModel scan,
        RuleEditorViewModel ruleEditor,
        UiToolStatusReporter toolStatus,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(ruleEditor);
        ArgumentNullException.ThrowIfNull(toolStatus);
        ArgumentNullException.ThrowIfNull(logger);
        Scan = scan;
        RuleEditor = ruleEditor;
        ToolStatus = toolStatus;
        _logger = logger;

        // Live-Preview bridge: when the user types a valid rule edit, the editor parses it and
        // hands the new ruleset off to the scan view-model, which re-evaluates each cached
        // outcome and pushes the refreshed proposed values back to the results grid.
        RuleEditor.PreviewRequested += (_, args) => Scan.UpdatePreview(args.RuleSet);
    }

    /// <summary>
    /// Raised when the user clicks a row in the Rule Editor's diff side-panel. The
    /// <see cref="Views.MainWindow"/> code-behind subscribes and handles the actual
    /// tab-switch + grid-scroll because those interactions need <see cref="TabControl"/> and
    /// <see cref="Avalonia.Controls.DataGrid"/> references the VM shouldn't own.
    /// </summary>
    public event EventHandler<JumpToRowEventArgs>? JumpToRowRequested;

    [RelayCommand]
    private void JumpToRow(TrackOutcomeViewModel? row)
    {
        if (row is null) return;
        JumpToRowRequested?.Invoke(this, new JumpToRowEventArgs(row));
    }

    /// <summary>
    /// Opens the platform folder picker (Avalonia routes to Cocoa/WinUI/Gtk per host). Selected
    /// path becomes the scan source. Bound to a button click via x:Static reference to the
    /// command name; the View injects its own <see cref="TopLevel"/> when invoking.
    /// </summary>
    [RelayCommand]
    public async Task PickSourceFolderAsync(TopLevel? topLevel)
    {
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Musik-Ordner auswählen",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } localPath)
        {
            Scan.SourceDirectory = localPath;
            _logger.LogInformation("Source folder selected: {Path}", localPath);
        }
    }
}

/// <summary>EventArgs payload for <see cref="MainWindowViewModel.JumpToRowRequested"/> — wraps the
/// row VM so CA1003's EventArgs-derived T constraint is satisfied.</summary>
public sealed class JumpToRowEventArgs(TrackOutcomeViewModel row) : EventArgs
{
    public TrackOutcomeViewModel Row { get; } = row ?? throw new ArgumentNullException(nameof(row));
}
