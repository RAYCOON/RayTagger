using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Ui.Services;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// View-model behind the "Regeln" tab. Loads <c>mappings.yaml</c>, validates the buffer against
/// <see cref="MappingRulesLoader"/> on every change (debounced), and writes the buffer back to
/// disk atomically on Save. Live-preview against scan outcomes lives in a follow-up phase —
/// this VM focuses on a robust editing + validation foundation.
/// </summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    private readonly ScanCoordinator _coordinator;
    private readonly ILogger<RuleEditorViewModel> _logger;
    private CancellationTokenSource? _validationCts;
    private string? _lastSavedYaml;

    /// <summary>
    /// Absolute path of the currently-loaded <c>mappings.yaml</c>. Null until the user explicitly
    /// loads one (either via auto-discovery from the last scan or the manual file picker).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadedFile))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private string? _filePath;

    /// <summary>The full YAML buffer the user is editing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _yamlText = string.Empty;

    /// <summary>Banner above the editor — "Bereit", "Wird geladen…", error messages, etc.</summary>
    [ObservableProperty]
    private string _statusMessage = "Keine Regel-Datei geladen.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>Schema/parse errors from the latest validation pass. Empty = valid.</summary>
    public ObservableCollection<RuleValidationError> Errors { get; } = [];

    public bool HasLoadedFile => !string.IsNullOrEmpty(FilePath);
    public bool IsDirty => _lastSavedYaml is not null && !string.Equals(YamlText, _lastSavedYaml, StringComparison.Ordinal);
    public bool IsValid => Errors.Count == 0;

    public RuleEditorViewModel(ScanCoordinator coordinator, ILogger<RuleEditorViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <summary>
    /// Loads <paramref name="path"/> into the editor. Validates immediately so the user sees
    /// existing errors even before they touch anything.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            StatusMessage = $"Datei nicht gefunden: {path}";
            return;
        }

        IsBusy = true;
        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            FilePath = path;
            YamlText = text;
            _lastSavedYaml = text;
            OnPropertyChanged(nameof(IsDirty));
            StatusMessage = $"Geladen: {path}";
            ValidateBuffer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load mappings file {Path}", path);
            StatusMessage = $"Fehler beim Laden: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Tries to auto-load from the most recent scan. Falls back to silently doing nothing if no
    /// scan has run yet — the user can still load manually via the toolbar button.
    /// </summary>
    public Task TryAutoLoadFromLastScanAsync()
    {
        var path = _coordinator.LastMappingsFile;
        return string.IsNullOrEmpty(path) ? Task.CompletedTask : LoadAsync(path);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(FilePath)) return;

        // Validate first — refuse to save invalid YAML so the file on disk is never broken.
        ValidateBuffer();
        if (!IsValid)
        {
            StatusMessage = $"Speichern abgebrochen: {Errors.Count} Validierungsfehler.";
            return;
        }

        IsBusy = true;
        try
        {
            var tempPath = FilePath + ".tagger-edit.tmp";
            await File.WriteAllTextAsync(tempPath, YamlText).ConfigureAwait(true);
            File.Move(tempPath, FilePath, overwrite: true);
            _lastSavedYaml = YamlText;
            OnPropertyChanged(nameof(IsDirty));
            StatusMessage = $"Gespeichert: {FilePath}";
            _logger.LogInformation("Saved mappings.yaml to {Path}", FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save mappings file {Path}", FilePath);
            StatusMessage = $"Fehler beim Speichern: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasLoadedFile))]
    private Task ReloadAsync() => string.IsNullOrEmpty(FilePath) ? Task.CompletedTask : LoadAsync(FilePath);

    private bool CanSave() => HasLoadedFile && IsDirty && !IsBusy && IsValid;

    /// <summary>
    /// Re-validates the buffer with a 300 ms debounce. Cheap synchronous parse (no IO), but the
    /// debounce stops the validator from running on every keystroke during fast typing.
    /// </summary>
    partial void OnYamlTextChanged(string value)
    {
        _validationCts?.Cancel();
        _validationCts = new CancellationTokenSource();
        var token = _validationCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ValidateBuffer);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer keystroke — that's fine.
            }
        }, token);
    }

    private void ValidateBuffer()
    {
        Errors.Clear();
        if (string.IsNullOrWhiteSpace(YamlText))
        {
            StatusMessage = "Leerer Puffer.";
            OnPropertyChanged(nameof(IsValid));
            return;
        }

        try
        {
            // Use the coordinator's last-loaded taxonomy so `enforce: true` validation matches
            // what a scan would see. Falls back to no-taxonomy if we haven't scanned yet.
            MappingRulesLoader.LoadFromString(YamlText, "(editor buffer)", _coordinator.LastTaxonomy);
            StatusMessage = "Regeln gültig.";
        }
        catch (ConfigurationException ex)
        {
            foreach (var err in ex.Errors)
            {
                Errors.Add(new RuleValidationError(err.YamlPath, err.Reason));
            }
            // Fallback when the exception had no structured errors (rare — e.g. a raw YAML parse failure).
            if (Errors.Count == 0)
            {
                Errors.Add(new RuleValidationError("(root)", ex.Message));
            }
            StatusMessage = $"{Errors.Count} Fehler.";
        }
        finally
        {
            OnPropertyChanged(nameof(IsValid));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>One line in the editor's error list. <see cref="YamlPath"/> points at the location
/// inside <c>mappings.yaml</c> (e.g. <c>rules[2].set.mood</c>); <see cref="Message"/> is the
/// human-readable explanation.</summary>
public sealed record RuleValidationError(string YamlPath, string Message);
