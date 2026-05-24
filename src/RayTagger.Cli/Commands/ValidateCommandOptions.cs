using System.CommandLine;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Options for the <c>validate</c> verb. Runs the full tagging pipeline in dry-run mode against a
/// reference library and produces a per-dimension accuracy report. Read-only by design — never
/// writes to the reference files.
/// </summary>
internal sealed class ValidateCommandOptions
{
    public Argument<DirectoryInfo> Reference { get; } = new("reference")
    {
        Description = "Reference library root. Genre is extracted from the immediate subfolder; " +
                      "subgenre from a sub-subfolder if present. BPM/Key/Energy truth comes from " +
                      "the Mixed-In-Key comment tag on each file.",
    };

    public Option<FileInfo?> Config { get; } = new("--config", "-c")
    {
        Description = "Path to tagger.yaml. Same resolution order as the scan verb.",
    };

    public Option<FileInfo?> Output { get; } = new("--output", "-o")
    {
        Description = "JSON report path. Default: ./reports/backtest-{timestamp}.json. " +
                      "The Markdown summary is written next to it with .md extension.",
    };

    public Option<int> Take { get; } = new("--take")
    {
        Description = "Limit the run to the first N files (for fast iteration). 0 = all files.",
        DefaultValueFactory = _ => 0,
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Enable debug-level logging for this run.",
    };

    public Command Build(Func<System.CommandLine.ParseResult, CancellationToken, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var cmd = new Command(
            "validate",
            "Run the tagging pipeline read-only against a reference library and report per-dimension " +
            "accuracy. Genre/SubGenre truth from folder structure, BPM/Key/Energy truth from " +
            "Mixed-In-Key comment-tags. Never writes to the reference files.")
        {
            Reference, Config, Output, Take, Verbose,
        };
        cmd.SetAction(handler);
        return cmd;
    }
}
