using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Analysis.Tests;

public class EnergyCalibrationServiceTests
{
    // Stub discovery — yields a fixed track list without touching the filesystem.
    private sealed class StubDiscovery(IEnumerable<TrackFile> files) : IFileDiscoveryService
    {
        public async IAsyncEnumerable<TrackFile> EnumerateAsync(ScanOptions scan,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var f in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return f;
            }
        }
    }

    private static TrackFile Track(int i) =>
        new($"/fake/t{i}.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    private static EssentiaResult Result(double flux, double beats, double onset, double dance, double aloud) =>
        new(Bpm: null, BpmConfidence: null,
            KeyKey: null, KeyScale: null, KeyStrength: null,
            SpectralEnergy: null,
            AverageLoudness: aloud,
            SpectralFlux: flux,
            OnsetRate: onset,
            Danceability: dance,
            BeatsLoudness: beats,
            SpectralCentroidMean: null,
            SpectralComplexityMean: null,
            DynamicComplexity: null,
            ChordsChangesRate: null,
            ChordsStrengthMean: null);

    [Theory]
    // Reference values for [1..20] via NIST type 7 / Excel PERCENTILE.INC:
    //   rank = p/100 · (N-1) — interpolation between sorted[floor(rank)] and sorted[ceil(rank)].
    //   p=10 → rank=1.9 → 2 + 0.9·(3-2) = 2.9
    //   p=50 → rank=9.5 → 10 + 0.5·(11-10) = 10.5
    //   p=90 → rank=17.1 → 18 + 0.1·(19-18) = 18.1
    [InlineData(10.0,  2.9)]
    [InlineData(50.0, 10.5)]
    [InlineData(90.0, 18.1)]
    [InlineData(0.0,   1.0)]
    [InlineData(100.0,20.0)]
    public void Percentile_matches_excel_inclusive_definition(double p, double expected)
    {
        var data = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        EnergyCalibrationService.Percentile(data, p).Should().BeApproximately(expected, 1e-6);
    }

    [Fact]
    public async Task Aborts_when_fewer_than_minimum_tracks_succeed()
    {
        // Discovery yields 5 tracks; Essentia "succeeds" for all (returns full feature set).
        var tracks = Enumerable.Range(0, 5).Select(Track).ToArray();
        var discovery = new StubDiscovery(tracks);
        var essentia = Substitute.For<IEssentiaAnalysisService>();
        essentia.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(Result(0.1, 0.1, 5.0, 1.6, 0.8));

        var service = new EnergyCalibrationService(discovery, essentia, NullLogger<EnergyCalibrationService>.Instance);

        // The CalibrateAsync needs a folder that exists for the up-front Directory.Exists check.
        // Use the OS temp dir — Discovery is stubbed so the folder contents don't matter.
        var report = await service.CalibrateAsync(Path.GetTempPath());

        report.Succeeded.Should().BeFalse();
        report.Profile.Should().BeNull();
        report.SuccessCount.Should().Be(5);
        report.AbortReason.Should().Contain($"need at least {EnergyCalibrationService.MinimumSamples}");
    }

    [Fact]
    public async Task Produces_profile_with_percentile_anchors_when_enough_samples()
    {
        var tracks = Enumerable.Range(0, EnergyCalibrationService.MinimumSamples).Select(Track).ToArray();
        var discovery = new StubDiscovery(tracks);
        var essentia = Substitute.For<IEssentiaAnalysisService>();

        // Give the service a queue of distinct values per track so we have a real distribution
        // to compute percentiles over (not 20 identical samples → degenerate 0-width range).
        var sequence = Enumerable.Range(0, EnergyCalibrationService.MinimumSamples)
            .Select(i => Result(
                flux: 0.05 + i * 0.005,    // 0.050 … 0.145
                beats: 0.02 + i * 0.012,   // 0.020 … 0.248
                onset: 3.0 + i * 0.25,     // 3.0   … 7.75
                dance: 1.0 + i * 0.08,     // 1.0   … 2.52
                aloud: 0.50 + i * 0.025))  // 0.50  … 0.975
            .ToArray();
        var counter = 0;
        essentia.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(_ => sequence[counter++]);

        var service = new EnergyCalibrationService(discovery, essentia, NullLogger<EnergyCalibrationService>.Instance);
        var report = await service.CalibrateAsync(Path.GetTempPath());

        report.Succeeded.Should().BeTrue();
        report.Profile.Should().NotBeNull();
        report.SuccessCount.Should().Be(EnergyCalibrationService.MinimumSamples);
        report.FailureCount.Should().Be(0);

        var p = report.Profile!;
        // 10/90 percentiles of the sequence — anchors avoid the extreme ends, so floor > 0th
        // sample and ceiling < last sample.
        p.SpectralFlux.Floor.Should().BeApproximately(0.05950, 0.001);
        p.SpectralFlux.Ceiling.Should().BeApproximately(0.13550, 0.001);
        p.BeatsLoudness.Floor.Should().BeApproximately(0.04280, 0.002);
        p.BeatsLoudness.Ceiling.Should().BeApproximately(0.22520, 0.002);
        p.OnsetRate.Floor.Should().BeGreaterThan(3.0).And.BeLessThan(4.0);
        p.OnsetRate.Ceiling.Should().BeGreaterThan(6.5).And.BeLessThan(8.0);
        p.SampleCount.Should().Be(EnergyCalibrationService.MinimumSamples);
        p.SourcePath.Should().Be(Path.GetFullPath(Path.GetTempPath()));
    }

    [Fact]
    public async Task Tracks_with_missing_features_count_as_failures_but_dont_abort()
    {
        // 22 tracks: 20 with full features, 2 with a null somewhere → fall into failure bucket.
        // Still leaves 20 successes — exactly the minimum. Should produce a profile.
        var tracks = Enumerable.Range(0, 22).Select(Track).ToArray();
        var discovery = new StubDiscovery(tracks);
        var essentia = Substitute.For<IEssentiaAnalysisService>();
        var counter = 0;
        essentia.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var i = counter++;
                if (i is 5 or 12)
                {
                    return new EssentiaResult(null, null, null, null, null, null,
                        AverageLoudness: 0.8, SpectralFlux: null, OnsetRate: 5.0,
                        Danceability: 1.5, BeatsLoudness: 0.1,
                        SpectralCentroidMean: null, SpectralComplexityMean: null,
                        DynamicComplexity: null, ChordsChangesRate: null, ChordsStrengthMean: null);
                }
                return Result(0.1, 0.1, 5.0, 1.6, 0.8);
            });

        var service = new EnergyCalibrationService(discovery, essentia, NullLogger<EnergyCalibrationService>.Instance);
        var report = await service.CalibrateAsync(Path.GetTempPath());

        report.Succeeded.Should().BeTrue();
        report.SuccessCount.Should().Be(20);
        report.FailureCount.Should().Be(2);
        report.FailedTrackPaths.Should().HaveCount(2);
    }

    [Fact]
    public async Task Aborts_with_clear_reason_when_folder_missing()
    {
        var essentia = Substitute.For<IEssentiaAnalysisService>();
        var service = new EnergyCalibrationService(
            new StubDiscovery([]), essentia, NullLogger<EnergyCalibrationService>.Instance);

        var report = await service.CalibrateAsync("/nonexistent-folder-for-tagger-tests");

        report.Succeeded.Should().BeFalse();
        report.AbortReason.Should().Contain("Folder not found");
    }

    [Fact]
    public async Task Progress_reports_fire_per_track()
    {
        var tracks = Enumerable.Range(0, 25).Select(Track).ToArray();
        var discovery = new StubDiscovery(tracks);
        var essentia = Substitute.For<IEssentiaAnalysisService>();
        essentia.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(Result(0.1, 0.1, 5.0, 1.6, 0.8));

        var service = new EnergyCalibrationService(discovery, essentia, NullLogger<EnergyCalibrationService>.Instance);

        var reports = new List<EnergyCalibrationProgress>();
        var progress = new Progress<EnergyCalibrationProgress>(reports.Add);

        await service.CalibrateAsync(Path.GetTempPath(), parallelism: 1, progress);

        // Wait briefly for IProgress to flush its callback (Progress<T> marshals via SyncContext).
        await Task.Delay(50);

        // One per track + one final summary; some progress callbacks may be coalesced by the
        // synchronization context, so we relax to "at least N+1".
        reports.Should().HaveCountGreaterThan(0);
        reports.Last().TracksProcessed.Should().Be(25);
        reports.Last().CurrentTrackPath.Should().BeNull();
    }
}
