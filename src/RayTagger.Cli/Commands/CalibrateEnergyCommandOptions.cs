using System.CommandLine;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Options for the <c>calibrate-energy</c> verb. Walks a folder, runs Essentia per track, and
/// freezes the resulting per-feature 10th/90th-percentile anchors into the calibration profile
/// file (default <c>./energy-calibration.yaml</c> next to <c>tagger.yaml</c>).
/// </summary>
internal sealed class CalibrateEnergyCommandOptions
{
    public Argument<DirectoryInfo> Folder { get; } = new("folder")
    {
        Description = "Folder containing the audio library to calibrate against. Walked recursively.",
    };

    public Option<FileInfo?> Config { get; } = new("--config", "-c")
    {
        Description = "Path to tagger.yaml. Resolution order: --config / $RAYTAGGER_CONFIG / ./tagger.yaml / ./config/tagger.yaml / walk up parents.",
    };

    public Option<FileInfo?> Output { get; } = new("--output", "-o")
    {
        Description = "Override the calibration profile output path. Defaults to analysis.energy.calibration_file from the config.",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Enable debug-level logging for this run.",
    };

    public Command Build(Func<System.CommandLine.ParseResult, CancellationToken, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var cmd = new Command(
            "calibrate-energy",
            "Build a frozen energy calibration profile from a folder's audio library. " +
            "Re-tagging existing files to apply the new profile is the user's responsibility.")
        {
            Folder, Config, Output, Verbose,
        };
        cmd.SetAction(handler);
        return cmd;
    }
}
