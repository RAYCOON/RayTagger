using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class EssentiaKeyAnalyzerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    [Theory]
    [InlineData("A",  "minor", "Am",  "8A")]
    [InlineData("A",  "major", "A",   "11B")]
    [InlineData("F#", "minor", "F#m", "11A")]
    [InlineData("C",  "major", "C",   "8B")]
    public async Task Maps_essentia_key_and_scale_to_full_musical_key(
        string essentiaKey, string essentiaScale, string expectedStandard, string expectedCamelot)
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(null, null, essentiaKey, essentiaScale, KeyStrength: 0.7, null, null, null, null, null, null, null, null, null, null, null));

        var analyzer = new EssentiaKeyAnalyzer(service, new KeyAnalyzerOptions(), NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key.Should().NotBeNull();
        result.Key!.Standard.Should().Be(expectedStandard);
        result.Key.Camelot.Should().Be(expectedCamelot);
        result.Confidence.Should().BeApproximately(0.7, 0.001);
    }

    [Fact]
    public async Task Returns_empty_when_key_field_missing()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(null, null, null, "minor", 0.5, null, null, null, null, null, null, null, null, null, null, null));

        var analyzer = new EssentiaKeyAnalyzer(service, new KeyAnalyzerOptions(), NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key.Should().BeNull();
    }

    [Fact]
    public async Task Returns_empty_when_essentia_emits_unrecognised_key_string()
    {
        // Defensive: a malformed key (e.g. "??") shouldn't pin a half-built MusicalKey downstream.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(null, null, "??", "minor", 0.5, null, null, null, null, null, null, null, null, null, null, null));

        var analyzer = new EssentiaKeyAnalyzer(service, new KeyAnalyzerOptions(), NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key.Should().BeNull();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Returns_empty_when_service_returns_null()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns((EssentiaResult?)null);

        var analyzer = new EssentiaKeyAnalyzer(service, new KeyAnalyzerOptions(), NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key.Should().BeNull();
    }

    // -----------------------------------------------------------------------------------
    // KeyProfileSelection — multi-profile reads + voting / fallback strategies
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task EnsembleVoting_picks_key_agreed_by_majority_when_edma_disagrees()
    {
        // EDMA says A-minor; Temperley and Krumhansl both say D-minor. Two of three vote D-minor
        // → ensemble picks D-minor. Confidence is the average of the two agreeing strengths.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult(
                edma: ("A", "minor", 0.3),
                temperley: ("D", "minor", 0.7),
                krumhansl: ("D", "minor", 0.8)));

        var analyzer = new EssentiaKeyAnalyzer(
            service,
            new KeyAnalyzerOptions { Profile = KeyProfileSelection.EnsembleVoting },
            NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key!.Standard.Should().Be("Dm");
        result.Confidence.Should().BeApproximately(0.75, 0.01,
            because: "average of Temperley 0.7 and Krumhansl 0.8");
    }

    [Fact]
    public async Task EnsembleVoting_falls_back_to_highest_strength_when_no_agreement()
    {
        // Three different keys → no majority. Pick the strongest single profile.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult(
                edma: ("A", "minor", 0.4),
                temperley: ("D", "minor", 0.9),       // highest strength
                krumhansl: ("F", "major", 0.6)));

        var analyzer = new EssentiaKeyAnalyzer(
            service,
            new KeyAnalyzerOptions { Profile = KeyProfileSelection.EnsembleVoting },
            NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key!.Standard.Should().Be("Dm");
        result.Confidence.Should().BeApproximately(0.9, 0.01);
    }

    [Fact]
    public async Task EdmaWithFallback_keeps_edma_above_threshold()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult(
                edma: ("A", "minor", 0.6),            // above default 0.5 threshold
                temperley: ("D", "minor", 0.9),
                krumhansl: ("F", "major", 0.6)));

        var analyzer = new EssentiaKeyAnalyzer(
            service,
            new KeyAnalyzerOptions { Profile = KeyProfileSelection.EdmaWithFallback },
            NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key!.Standard.Should().Be("Am",
            because: "EDMA strength 0.6 cleared the 0.5 threshold");
    }

    [Fact]
    public async Task EdmaWithFallback_switches_to_strongest_alt_below_threshold()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult(
                edma: ("A", "minor", 0.3),            // below default 0.5
                temperley: ("D", "minor", 0.85),
                krumhansl: ("F", "major", 0.6)));

        var analyzer = new EssentiaKeyAnalyzer(
            service,
            new KeyAnalyzerOptions { Profile = KeyProfileSelection.EdmaWithFallback },
            NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key!.Standard.Should().Be("Dm",
            because: "EDMA below threshold → Temperley wins on strength (0.85 vs 0.6)");
    }

    [Fact]
    public async Task EnsembleVoting_works_when_only_one_profile_present()
    {
        // Defensive: if Essentia somehow produced only EDMA (e.g. older binary), the analyzer
        // should still emit that single value rather than refusing.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult(edma: ("A", "minor", 0.7), temperley: null, krumhansl: null));

        var analyzer = new EssentiaKeyAnalyzer(
            service,
            new KeyAnalyzerOptions { Profile = KeyProfileSelection.EnsembleVoting },
            NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key!.Standard.Should().Be("Am");
    }

    private static EssentiaResult MakeResult(
        (string Key, string Scale, double Strength)? edma = null,
        (string Key, string Scale, double Strength)? temperley = null,
        (string Key, string Scale, double Strength)? krumhansl = null) =>
        new(
            Bpm: null, BpmConfidence: null,
            KeyKey: edma?.Key, KeyScale: edma?.Scale, KeyStrength: edma?.Strength,
            SpectralEnergy: null,
            AverageLoudness: null, SpectralFlux: null, OnsetRate: null, Danceability: null, BeatsLoudness: null,
            SpectralCentroidMean: null, SpectralComplexityMean: null, DynamicComplexity: null,
            ChordsChangesRate: null, ChordsStrengthMean: null,
            KeyTemperleyKey: temperley?.Key, KeyTemperleyScale: temperley?.Scale, KeyTemperleyStrength: temperley?.Strength,
            KeyKrumhanslKey: krumhansl?.Key, KeyKrumhanslScale: krumhansl?.Scale, KeyKrumhanslStrength: krumhansl?.Strength);
}
