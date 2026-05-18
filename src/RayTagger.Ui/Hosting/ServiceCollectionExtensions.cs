using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RayTagger.Hosting;
using RayTagger.Ui.Services;
using RayTagger.Ui.ViewModels;
using Serilog;
using Serilog.Events;

namespace RayTagger.Ui.Hosting;

/// <summary>
/// DI registration for the Avalonia UI. Layers Serilog logging + UI-specific services
/// (<see cref="ScanCoordinator"/>, view-models, the observable <see cref="UiToolStatusReporter"/>)
/// on top of the shared <see cref="ServiceCollectionComposer"/> from <c>RayTagger.Hosting</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRayTaggerUiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Serilog → Microsoft.Extensions.Logging. File sink keeps a 7-day rolling log under
        // the OS temp dir; the console sink is helpful while the dev loop is running.
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(Path.GetTempPath(), "raytagger-ui", "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
        services.AddLogging(b => b.AddSerilog(logger, dispose: true));

        // Shared pipeline + lookup HttpClient registrations — also brings PipelineFactory.
        services.AddRayTaggerHosting();

        // UI-specific: observable status reporter, scan coordinator, view-models.
        services.AddSingleton<UiToolStatusReporter>();
        services.AddSingleton<IToolStatusReporter>(sp => sp.GetRequiredService<UiToolStatusReporter>());
        services.AddSingleton<ScanCoordinator>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<ScanViewModel>();

        return services;
    }
}
