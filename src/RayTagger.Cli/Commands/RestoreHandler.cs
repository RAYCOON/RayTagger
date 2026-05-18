using System.CommandLine;
using RayTagger.Cli.Hosting;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Metadata;
using Spectre.Console;
using FsFile = System.IO.File;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Implements the <c>restore</c> verb: reads a sidecar YAML created by an earlier write, rebuilds
/// the <see cref="TrackTags"/> snapshot it captured, and writes those values back onto the file.
/// The current sidecar is removed after a successful restore (unless <c>--keep</c>).
/// </summary>
/// <remarks>
/// Restore uses <see cref="TagFieldSource.Rules"/> as the field source so the writer treats every
/// value as "must write" — semantically restore IS a forced overwrite of the prior state, which
/// matches the Rules invariant (Rules always wins, regardless of policy). No fresh sidecar is
/// taken during restore (would just snapshot the post-write state we're undoing).
/// </remarks>
internal static class RestoreHandler
{
    public static async Task<int> RunAsync(ParseResult parseResult, RestoreCommandOptions opts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(opts);

        var fileArg = parseResult.GetValue(opts.File);
        var sidecarOverride = parseResult.GetValue(opts.Sidecar);
        var keep = parseResult.GetValue(opts.Keep);
        var dryRun = parseResult.GetValue(opts.DryRun);
        var verbose = parseResult.GetValue(opts.Verbose);

        var console = AnsiConsole.Console;

        if (fileArg is null || !fileArg.Exists)
        {
            console.MarkupLine("[red]File not found:[/] " + Markup.Escape(fileArg?.FullName ?? "<missing>"));
            return ExitCodes.InvalidArguments;
        }

        using var loggerFactory = SerilogSetup.Build(new LoggingOptions(), verboseOverride: verbose);

        var sidecarService = new SidecarRestoreService();
        var sidecarPath = sidecarOverride?.FullName ?? sidecarService.FindLatestSidecar(fileArg.FullName);
        if (sidecarPath is null || !FsFile.Exists(sidecarPath))
        {
            console.MarkupLine("[yellow]No sidecar found for[/] " + Markup.Escape(fileArg.FullName));
            return ExitCodes.GenericFailure;
        }

        TrackTags snapshot;
        try
        {
            snapshot = sidecarService.Read(sidecarPath);
        }
        catch (SidecarRestoreException ex)
        {
            console.MarkupLine("[red]Sidecar read failed:[/] " + Markup.Escape(ex.Message));
            return ExitCodes.GenericFailure;
        }

        console.MarkupLine($"[bold]restore[/]  [cyan]{Markup.Escape(fileArg.FullName)}[/]");
        console.MarkupLine($"  from sidecar [grey]{Markup.Escape(Path.GetFileName(sidecarPath))}[/]");
        RenderSnapshot(console, snapshot);

        if (dryRun)
        {
            console.MarkupLine("[yellow]--dry-run: nothing written.[/]");
            return ExitCodes.Success;
        }

        var reader = new TagLibTagReader();
        var backupWriter = new BackupSidecarWriter();
        var writer = new TagLibTagWriter(reader, backupWriter);

        var resolved = ToResolvedTrackTags(snapshot);
        try
        {
            // Backup=false: we're undoing, no point capturing the post-restore state.
            writer.Write(fileArg.FullName, resolved, new TagWriteOptions(DryRun: false, Backup: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            console.MarkupLine("[red]Restore failed:[/] " + Markup.Escape(ex.Message));
            return ExitCodes.GenericFailure;
        }

        if (!keep)
        {
            try
            {
                FsFile.Delete(sidecarPath);
                console.MarkupLine($"[green]✓[/] restored and removed sidecar.");
            }
            catch (IOException ex)
            {
                console.MarkupLine($"[yellow]Restored, but sidecar removal failed:[/] {Markup.Escape(ex.Message)}");
            }
        }
        else
        {
            console.MarkupLine($"[green]✓[/] restored (sidecar kept).");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static ResolvedTrackTags ToResolvedTrackTags(TrackTags snapshot)
    {
        // Source = Rules with confidence 1 means "must write, regardless of policy" — exactly
        // the restore semantics. Custom fields are rebuilt identically.
        var custom = snapshot.Custom.ToDictionary(
            kv => kv.Key,
            kv => new ResolvedField<string>(kv.Value, TagFieldSource.Rules, 1.0),
            StringComparer.OrdinalIgnoreCase);

        return new ResolvedTrackTags(
            Genre: new ResolvedField<string>(snapshot.Genre, TagFieldSource.Rules, 1.0),
            SubGenre: new ResolvedField<string>(snapshot.SubGenre, TagFieldSource.Rules, 1.0),
            Bpm: new ResolvedValueField<double>(snapshot.Bpm, TagFieldSource.Rules, 1.0),
            Key: new ResolvedField<MusicalKey>(snapshot.Key, TagFieldSource.Rules, 1.0),
            Energy: new ResolvedValueField<int>(snapshot.Energy, TagFieldSource.Rules, 1.0),
            Mood: new ResolvedField<string>(snapshot.Mood, TagFieldSource.Rules, 1.0),
            SetPosition: new ResolvedField<string>(snapshot.SetPosition, TagFieldSource.Rules, 1.0),
            Custom: custom);
    }

    private static void RenderSnapshot(IAnsiConsole console, TrackTags snapshot)
    {
        var table = new Table().NoBorder();
        table.AddColumn("Field");
        table.AddColumn("Value to restore");
        table.AddRow("artist", Markup.Escape(snapshot.Artist ?? "—"));
        table.AddRow("title", Markup.Escape(snapshot.Title ?? "—"));
        table.AddRow("genre", Markup.Escape(snapshot.Genre ?? "—"));
        table.AddRow("subgenre", Markup.Escape(snapshot.SubGenre ?? "—"));
        table.AddRow("bpm", snapshot.Bpm?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        table.AddRow("key", Markup.Escape(snapshot.Key?.Standard ?? "—"));
        table.AddRow("energy", snapshot.Energy?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—");
        if (snapshot.Custom.Count > 0)
        {
            table.AddRow("custom", Markup.Escape(string.Join(", ", snapshot.Custom.Keys)));
        }
        console.Write(table);
    }
}
