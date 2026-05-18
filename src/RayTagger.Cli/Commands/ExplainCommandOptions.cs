using System.CommandLine;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Options for the <c>explain</c> verb. Captured in a single class so the same instances are
/// referenced when building the command tree and when reading values inside the handler.
/// </summary>
internal sealed class ExplainCommandOptions
{
    public Argument<FileInfo> File { get; } = new("file")
    {
        Description = "Audio file to evaluate the mapping ruleset against.",
    };

    public Option<FileInfo?> Config { get; } = new("--config", "-c")
    {
        Description = "Path to tagger.yaml. Defaults to ./tagger.yaml in the current directory.",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Enable debug-level logging for this run.",
    };

    public Command Build(Func<System.CommandLine.ParseResult, CancellationToken, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var cmd = new Command("explain", "Show which mapping rules would fire for one file and why.")
        {
            File, Config, Verbose,
        };
        cmd.SetAction(handler);
        return cmd;
    }
}
