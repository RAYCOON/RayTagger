using Microsoft.Extensions.Logging.Abstractions;
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
            .Returns(new EssentiaResult(null, null, essentiaKey, essentiaScale, KeyStrength: 0.7, null));

        var analyzer = new EssentiaKeyAnalyzer(service, NullLogger<EssentiaKeyAnalyzer>.Instance);
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
            .Returns(new EssentiaResult(null, null, null, "minor", 0.5, null));

        var analyzer = new EssentiaKeyAnalyzer(service, NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key.Should().BeNull();
    }

    [Fact]
    public async Task Returns_empty_when_essentia_emits_unrecognised_key_string()
    {
        // Defensive: a malformed key (e.g. "??") shouldn't pin a half-built MusicalKey downstream.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(null, null, "??", "minor", 0.5, null));

        var analyzer = new EssentiaKeyAnalyzer(service, NullLogger<EssentiaKeyAnalyzer>.Instance);
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

        var analyzer = new EssentiaKeyAnalyzer(service, NullLogger<EssentiaKeyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Key.Should().BeNull();
    }
}
