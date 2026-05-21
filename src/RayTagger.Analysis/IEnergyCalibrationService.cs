using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Builds an <see cref="EnergyCalibrationProfile"/> by walking a folder, running Essentia per
/// track, and computing the 10th/90th percentiles of the five composite features. Used by both
/// the CLI (<c>tagger calibrate-energy</c>) and the UI's "Calibrate Energy" button.
/// </summary>
public interface IEnergyCalibrationService
{
    /// <summary>
    /// Minimum number of successfully-analysed tracks required to produce a profile. Below this
    /// the service refuses to write a profile — empirical percentiles from fewer samples are too
    /// noisy to improve over the built-in defaults.
    /// </summary>
    int MinimumSampleCount { get; }

    /// <summary>
    /// Run a calibration pass against <paramref name="folder"/> (recursively).
    /// </summary>
    /// <param name="folder">Absolute path to a directory containing audio files.</param>
    /// <param name="parallelism">
    /// Max concurrent Essentia subprocesses. <c>1</c> = sequential (deterministic ordering for tests).
    /// Production callers should pass <c>options.Scan.EffectiveParallelism</c> so calibration uses
    /// the same worker count as scanning — there's no reason to throttle one but not the other.
    /// Clamped to &gt;= 1.
    /// </param>
    /// <param name="progress">Optional progress sink — fires once per track and once at the end.</param>
    /// <param name="cancellationToken">Cancels the walk; partial state is discarded.</param>
    /// <returns>A report containing the new profile and per-track success/failure counts.</returns>
    Task<EnergyCalibrationReport> CalibrateAsync(
        string folder,
        int parallelism = 1,
        IProgress<EnergyCalibrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-track progress event emitted by the calibration service.</summary>
public sealed record EnergyCalibrationProgress(
    int TracksProcessed,
    int TracksTotal,
    int FailureCount,
    string? CurrentTrackPath);

/// <summary>Outcome of a calibration pass.</summary>
public sealed record EnergyCalibrationReport(
    EnergyCalibrationProfile? Profile,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<string> FailedTrackPaths,
    string? AbortReason)
{
    public bool Succeeded => Profile is not null;
}
