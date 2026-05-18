using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RayTagger.Ui.Views;

/// <summary>
/// Minimal yes/no modal. Avalonia ships no built-in MessageBox; this is the smallest possible
/// stand-in for "user, really do this?" prompts. Callers use <see cref="ShowAsync"/> instead of
/// the raw window so they don't have to remember the result-via-Close pattern.
/// </summary>
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows a modal yes/no dialog over <paramref name="owner"/>. Resolves to <c>true</c> when the
    /// user clicks the confirm button, <c>false</c> when they cancel or close the window.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message,
        string confirmText = "Ja, anwenden", string cancelText = "Abbrechen")
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dialog = new ConfirmationDialog { Title = title };
        dialog.MessageBlock.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
