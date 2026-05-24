using System.CommandLine;

namespace RayTagger.Cli.Commands;

/// <summary>
/// The set of options exposed by the <c>scan</c> verb. Captured in a single class so the same
/// instances are referenced when building the command tree and when reading values inside the
/// handler — System.CommandLine identifies options by reference, not name.
/// </summary>
internal sealed class ScanCommandOptions
{
    public Option<FileInfo?> Config { get; } = new("--config", "-c")
    {
        Description = "Path to tagger.yaml. Resolution order: --config / $RAYTAGGER_CONFIG / ./tagger.yaml / ./config/tagger.yaml / walk up parents.",
    };

    public Option<DirectoryInfo?> Source { get; } = new("--source", "-s")
    {
        Description = "Override scan.source from the config file.",
    };

    public Option<bool> DryRun { get; } = new("--dry-run")
    {
        Description = "Report changes without writing any tag. Overrides write.dry_run from config.",
    };

    public Option<bool> Write { get; } = new("--write")
    {
        Description = "Force write mode (overrides config dry-run). Mutually exclusive with --dry-run.",
    };

    public Option<bool> ForceOverwrite { get; } = new("--force-overwrite")
    {
        Description = "For this run only, set all per-dimension `existing_confidence` values to 0 " +
                      "(BPM, Key, Energy, Lookup). Existing tags lose their protection — every " +
                      "usable analyzer/lookup hit overrides them. Useful for re-tagging an entire " +
                      "library after a Tagger update without editing tagger.yaml. The config file " +
                      "is not modified.",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Enable debug-level logging for this run.",
    };

    public Command Build(Func<System.CommandLine.ParseResult, CancellationToken, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var cmd = new Command("scan", "Scan a directory and report (or write) the tag changes Tagger would make.")
        {
            Config, Source, DryRun, Write, ForceOverwrite, Verbose,
        };
        cmd.SetAction(handler);
        return cmd;
    }
}
