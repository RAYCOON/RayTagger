using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Ui.Hosting;
using RayTagger.Ui.ViewModels;
using RayTagger.Ui.Views;

namespace RayTagger.Ui;

/// <summary>
/// Avalonia <see cref="Application"/> root. Builds the DI container at app startup and hands the
/// main-window view-model a scoped service provider so the rest of the UI can resolve services
/// without going through a service-locator.
/// </summary>
public sealed partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddRayTaggerUiServices();
            _serviceProvider = services.BuildServiceProvider();

            desktop.MainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>(),
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                _serviceProvider?.Dispose();
                _serviceProvider = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
