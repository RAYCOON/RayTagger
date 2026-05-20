using Avalonia.Controls;
using Avalonia.Interactivity;
using RayTagger.Ui.ViewModels;

namespace RayTagger.Ui.Views;

/// <summary>
/// Read-only modal that lists every mapping rule that fired for a single row. Triggered by the
/// "Regeln" button in the results grid — the row's <see cref="TrackOutcomeViewModel.AppliedRules"/>
/// would otherwise be hidden behind a comma-joined cell that truncates on long chains.
/// </summary>
public partial class AppliedRulesDialog : Window
{
    public AppliedRulesDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog modal-over <paramref name="owner"/>, bound to <paramref name="row"/>'s
    /// applied-rules list. No transformation — the row VM already exposes the rule names.
    /// </summary>
    public static Task ShowAsync(Window owner, TrackOutcomeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(row);

        var dialog = new AppliedRulesDialog
        {
            DataContext = row,
            Title = $"Angewendete Regeln: {row.FileName}",
        };
        return dialog.ShowDialog(owner);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
