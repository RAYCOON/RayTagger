using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit.TextMate;
using RayTagger.Ui.ViewModels;
using TextMateSharp.Grammars;

namespace RayTagger.Ui.Views;

/// <summary>
/// Code-behind for <c>RuleEditorView.axaml</c>. Two responsibilities the markup can't handle:
/// <list type="bullet">
///   <item>Sync the AvaloniaEdit <c>TextEditor.Text</c> with <see cref="RuleEditorViewModel.YamlText"/>.
///         AvaloniaEdit's <c>Text</c> is a CLR property, not a styled one, so it doesn't bind.</item>
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

        var registry = new RegistryOptions(ThemeName.Dark);
        _textMate = Editor.InstallTextMate(registry);
        _textMate.SetGrammar(registry.GetScopeByLanguageId(registry.GetLanguageByExtension(".yaml").Id));

        Editor.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
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

    /// <summary>
    /// First-time tab activation triggers auto-load from the last scan's mappings file. Cheap
    /// no-op if no scan has run yet or a file is already loaded — see the VM's implementation.
    /// </summary>
    private async void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_boundVm is null || !string.IsNullOrEmpty(_boundVm.FilePath)) return;
        await _boundVm.TryAutoLoadFromLastScanAsync();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_boundVm is null || e.PropertyName != nameof(RuleEditorViewModel.YamlText)) return;
        SyncEditorFromVm(_boundVm.YamlText);
    }

    private void SyncEditorFromVm(string yamlText)
    {
        if (_suppressVmChange) return;
        if (Editor.Document is { } doc && doc.Text == yamlText) return;

        _suppressEditorChange = true;
        try
        {
            Editor.Text = yamlText;
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
