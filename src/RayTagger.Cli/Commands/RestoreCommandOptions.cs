using System.CommandLine;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Options for the <c>restore</c> verb. Restores tag state from the most recent sidecar backup
/// of one audio file.
/// </summary>
internal sealed class RestoreCommandOptions
{
    public Argument<FileInfo> File { get; } = new("file")
    {
        Description = "Audio file whose tags should be reverted to the most recent sidecar snapshot.",
    };

    public Option<FileInfo?> Sidecar { get; } = new("--sidecar")
    {
        Description = "Restore from a specific sidecar instead of the latest one alongside <file>.",
    };

    public Option<bool> Keep { get; } = new("--keep")
    {
        Description = "Keep the sidecar after a successful restore (default: delete it).",
    };

    public Option<bool> DryRun { get; } = new("--dry-run")
    {
        Description = "Print what would be restored without touching the audio file.",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Enable debug-level logging for this run.",
    };

    public Command Build(Func<System.CommandLine.ParseResult, CancellationToken, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var cmd = new Command("restore", "Revert one file's tags to the most recent sidecar snapshot.")
        {
            File, Sidecar, Keep, DryRun, Verbose,
        };
        cmd.SetAction(handler);
        return cmd;
    }
}
