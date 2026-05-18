using RayTagger.Core.Models;
using Spectre.Console;

namespace RayTagger.Cli.Output;

/// <summary>
/// Renders pipeline outcomes to the console. Uses Spectre.Console for table layout and colour
/// coding so a scan report is scannable at a glance.
/// </summary>
internal sealed class OutcomeRenderer
{
    private readonly IAnsiConsole _console;
    private readonly Table _table;
    private int _written;
    private int _unchanged;
    private int _skipped;
    private int _failed;

    public OutcomeRenderer(IAnsiConsole console)
    {
        _console = console;
        _table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]File[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Fields[/]")
            .AddColumn("[bold]Notes[/]");
    }

    public void Add(PipelineOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var (statusMarkup, counterBucket) = outcome.Status switch
        {
            PipelineStatus.Written => ("[green]Written[/]", 0),
            PipelineStatus.Unchanged => ("[grey]Unchanged[/]", 1),
            PipelineStatus.Skipped => ("[yellow]Skipped[/]", 2),
            PipelineStatus.Failed => ("[red]Failed[/]", 3),
            _ => ("[grey]?[/]", -1),
        };

        switch (counterBucket)
        {
            case 0: _written++; break;
            case 1: _unchanged++; break;
            case 2: _skipped++; break;
            case 3: _failed++; break;
        }

        var fields = outcome.AppliedRules.Count > 0
            ? string.Join(", ", outcome.AppliedRules.SelectMany(r => r.ChangedFields).Distinct())
            : ListChangedFields(outcome);

        var notes = outcome.Errors.Count > 0
            ? string.Join("; ", outcome.Errors.Select(e => Markup.Escape($"{e.Stage}: {e.Message}")))
            : string.Empty;

        _table.AddRow(
            Markup.Escape(Path.GetFileName(outcome.File.Path)),
            statusMarkup,
            Markup.Escape(fields),
            $"[red]{notes}[/]");
    }

    public void Flush()
    {
        _console.Write(_table);

        var summary = new BarChart()
            .Label("[bold]Summary[/]")
            .CenterLabel()
            .AddItem("Written", _written, Color.Green)
            .AddItem("Unchanged", _unchanged, Color.Grey)
            .AddItem("Skipped", _skipped, Color.Yellow)
            .AddItem("Failed", _failed, Color.Red);

        _console.Write(summary);
        _console.WriteLine();
    }

    public int FailureCount => _failed;

    private static string ListChangedFields(PipelineOutcome outcome)
    {
        var changed = new List<string>(5);
        if (outcome.Resolved.Genre.Source != TagFieldSource.Existing) changed.Add("Genre");
        if (outcome.Resolved.SubGenre.Source != TagFieldSource.Existing) changed.Add("SubGenre");
        if (outcome.Resolved.Bpm.Source != TagFieldSource.Existing) changed.Add("Bpm");
        if (outcome.Resolved.Key.Source != TagFieldSource.Existing) changed.Add("Key");
        if (outcome.Resolved.Energy.Source != TagFieldSource.Existing) changed.Add("Energy");
        return changed.Count == 0 ? string.Empty : string.Join(", ", changed);
    }
}
