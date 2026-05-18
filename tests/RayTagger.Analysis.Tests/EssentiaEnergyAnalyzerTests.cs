using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class EssentiaEnergyAnalyzerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    [Theory]
    // Anchors of the log-scaled calibration range. Floor maps to 1, ceiling to 10.
    [InlineData(0.001,  1)]
    [InlineData(0.0001, 1)] // below floor → clamped
    [InlineData(0.2,    10)]
    [InlineData(1.0,    10)] // above ceiling → clamped
    // Intermediate buckets — these encode the log scaling, change them only intentionally.
    [InlineData(0.01,   5)]
    [InlineData(0.05,   8)]
    public void Maps_spectral_energy_to_one_to_ten_bucket(double spectralEnergy, int expectedBucket)
    {
        EssentiaEnergyAnalyzer.MapToBucket(spectralEnergy).Should().Be(expectedBucket);
    }

    [Fact]
    public async Task Returns_bucket_and_fixed_confidence_when_spectral_energy_present()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(null, null, null, null, null, SpectralEnergy: 0.05));

        var analyzer = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Energy.Should().Be(8);
        result.Confidence.Should().BeApproximately(0.8, 0.001);
    }

    [Fact]
    public async Task Returns_empty_when_spectral_energy_missing()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(null, null, null, null, null, null));

        var analyzer = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Energy.Should().BeNull();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Returns_empty_when_spectral_energy_is_zero_or_negative()
    {
        // Defensive — spectral energy can only be ≥ 0 in theory, but log(0) explodes so guard.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EssentiaResult(null, null, null, null, null, SpectralEnergy: 0));

        var analyzer = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Energy.Should().BeNull();
    }

    [Fact]
    public async Task Returns_empty_when_service_returns_null()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns((EssentiaResult?)null);

        var analyzer = new EssentiaEnergyAnalyzer(service, NullLogger<EssentiaEnergyAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Energy.Should().BeNull();
    }
}
