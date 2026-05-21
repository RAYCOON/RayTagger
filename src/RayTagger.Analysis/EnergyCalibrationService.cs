using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Analysis;

/// <summary>
/// Default <see cref="IEnergyCalibrationService"/>. Walks a folder via the shared
/// <see cref="IFileDiscoveryService"/>, runs <see cref="IEssentiaAnalysisService"/> per file, and
/// computes per-feature 10th/90th percentile anchors over the successful samples.
/// </summary>
/// <remarks>
/// <para><b>Why percentiles, not min/max:</b> a single outlier track (e.g. an unmastered demo
/// with abnormally low loudness) would otherwise pin the floor to a value no representative
/// track ever reaches, squashing the bucket distribution. 10/90 leaves headroom on both ends
/// while still being driven by typical material.</para>
///
/// <para><b>Why the 20-track minimum:</b> below this sample size, the percentile estimate is
/// dominated by single-track variance and routinely produces anchors worse than the built-in
/// defaults. Refusing to write rather than emitting a bad profile is the safer failure mode.</para>
///
/// <para>The service uses the file-discovery service's defaults: MP3/FLAC/AIFF, recursive,
/// skipping hidden directories. Per-track Essentia failures are logged but never abort the
/// pass — a 5% failure rate on a large library is normal (corrupt files, container quirks).</para>
/// </remarks>
public sealed class EnergyCalibrationService : IEnergyCalibrationService
{
    public const int MinimumSamples = 20;
    private const double LowerPercentile = 10.0;
    private const double UpperPercentile = 90.0;

    private readonly IFileDiscoveryService _discovery;
    private readonly IEssentiaAnalysisService _essentia;
    private readonly ILogger<EnergyCalibrationService> _logger;

    public EnergyCalibrationService(
        IFileDiscoveryService discovery,
        IEssentiaAnalysisService essentia,
        ILogger<EnergyCalibrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(essentia);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _essentia = essentia;
        _logger = logger;
    }

    public int MinimumSampleCount => MinimumSamples;

    public async Task<EnergyCalibrationReport> CalibrateAsync(
        string folder,
        int parallelism = 1,
        IProgress<EnergyCalibrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (!Directory.Exists(folder))
        {
            return new EnergyCalibrationReport(
                Profile: null, SuccessCount: 0, FailureCount: 0,
                FailedTrackPaths: Array.Empty<string>(),
                AbortReason: $"Folder not found: {folder}");
        }

        var absoluteFolder = Path.GetFullPath(folder);
        var scan = new ScanOptions
        {
            Source = absoluteFolder,
            Recursive = true,
            Formats = ["mp3", "flac", "aiff"],
        };

        // Fan-out N Essentia subprocesses (matches the scan pipeline's worker count). ConcurrentBag
        // is the right shape for unordered append-only collection — we sort once at the end for
        // percentile computation, so unordered insertion is fine and lock-free.
        var fluxes = new ConcurrentBag<double>();
        var beats = new ConcurrentBag<double>();
        var onsets = new ConcurrentBag<double>();
        var dances = new ConcurrentBag<double>();
        var louds = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<string>();
        var processed = 0;
        var failureCount = 0;

        var effectiveParallelism = Math.Max(1, parallelism);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = effectiveParallelism,
            CancellationToken = cancellationToken,
        };

        _logger.LogInformation(
            "Calibration starting against {Folder} with parallelism {Parallelism}.",
            absoluteFolder, effectiveParallelism);

