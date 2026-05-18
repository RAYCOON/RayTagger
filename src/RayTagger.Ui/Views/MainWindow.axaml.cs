using Avalonia.Controls;
using Avalonia.Interactivity;
using RayTagger.Ui.ViewModels;

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
}
