using Avalonia;
using Avalonia.ReactiveUI;

namespace RayTagger.Ui;

/// <summary>
/// Avalonia desktop entry point. Builds the app with the platform-default backend, the Inter
/// fonts pack, and ReactiveUI plumbing for MVVM property change wiring.
/// </summary>
internal static class Program
{
    // STAThread is required on Windows for OpenFolderPicker / drag-drop. No-op on macOS/Linux.
    [System.STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Avalonia builder entry — also used by the Avalonia design-time tooling.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