        // We don't know the total up front because IFileDiscoveryService streams. Progress
        // reports use total=0 until the walk completes; UI consumers should display a
        // "scanned N of ?" string while total is 0.
        await Parallel.ForEachAsync(
            _discovery.EnumerateAsync(scan, cancellationToken),
            parallelOptions,
            async (file, ct) =>
            {
                EssentiaResult? result = null;
                try
                {
                    result = await _essentia.RunAsync(file, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Calibration: Essentia failed on {Path}", file.Path);
                }

                var p = Interlocked.Increment(ref processed);
                if (TryCollect(result, fluxes, beats, onsets, dances, louds))
                {
                    progress?.Report(new EnergyCalibrationProgress(p, 0, Volatile.Read(ref failureCount), file.Path));
                }
                else
                {
                    failures.Add(file.Path);
                    var f = Interlocked.Increment(ref failureCount);
                    progress?.Report(new EnergyCalibrationProgress(p, 0, f, file.Path));
                }
            }).ConfigureAwait(false);

        var successCount = processed - failures.Count;
        progress?.Report(new EnergyCalibrationProgress(processed, processed, failures.Count, null));

        if (successCount < MinimumSamples)
        {
            var reason = successCount == 0
                ? "No audio files found or analysed in the selected folder."
                : $"Only {successCount} tracks analysed successfully; need at least {MinimumSamples} for a stable profile.";
            return new EnergyCalibrationReport(
                Profile: null,
                SuccessCount: successCount,
                FailureCount: failures.Count,
                FailedTrackPaths: failures.ToArray(),
                AbortReason: reason);
        }

        var profile = new EnergyCalibrationProfile
        {
            SpectralFlux = AnchorFrom(fluxes.ToArray()),
            BeatsLoudness = AnchorFrom(beats.ToArray()),
            OnsetRate = AnchorFrom(onsets.ToArray()),
            Danceability = AnchorFrom(dances.ToArray()),
            AverageLoudness = AnchorFrom(louds.ToArray()),
            SampleCount = successCount,
            GeneratedAt = DateTime.UtcNow,
            SourcePath = absoluteFolder,
        };

        _logger.LogInformation(
            "Calibration complete: {Success}/{Total} tracks ({Failures} failures). " +
            "flux=[{FluxLo:F3},{FluxHi:F3}] beats=[{BeatsLo:F3},{BeatsHi:F3}] " +
            "onset=[{OnsetLo:F2},{OnsetHi:F2}] dance=[{DanceLo:F2},{DanceHi:F2}] " +
            "aloud=[{LoudLo:F2},{LoudHi:F2}]",
            successCount, processed, failures.Count,
            profile.SpectralFlux.Floor, profile.SpectralFlux.Ceiling,
            profile.BeatsLoudness.Floor, profile.BeatsLoudness.Ceiling,
            profile.OnsetRate.Floor, profile.OnsetRate.Ceiling,
            profile.Danceability.Floor, profile.Danceability.Ceiling,
            profile.AverageLoudness.Floor, profile.AverageLoudness.Ceiling);

        return new EnergyCalibrationReport(profile, successCount, failures.Count, failures.ToArray(), AbortReason: null);
    }

    // Returns true when *all five* features were present — the calibration only counts a track if
    // every feature contributes, so all anchors are derived from the same sample set.
    // ConcurrentBag<T>.Add is lock-free and thread-safe; we don't care about insertion order
    // because percentiles operate on a sorted snapshot.
    private static bool TryCollect(
        EssentiaResult? r,
        ConcurrentBag<double> fluxes, ConcurrentBag<double> beats, ConcurrentBag<double> onsets,
        ConcurrentBag<double> dances, ConcurrentBag<double> louds)
    {
        if (r is null) return false;
        if (r.SpectralFlux is null || r.BeatsLoudness is null || r.OnsetRate is null
            || r.Danceability is null || r.AverageLoudness is null) return false;

        fluxes.Add(r.SpectralFlux.Value);
        beats.Add(r.BeatsLoudness.Value);
        onsets.Add(r.OnsetRate.Value);
        dances.Add(r.Danceability.Value);
        louds.Add(r.AverageLoudness.Value);
        return true;
    }

    private static FeatureAnchor AnchorFrom(double[] samples)
    {
        Array.Sort(samples);
        return new FeatureAnchor
        {
            Floor = Percentile(samples, LowerPercentile),
            Ceiling = Percentile(samples, UpperPercentile),
        };
    }

    /// <summary>
    /// Linear-interpolation percentile (NIST type 7 / Excel PERCENTILE.INC). Internal so tests
    /// can pin the math without going through a real Essentia run.
    /// </summary>
    internal static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        var rank = p / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];

        var fraction = rank - lower;
        return sorted[lower] + fraction * (sorted[upper] - sorted[lower]);
    }
}
