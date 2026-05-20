using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Metadata;
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
    private readonly ITagReader _reader;
    private readonly IMappingRuleEngine _ruleEngine;
    private readonly ILogger<RuleEditorViewModel> _logger;
    private CancellationTokenSource? _validationCts;
    private string? _lastSavedYaml;

    /// <summary>
    /// Taxonomy discovered next to a manually-loaded <c>mappings.yaml</c> (via a sibling or
    /// parent <c>tagger.yaml</c>). Falls back to the coordinator's last-scan taxonomy when null.
    /// Lets "Gegen Datei testen…" work correctly even before the user has run a full scan.
    /// </summary>
    private Taxonomy? _localTaxonomy;

    private Taxonomy CurrentTaxonomy => _localTaxonomy ?? _coordinator.LastTaxonomy;

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

    /// <summary>
    /// Fires whenever the editor buffer parses to a valid <see cref="MappingRuleSet"/>. The
    /// <see cref="MainWindowViewModel"/> subscribes and forwards the new ruleset to
    /// <see cref="ScanViewModel.UpdatePreview"/> so the results grid re-evaluates against every
    /// already-scanned outcome — no re-read / re-analyze needed.
    /// </summary>
    public event EventHandler<RulePreviewEventArgs>? PreviewRequested;

    public RuleEditorViewModel(
        ScanCoordinator coordinator,
        ITagReader reader,
        IMappingRuleEngine ruleEngine,
        ILogger<RuleEditorViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _reader = reader;
        _ruleEngine = ruleEngine;
        _logger = logger;

        // Auto-load mappings.yaml the moment a scan picks up a config. Decoupled from the tab
        // SelectionChanged event (which was timing-fragile) — the user can switch to Regeln any
        // time after a scan and the editor already has the file. The handler is idempotent: it
        // skips when the editor is dirty or already showing the same file.
        _coordinator.OptionsLoaded += OnCoordinatorOptionsLoaded;
    }

    private async void OnCoordinatorOptionsLoaded(object? sender, EventArgs e)
    {
        try
        {
            await TryAutoLoadFromLastScanAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-load after scan failed");
        }
    }

    /// <summary>
    /// "Gegen Datei testen…" — runs the current editor buffer against a single user-picked audio
    /// file. No analysis, no lookup, no write: just Read + Map, exactly like the CLI's
    /// <c>tagger explain</c> verb. Lets the user verify rule behaviour against a real file before
    /// saving the buffer.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="RuleTestResult"/> carries an <see cref="RuleTestResult.ErrorMessage"/>
    /// when parsing / reading failed. Callers (the view code-behind that opens the result dialog)
    /// should render the error in the same dialog so the user sees what went wrong in context
    /// instead of just seeing the dialog never open.
    /// </remarks>
    public async Task<RuleTestResult> TestAgainstFileAsync(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fileName = Path.GetFileName(filePath);

        var taxonomy = CurrentTaxonomy;
        MappingRuleSet rules;
        try
        {
            rules = MappingRulesLoader.LoadFromString(YamlText, "(editor buffer)", taxonomy);
        }
        catch (ConfigurationException ex)
        {
            return new RuleTestResult(filePath, fileName, Existing: null, Applied: [], Final: null,
                ErrorMessage: $"Regeln im Editor sind ungültig: {ex.Message}");
        }

        if (!File.Exists(filePath))
        {
            return new RuleTestResult(filePath, fileName, Existing: null, Applied: [], Final: null,
                ErrorMessage: $"Datei nicht gefunden: {filePath}");
        }

        TrackTags existing;
        try
        {
            // Reader is synchronous (TagLib# is); push to the threadpool so a slow disk doesn't
            // freeze the UI between picker-close and dialog-open.
            existing = await Task.Run(() => _reader.Read(filePath)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or MetadataException)
        {
            _logger.LogWarning(ex, "Rule test: read failed for {Path}", filePath);
            return new RuleTestResult(filePath, fileName, Existing: null, Applied: [], Final: null,
                ErrorMessage: $"Datei konnte nicht gelesen werden: {ex.Message}");
        }

        var format = AudioFormatDetector.TryDetect(filePath) ?? AudioFormat.Mp3;
        var info = new FileInfo(filePath);
        var trackFile = new TrackFile(filePath, format, info.Length, info.LastWriteTimeUtc);

        var resolved = ResolvedFromExistingBuilder.Build(existing);
        var evaluation = _ruleEngine.Evaluate(resolved, existing, trackFile, rules, taxonomy);

        return new RuleTestResult(filePath, fileName, existing, evaluation.Applied, evaluation.Tags);
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
            // Best-effort: pull in the taxonomy from a tagger.yaml sitting next to the mappings
            // file so "Gegen Datei testen…" works without requiring a prior scan.
            TryLoadLocalTaxonomy(path);
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
    /// scan has run yet — the user can still load manually via the toolbar button. Skips when
    /// the user has unsaved edits or the editor is already showing the same file so a re-scan
    /// doesn't stomp work-in-progress.
    /// </summary>
    public Task TryAutoLoadFromLastScanAsync()
    {
        var path = _coordinator.LastMappingsFile;
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        if (IsDirty) return Task.CompletedTask;
        if (string.Equals(FilePath, path, StringComparison.Ordinal)) return Task.CompletedTask;
        return LoadAsync(path);
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

    /// <summary>
    /// Looks for a <c>tagger.yaml</c> in the directory of the loaded mappings file (and one level
    /// up). If found, runs the full loader so the parsed taxonomy populates <see cref="_localTaxonomy"/>.
    /// Failures are logged at debug level and silently ignored — the editor still works, only the
    /// taxonomy-dependent actions (<c>normalise_genre</c>, <c>enforce</c>) become no-ops, same as
    /// before this feature existed.
    /// </summary>
    private void TryLoadLocalTaxonomy(string mappingsPath)
    {
        _localTaxonomy = null;
        var dir = Path.GetDirectoryName(mappingsPath);
        if (dir is null) return;

        var candidates = new List<string> { Path.Combine(dir, "tagger.yaml") };
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent is not null)
        {
            candidates.Add(Path.Combine(parent, "tagger.yaml"));
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var opts = TaggerOptionsLoader.Load(candidate);
                _localTaxonomy = opts.Taxonomy.Loaded;
                _logger.LogInformation("Local taxonomy loaded from {Path} for rule-editor tests", candidate);
                return;
            }
            catch (Exception ex) when (ex is ConfigurationException or IOException)
            {
                _logger.LogDebug(ex, "Failed to load taxonomy from candidate {Path}", candidate);
            }
        }
    }

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
            // Use the effective taxonomy (local discovery via tagger.yaml next to the mappings
            // file, or the coordinator's last-scan taxonomy as fallback) so `enforce: true`
            // validation matches what a real scan would see.
            var ruleSet = MappingRulesLoader.LoadFromString(YamlText, "(editor buffer)", CurrentTaxonomy);
            StatusMessage = "Regeln gültig.";
            // Live-Preview: hand the parsed ruleset off so the results grid can re-evaluate.
            PreviewRequested?.Invoke(this, new RulePreviewEventArgs(ruleSet));
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

/// <summary>EventArgs payload for <see cref="RuleEditorViewModel.PreviewRequested"/> — wraps the
/// parsed <see cref="MappingRuleSet"/>. Exists because CA1003 wants event-handler T-params to
/// derive from <see cref="EventArgs"/>.</summary>
public sealed class RulePreviewEventArgs(MappingRuleSet ruleSet) : EventArgs
{
    public MappingRuleSet RuleSet { get; } = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
}
