using System.CommandLine;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Options for the <c>setup</c> verb. Bootstraps the native analysis tools so a subsequent
/// <c>scan</c> can run offline. Idempotent — re-running over already-cached tools is a no-op.
/// </summary>
internal sealed class SetupCommandOptions
{
    public Option<FileInfo?> Config { get; } = new("--config", "-c")
    {
        Description = "Path to tagger.yaml. Defaults to ./tagger.yaml.",
    };

    public Option<bool> Force { get; } = new("--force")
    {
        Description = "Re-download even if a cached copy exists. Useful after manifest changes.",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Enable debug-level logging for this run.",
    };

    public Command Build(Func<System.CommandLine.ParseResult, CancellationToken, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var cmd = new Command("setup",
            "Download the native analysis tools (Essentia, fpcalc) defined in native-tools.yaml into the local cache.")
        {
            Config, Force, Verbose,
        };
        cmd.SetAction(handler);
        return cmd;
    }
}
