using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using RayTagger.Ui.ViewModels;
using TextMateSharp.Grammars;

namespace RayTagger.Ui.Views;

/// <summary>
/// Code-behind for <c>RuleEditorView.axaml</c>. Two responsibilities the markup can't handle:
/// <list type="bullet">
///   <item>Sync the AvaloniaEdit <c>TextEditor</c> with <see cref="RuleEditorViewModel.YamlText"/>.
///         AvaloniaEdit's <c>Text</c> property is a CLR property, not styled — bindings don't
///         work, and the convenience setter has historic flakiness before the editor is
///         measured, so we update <c>Editor.Document</c> directly.</item>
///   <item>Install the TextMate YAML grammar on first load — gives us syntax highlighting that
///         tracks the current theme.</item>
/// </list>
/// </summary>
public partial class RuleEditorView : UserControl
{
    private TextMate.Installation? _textMate;
    private bool _suppressEditorChange;
    private bool _suppressVmChange;
    private RuleEditorViewModel? _boundVm;

    public RuleEditorView()
    {
        InitializeComponent();

        // Install TextMate defensively — if YAML grammar isn't bundled in this TextMateSharp
        // build the constructor would NRE and the whole view would refuse to render. Highlighting
        // is a nice-to-have; falling back to plain text is acceptable.
        try
        {
            var registry = new RegistryOptions(ThemeName.Dark);
            _textMate = Editor.InstallTextMate(registry);
            var lang = registry.GetLanguageByExtension(".yaml");
            if (lang is not null)
            {
                _textMate.SetGrammar(registry.GetScopeByLanguageId(lang.Id));
            }
        }
        catch (Exception)
        {
            // Highlighter unavailable — editor still works as a plain text view.
        }

        Editor.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;

        // Pull the initial DataContext synchronously in case the binding already evaluated before
        // we subscribed (happens when this view is inside an eagerly-instantiated TabItem).
        OnDataContextChanged(this, EventArgs.Empty);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _boundVm = DataContext as RuleEditorViewModel;
        if (_boundVm is null) return;

        _boundVm.PropertyChanged += OnVmPropertyChanged;
        SyncEditorFromVm(_boundVm.YamlText);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_boundVm is null || e.PropertyName != nameof(RuleEditorViewModel.YamlText)) return;
        SyncEditorFromVm(_boundVm.YamlText);
    }

    private void SyncEditorFromVm(string yamlText)
    {
        if (_suppressVmChange) return;
        if (Editor.Document is { } current && current.Text == yamlText) return;

        _suppressEditorChange = true;
        try
        {
            // Setting Document.Text is the reliable update path — the public Text setter has
            // a known history of silently failing before the editor is measured (e.g. the
            // first attach inside a TabItem that hasn't been activated yet). Document is
            // auto-created by the TextEditor constructor so the null branch is defensive.
            if (Editor.Document is null)
            {
                Editor.Document = new TextDocument(yamlText);
            }
            else
            {
                Editor.Document.Text = yamlText;
            }
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorChange || _boundVm is null) return;
        _suppressVmChange = true;
        try
        {
            _boundVm.YamlText = Editor.Text;
        }
        finally
        {
            _suppressVmChange = false;
        }
    }

    /// <summary>
    /// File-picker for opening a <c>mappings.yaml</c>. Routed through the top-level window so the
    /// platform-native picker is used.
    /// </summary>
    private async void OnLoadClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel || _boundVm is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "mappings.yaml öffnen",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("YAML") { Patterns = ["*.yaml", "*.yml"] }],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } localPath)
        {
            await _boundVm.LoadAsync(localPath);
        }
    }
}
