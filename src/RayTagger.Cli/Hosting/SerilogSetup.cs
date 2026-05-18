using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace RayTagger.Cli.Hosting;

/// <summary>
/// Builds a Serilog logger from a <see cref="LoggingOptions"/> block and adapts it into a
/// Microsoft.Extensions.Logging <see cref="ILoggerFactory"/> for use by the rest of the app.
/// </summary>
internal static class SerilogSetup
{
    public static ILoggerFactory Build(LoggingOptions logging, bool verboseOverride)
    {
        ArgumentNullException.ThrowIfNull(logging);

        var level = verboseOverride ? LogEventLevel.Debug : ParseLevel(logging.Level);

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext();

        if (logging.Console)
        {
            config = config.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        if (logging.File.Enabled && !string.IsNullOrWhiteSpace(logging.File.Directory))
        {
            Directory.CreateDirectory(logging.File.Directory);
            var path = Path.Combine(logging.File.Directory, $"tagger-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            config = config.WriteTo.File(
                path,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        var serilogLogger = config.CreateLogger();
        return new SerilogLoggerFactory(serilogLogger, dispose: true);
    }

    private static LogEventLevel ParseLevel(string level) => level.ToUpperInvariant() switch
    {
        "VERBOSE" => LogEventLevel.Verbose,
        "DEBUG" => LogEventLevel.Debug,
        "INFORMATION" => LogEventLevel.Information,
        "WARNING" => LogEventLevel.Warning,
        "ERROR" => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };
}
