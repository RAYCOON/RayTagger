using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Core.Configuration;
using RayTagger.Hosting;

namespace RayTagger.Ui.Services;

/// <summary>
/// UI wrapper around <see cref="IEnergyCalibrationService"/>. Mirrors the
/// <see cref="ScanCoordinator"/> shape: locates the relevant <c>tagger.yaml</c>, builds the
/// calibration service via <see cref="PipelineFactory"/>, runs it, and persists the resulting
/// profile to the configured output path. The view-model only deals in folders, progress
/// callbacks, and a report — never with config-loading details.
/// </summary>
public sealed class EnergyCalibrationCoordinator
{
    private readonly PipelineFactory _pipelineFactory;
    private readonly UiToolStatusReporter _statusReporter;
    private readonly ILogger<EnergyCalibrationCoordinator> _logger;

    public EnergyCalibrationCoordinator(
        PipelineFactory pipelineFactory,
        UiToolStatusReporter statusReporter,
        ILogger<EnergyCalibrationCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        ArgumentNullException.ThrowIfNull(statusReporter);
        ArgumentNullException.ThrowIfNull(logger);
        _pipelineFactory = pipelineFactory;
        _statusReporter = statusReporter;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full calibration against <paramref name="folder"/> and freezes the resulting profile
    /// to <c>analysis.energy.calibration_file</c> from the resolved config. The returned report
    /// reflects the actual on-disk state — when <see cref="EnergyCalibrationReport.Succeeded"/> is
    /// <c>true</c>, the profile file exists.
    /// </summary>
    public async Task<EnergyCalibrationOutcome> CalibrateAsync(
        string folder,
        IProgress<EnergyCalibrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _statusReporter.Reset();

        TaggerOptions options;
        try
        {
            options = LoadOrDefaults(folder);
        }
        catch (ConfigurationException ex)
        {
            _logger.LogError(ex, "Calibration: config load failed");
            return new EnergyCalibrationOutcome(
                Report: new EnergyCalibrationReport(null, 0, 0, Array.Empty<string>(), ex.Message),
                ProfilePath: null,
                PreviousProfile: null);
        }

        var outputPath = options.Analysis.Energy.CalibrationFile;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return new EnergyCalibrationOutcome(
                Report: new EnergyCalibrationReport(null, 0, 0, Array.Empty<string>(),
                    "analysis.energy.calibration_file is empty in tagger.yaml — set a path before calibrating."),
                ProfilePath: null,
                PreviousProfile: null);
        }

        EnergyCalibrationProfile? previous = null;
        if (File.Exists(outputPath))
        {
            try { previous = EnergyCalibrationProfileLoader.Load(outputPath); }
            catch (ConfigurationException) { /* unreadable — show empty "previous", overwrite anyway */ }
        }

        var service = await _pipelineFactory.BuildEnergyCalibrationServiceAsync(
            options, _statusReporter, cancellationToken).ConfigureAwait(false);

        if (service is null)
        {
            return new EnergyCalibrationOutcome(
                Report: new EnergyCalibrationReport(null, 0, 0, Array.Empty<string>(),
                    "Essentia is not available. Install it (see docs/INSTALL.md) and try again."),
                ProfilePath: outputPath,
                PreviousProfile: previous);
        }

        // Use the same parallelism setting as the scan pipeline — there's no reason to fan-out
        // differently for calibration than for the actual scan, both bottleneck on the same
        // Essentia subprocess. options.Scan.EffectiveParallelism resolves 0 → ProcessorCount.
        var report = await service.CalibrateAsync(
            folder, options.Scan.EffectiveParallelism, progress, cancellationToken).ConfigureAwait(false);

        if (!report.Succeeded)
        {
            return new EnergyCalibrationOutcome(report, outputPath, previous);
        }

        try
        {
            EnergyCalibrationProfileLoader.Save(outputPath, report.Profile!);
            _logger.LogInformation("Wrote calibration profile to {Path}", outputPath);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to write calibration profile to {Path}", outputPath);
            return new EnergyCalibrationOutcome(
                Report: new EnergyCalibrationReport(report.Profile, report.SuccessCount, report.FailureCount,
                    report.FailedTrackPaths, $"Profile computed but write failed: {ex.Message}"),
                ProfilePath: outputPath,
                PreviousProfile: previous);
        }

        return new EnergyCalibrationOutcome(report, outputPath, previous);
    }

    /// <summary>Minimum sample size enforced by the underlying service — surfaced for the UI banner.</summary>
#pragma warning disable CA1822 // Member surfaced via instance API for forward-compatibility (future scoped min).
    public int MinimumSampleCount => EnergyCalibrationService.MinimumSamples;
#pragma warning restore CA1822

    private static TaggerOptions LoadOrDefaults(string folder)
    {
        // Use the same lookup pattern as ScanCoordinator: tagger.yaml next to the folder, then one
        // up. Returns defaults when nothing found — but those defaults include the default
        // calibration_file path (./energy-calibration.yaml relative to the folder), so calibration
        // still has a sane place to write to.
        var here = Path.Combine(folder, "tagger.yaml");
        var oneUp = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var parent = oneUp is null ? null : Path.Combine(oneUp, "tagger.yaml");
        string? configPath = File.Exists(here) ? here : (parent is not null && File.Exists(parent) ? parent : null);

        if (configPath is not null)
        {
            return TaggerOptionsLoader.Load(configPath);
        }

        // No config — use defaults but anchor the calibration_file at the source folder so the
        // user finds the file where they expect it.
        var defaults = new TaggerOptions();
        defaults.Analysis.Energy.CalibrationFile = Path.Combine(folder, "energy-calibration.yaml");
        return defaults;
    }
}

/// <summary>Result of an UI-driven calibration call.</summary>
public sealed record EnergyCalibrationOutcome(
    EnergyCalibrationReport Report,
    string? ProfilePath,
    EnergyCalibrationProfile? PreviousProfile);
