using System.CommandLine;
using RayTagger.Cli.Commands;

namespace RayTagger.Cli;

/// <summary>
/// CLI entry. Builds the command tree (currently a single <c>scan</c> verb), delegates parsing to
/// System.CommandLine 2.0, and translates parse errors into the appropriate exit code.
/// </summary>
internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        var rootCommand = BuildRootCommand();
        return rootCommand.Parse(args).InvokeAsync();
    }

    internal static RootCommand BuildRootCommand()
    {
        var scanOpts = new ScanCommandOptions();
        var scanCommand = scanOpts.Build((parseResult, ct) =>
            ScanHandler.RunAsync(parseResult, scanOpts, ct));

        var explainOpts = new ExplainCommandOptions();
        var explainCommand = explainOpts.Build((parseResult, ct) =>
            ExplainHandler.RunAsync(parseResult, explainOpts, ct));

        var restoreOpts = new RestoreCommandOptions();
        var restoreCommand = restoreOpts.Build((parseResult, ct) =>
            RestoreHandler.RunAsync(parseResult, restoreOpts, ct));

        var setupOpts = new SetupCommandOptions();
        var setupCommand = setupOpts.Build((parseResult, ct) =>
            SetupHandler.RunAsync(parseResult, setupOpts, ct));

        return new RootCommand("RayTagger — automatic music file tagger (MP3/FLAC/AIFF).")
        {
            scanCommand,
            explainCommand,
            restoreCommand,
            setupCommand,
        };
    }
}
