using RayTagger.Analysis.Bootstrap;
using RayTagger.Hosting;
using Spectre.Console;

namespace RayTagger.Cli.Hosting;

/// <summary>
/// <see cref="IToolStatusReporter"/> backed by Spectre.Console — the CLI's coloured banner lines
/// at scan startup. The UI gets a different implementation that pushes status entries into an
/// observable collection bound to a panel.
/// </summary>
internal sealed class SpectreToolStatusReporter : IToolStatusReporter
{
    private readonly IAnsiConsole _console;

    public SpectreToolStatusReporter(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public void ReportTool(string dimension, string provider, NativeToolResolution resolution)
    {
        var version = string.IsNullOrWhiteSpace(resolution.Probe.Version) ? "(version unknown)" : resolution.Probe.Version;
        var sourceTag = resolution.Source switch
        {
            NativeToolResolutionSource.Path => "[grey](PATH)[/]",
            NativeToolResolutionSource.Cache => "[grey](cached)[/]",
            NativeToolResolutionSource.Downloaded => "[yellow](downloaded)[/]",
            _ => string.Empty,
        };
        _console.MarkupLine(
            $"[green]✓[/] [bold]{dimension}[/] via [cyan]{Markup.Escape(provider)}[/] [grey]{Markup.Escape(version)}[/] {sourceTag}");
    }

    public void ReportMissing(string dimension, string provider, string detail)
    {
        var d = string.IsNullOrWhiteSpace(detail) ? "not on PATH" : detail;
        _console.MarkupLine(
            $"[yellow]✗[/] [bold]{dimension}[/] via [cyan]{Markup.Escape(provider)}[/] disabled — [grey]{Markup.Escape(d)}[/]");
    }

    public void ReportLookupProvider(string name, bool available, string? detail = null)
    {
        if (available)
        {
            _console.MarkupLine($"[green]✓[/] [bold]lookup[/] via [cyan]{Markup.Escape(name)}[/]");
        }
        else
        {
            var d = string.IsNullOrWhiteSpace(detail) ? "disabled" : detail;
            _console.MarkupLine($"[grey]·[/] [bold]lookup[/] via [cyan]{Markup.Escape(name)}[/] — [grey]{Markup.Escape(d)}[/]");
        }
    }

    public void ReportNote(string message)
    {
        _console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }
}
