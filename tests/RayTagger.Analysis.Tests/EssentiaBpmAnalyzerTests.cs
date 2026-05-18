using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class EssentiaBpmAnalyzerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    [Fact]
    public async Task Returns_bpm_with_first_peak_weight_as_confidence()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(Bpm: 128.0, BpmConfidence: 0.85, null, null, null, null));

        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Bpm.Should().Be(128.0);
        result.Confidence.Should().BeApproximately(0.85, 0.001);
    }

    [Fact]
    public async Task Returns_empty_when_service_returns_null()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns((EssentiaResult?)null);

        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Bpm.Should().BeNull();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Returns_empty_when_bpm_field_missing()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(Bpm: null, BpmConfidence: 0.9, null, null, null, null));

        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Bpm.Should().BeNull();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Clamps_confidence_above_one()
    {
        // Defensive — Essentia shouldn't produce > 1.0 but we don't want to propagate junk.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(Bpm: 120, BpmConfidence: 1.5, null, null, null, null));

        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Confidence.Should().Be(1.0);
    }
}
