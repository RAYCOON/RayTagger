using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class EssentiaEnergyAnalyzerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    // Helper — build an EssentiaResult with only the five energy features, BPM/Key set to null
    // (the analyzer ignores them). Defaults are mid-range so a single feature override drives
    // the test scenario cleanly.
    private static EssentiaResult ER(
        double? flux = 0.10,
        double? beatsLoud = 0.10,
        double? onset = 5.0,
        double? dance = 1.6,
        double? aloud = 0.80,
        double? spectralEnergy = null) =>
        new(Bpm: null, BpmConfidence: null,
            KeyKey: null, KeyScale: null, KeyStrength: null,
            SpectralEnergy: spectralEnergy,
            AverageLoudness: aloud,
            SpectralFlux: flux,
            OnsetRate: onset,
            Danceability: dance,
            BeatsLoudness: beatsLoud,
            SpectralCentroidMean: null,
            SpectralComplexityMean: null,
            DynamicComplexity: null,
            ChordsChangesRate: null,
            ChordsStrengthMean: null);

    [Fact]
    public void Floor_anchors_for_every_feature_yield_bucket_1()
    {
        // Each feature at or below its floor → normalised 0 → composite 0 → bucket 1.
        var (bucket, conf) = EssentiaEnergyAnalyzer.Compose(
            ER(flux: 0.05, beatsLoud: 0.02, onset: 3.0, dance: 1.0, aloud: 0.50));

        bucket.Should().Be(1);
        conf.Should().BeApproximately(0.80, 0.001);
    }

    [Fact]
    public void Ceiling_anchors_for_every_feature_yield_bucket_10()
    {
        var (bucket, conf) = EssentiaEnergyAnalyzer.Compose(
            ER(flux: 0.15, beatsLoud: 0.25, onset: 7.5, dance: 2.5, aloud: 1.0));

        bucket.Should().Be(10);
        conf.Should().BeApproximately(0.80, 0.001);
    }

    [Theory]
    // Values above ceiling clamp; values below floor clamp. Same as ceiling/floor anchors.
    [InlineData(0.300, 0.500, 10.0, 3.0, 1.5, 10)]
    [InlineData(0.001, 0.001, 0.5,  0.5, 0.0, 1)]
    public void Out_of_range_features_clamp_to_floor_or_ceiling(
        double flux, double beats, double onset, double dance, double aloud, int expectedBucket)
    {
        var (bucket, _) = EssentiaEnergyAnalyzer.Compose(
            ER(flux: flux, beatsLoud: beats, onset: onset, dance: dance, aloud: aloud));

        bucket.Should().Be(expectedBucket);
    }

    [Theory]
    // Reference-library calibration spot-checks — captured from the 35-track DJ library used to
    // tune the anchors. Names in comments are subjective expectations, not test assertions.
    // Order: flux, beatsLoudness, onset, dance, aloud, expectedBucketRange (inclusive)
    [InlineData(0.060, 0.039, 3.70, 1.21, 0.816, 2, 3)] // Alix Perez - Ghosts (chillout DnB)
    [InlineData(0.083, 0.057, 5.50, 1.53, 0.901, 3, 5)] // Akshin Alizadeh - La Venganza (downtempo)
    [InlineData(0.118, 0.129, 5.06, 1.78, 0.958, 4, 6)] // Canu - Mariposa (organic house)
    [InlineData(0.120, 0.208, 4.91, 1.94, 0.966, 6, 8)] // Phenomenon (techno)
    [InlineData(0.136, 0.290, 5.76, 1.76, 0.945, 7, 9)] // On The Streets (prog house — banger)
    public void Real_library_tracks_land_in_expected_bucket_range(
        double flux, double beats, double onset, double dance, double aloud, int lo, int hi)
    {
        var (bucket, _) = EssentiaEnergyAnalyzer.Compose(
            ER(flux: flux, beatsLoud: beats, onset: onset, dance: dance, aloud: aloud));

        bucket.Should().NotBeNull();
        bucket!.Value.Should().BeInRange(lo, hi);
    }

    [Fact]
    public void Missing_lowest_weight_feature_does_not_change_bucket_meaningfully()
    {
        // average_loudness has only 0.10 weight — dropping it should keep the bucket within ±1.
        var withAll = EssentiaEnergyAnalyzer.Compose(
            ER(flux: 0.10, beatsLoud: 0.10, onset: 5.0, dance: 1.6, aloud: 0.80));
        var withoutLoudness = EssentiaEnergyAnalyzer.Compose(
            ER(flux: 0.10, beatsLoud: 0.10, onset: 5.0, dance: 1.6, aloud: null));

        withAll.Bucket.Should().NotBeNull();
        withoutLoudness.Bucket.Should().NotBeNull();
        Math.Abs(withAll.Bucket!.Value - withoutLoudness.Bucket!.Value).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Missing_features_reduce_confidence_proportionally()
    {
        // Drop danceability (weight 0.15) → coverage 0.85 → confidence 0.80 * 0.85 = 0.68.
        var (_, conf) = EssentiaEnergyAnalyzer.Compose(
            ER(dance: null));

        conf.Should().BeApproximately(0.80 * 0.85, 0.001);
    }

    [Fact]
    public void Returns_null_bucket_when_feature_coverage_below_minimum()
    {
        // Only average_loudness available (weight 0.10). Below the 0.40 coverage floor.
        var (bucket, conf) = EssentiaEnergyAnalyzer.Compose(new EssentiaResult(
            null, null, null, null, null, null,
            AverageLoudness: 0.9,
            SpectralFlux: null, OnsetRate: null, Danceability: null, BeatsLoudness: null,
            SpectralCentroidMean: null, SpectralComplexityMean: null, DynamicComplexity: null,
            ChordsChangesRate: null, ChordsStrengthMean: null));

        bucket.Should().BeNull();
        conf.Should().Be(0);
    }

    [Fact]
    public void Returns_null_bucket_when_all_features_missing()
    {
        var (bucket, conf) = EssentiaEnergyAnalyzer.Compose(new EssentiaResult(
            null, null, null, null, null, null,
            AverageLoudness: null, SpectralFlux: null,
            OnsetRate: null, Danceability: null, BeatsLoudness: null,
            SpectralCentroidMean: null, SpectralComplexityMean: null, DynamicComplexity: null,
            ChordsChangesRate: null, ChordsStrengthMean: null));

        bucket.Should().BeNull();
        conf.Should().Be(0);
    }

    [Fact]
    public async Task End_to_end_returns_bucket_for_typical_track()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(ER(flux: 0.12, beatsLoud: 0.15, onset: 5.5, dance: 1.8, aloud: 0.92));

        var analyzer = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Energy.Should().NotBeNull();
        result.Energy!.Value.Should().BeInRange(5, 8);
        result.Confidence.Should().BeApproximately(0.80, 0.001);
    }

    [Fact]
    public async Task End_to_end_returns_empty_when_service_returns_null()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns((EssentiaResult?)null);

        var analyzer = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Energy.Should().BeNull();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task End_to_end_returns_empty_when_features_below_coverage_threshold()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(
                null, null, null, null, null, null,
                AverageLoudness: 0.9, SpectralFlux: null,
                OnsetRate: null, Danceability: null, BeatsLoudness: null,
                SpectralCentroidMean: null, SpectralComplexityMean: null, DynamicComplexity: null,
                ChordsChangesRate: null, ChordsStrengthMean: null));

        var analyzer = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Energy.Should().BeNull();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Custom_calibration_profile_widens_the_dynamic_range()
    {
        // Profile with a much wider range than the defaults — same input values should now land
        // in a much lower bucket because they're closer to the new floor.
        var profile = new EnergyCalibrationProfile
        {
            // Defaults: [0.05, 0.15]; here we widen to [0.05, 0.30] → flux 0.12 maps to
            // (0.12-0.05)/(0.30-0.05) = 0.28 instead of 0.70
            SpectralFlux    = new FeatureAnchor { Floor = 0.050, Ceiling = 0.300 },
            BeatsLoudness   = new FeatureAnchor { Floor = 0.020, Ceiling = 0.500 },
            OnsetRate       = new FeatureAnchor { Floor = 3.0,   Ceiling = 15.0 },
            Danceability    = new FeatureAnchor { Floor = 1.0,   Ceiling = 4.0 },
            AverageLoudness = new FeatureAnchor { Floor = 0.50,  Ceiling = 1.00 },
            SampleCount = 50,
            GeneratedAt = DateTime.UtcNow,
        };

        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(ER(flux: 0.12, beatsLoud: 0.15, onset: 5.5, dance: 1.8, aloud: 0.92));

        var withDefaults = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var withProfile = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance, profile);

        var defaultResult = await withDefaults.AnalyzeAsync(FakeFile);
        var profileResult = await withProfile.AnalyzeAsync(FakeFile);

        defaultResult.Energy.Should().NotBeNull();
        profileResult.Energy.Should().NotBeNull();
        // Wider profile → lower bucket for the same input.
        profileResult.Energy!.Value.Should().BeLessThan(defaultResult.Energy!.Value);
    }

    [Fact]
    public void Calibration_profile_with_zero_width_range_does_not_crash()
    {
        // Degenerate profile (Floor == Ceiling) shouldn't divide by zero. The defensive midpoint
        // mapping in FeatureRange.Normalize keeps the analyzer producing *some* output.
        var profile = new EnergyCalibrationProfile
        {
            SpectralFlux    = new FeatureAnchor { Floor = 0.1, Ceiling = 0.1 },
            BeatsLoudness   = new FeatureAnchor { Floor = 0.1, Ceiling = 0.1 },
            OnsetRate       = new FeatureAnchor { Floor = 5.0, Ceiling = 5.0 },
            Danceability    = new FeatureAnchor { Floor = 1.5, Ceiling = 1.5 },
            AverageLoudness = new FeatureAnchor { Floor = 0.8, Ceiling = 0.8 },
        };

        var ranges = EssentiaEnergyAnalyzer.CompositeRanges.From(profile);
        var (bucket, _) = EssentiaEnergyAnalyzer.Compose(
            ER(flux: 0.12, beatsLoud: 0.15, onset: 5.5, dance: 1.8, aloud: 0.92),
            ranges);

        bucket.Should().NotBeNull();
        // All features normalise to 0.5 (midpoint), composite → 0.5, bucket = 1 + 9·0.5 = 5.5 → 6.
        bucket!.Value.Should().Be(6);
    }
}
