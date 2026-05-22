using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class EssentiaBpmAnalyzerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    /// <summary>
    /// Default snap config matches the production defaults in <see cref="BpmAnalyzerOptions"/>
    /// (0.12% tolerance, 0.5 step) — the analyzer uses these when constructed without options.
    /// </summary>
    private static BpmAnalyzerOptions DefaultSnapOpts() => new()
    {
        Provider = "essentia",
        SnapTolerancePercent = 0.12,
        SnapStep = 0.5,
    };

    private static IEssentiaAnalysisService ServiceReturning(double? bpm, double confidence = 0.7) =>
        Substitute.For<IEssentiaAnalysisService>().Configure(s =>
            s.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
                .Returns(bpm is null
                    ? null
                    : new EssentiaResult(Bpm: bpm, BpmConfidence: confidence,
                        null, null, null, null, null, null, null, null, null,
                        null, null, null, null, null)));

    [Fact]
    public async Task Returns_raw_when_no_range_configured()
    {
        // Without a genre hint the analyzer must not snap — that's the pipeline-level pass's job.
        // 122.07 falls through verbatim so the downstream snapper can still cleanup uniformly.
        var service = ServiceReturning(122.07);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile);

        result.Bpm.Should().BeApproximately(122.07, 1e-6);
        result.Confidence.Should().BeApproximately(0.7, 0.001);
        result.WasSnapped.Should().BeFalse();
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task Returns_empty_when_service_returns_null()
    {
        var service = ServiceReturning(null);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(110, 140));

        result.Bpm.Should().BeNull();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Clamps_confidence_above_one()
    {
        // Defensive — Essentia shouldn't produce > 1.0 but we don't propagate junk.
        var service = ServiceReturning(120, confidence: 1.5);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(110, 140));

        result.Confidence.Should().Be(1.0);
    }

    [Fact]
    public async Task In_range_value_snaps_and_does_not_mark_forced()
    {
        // 122.07 ∈ [110, 140] → snap to 122.0, no fold, no forced-fallback.
        var service = ServiceReturning(122.07);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(110, 140));

        result.Bpm.Should().BeApproximately(122.0, 1e-6);
        result.WasSnapped.Should().BeTrue();
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task In_range_value_already_on_grid_does_not_set_was_snapped()
    {
        // 125 ∈ [110, 140] and already exact → return it, but WasSnapped stays false.
        var service = ServiceReturning(125.0);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(110, 140));

        result.Bpm.Should().Be(125.0);
        result.WasSnapped.Should().BeFalse();
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task Below_min_doubles_then_snaps_when_resulting_value_is_in_range()
    {
        // Classic DnB intro: Essentia returns 85.9, range [130, 200] → double to 171.8 → snap to 171.5.
        // Wait: 171.8 is closer to 172 than 171.5 (step 0.5). Drift to 172: 0.2/172 = 0.116% — under
        // the 0.12% threshold. Snap fires → 172.0.
        var service = ServiceReturning(85.9);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(130, 200));

        result.Bpm.Should().BeApproximately(172.0, 1e-6);
        result.WasSnapped.Should().BeTrue();
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task Above_max_halves_then_snaps_when_resulting_value_is_in_range()
    {
        // Classic DubStep half-time convention: Essentia returns 154.02, range [50, 100] → halve
        // to 77.01 → snap to 77.0.
        var service = ServiceReturning(154.02);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(50, 100));

        result.Bpm.Should().BeApproximately(77.0, 1e-6);
        result.WasSnapped.Should().BeTrue();
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task Above_max_halves_cleanly_when_already_on_grid()
    {
        // raw=140 halved to 70, exact integer, snap is a no-op. Fold succeeds without snap firing.
        var service = ServiceReturning(140.0);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(50, 100));

        result.Bpm.Should().Be(70.0);
        result.WasSnapped.Should().BeFalse();
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_fold_marks_forced_fallback_with_snapped_raw()
    {
        // raw=105, range [130, 200]: 105 < 130 → fold ×2 → 210. 210 > 200 → still out of range.
        // Forced fallback: return snap(105) = 105 (already on grid), set IsForcedFallback.
        var service = ServiceReturning(105.0);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(130, 200));

        result.Bpm.Should().Be(105.0);
        result.IsForcedFallback.Should().BeTrue();
        result.WasSnapped.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_fold_also_snaps_raw_when_raw_has_drift()
    {
        // raw=105.07, range [130, 200]: fold ×2 → 210.14 → snap stays 210.14 (drift > tolerance)
        // → out of range → fallback to snap(105.07) = 105.0 (drift to 105 is 0.067%, snap fires).
        var service = ServiceReturning(105.07);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(130, 200));

        result.Bpm.Should().BeApproximately(105.0, 1e-6);
        result.IsForcedFallback.Should().BeTrue();
        result.WasSnapped.Should().BeTrue();
    }

    [Fact]
    public async Task Boundary_min_is_inclusive()
    {
        // raw exactly == Min → in range, no fold.
        var service = ServiceReturning(110.0);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(110, 140));

        result.Bpm.Should().Be(110.0);
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task Boundary_max_is_inclusive()
    {
        // raw exactly == Max → in range, no fold.
        var service = ServiceReturning(140.0);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange(110, 140));

        result.Bpm.Should().Be(140.0);
        result.IsForcedFallback.Should().BeFalse();
    }

    [Fact]
    public async Task Range_without_min_or_max_is_treated_as_no_range()
    {
        // A half-specified range carries no signal — the analyzer should behave as if no hint
        // was given (raw passes through unsnapped for the pipeline pass to handle).
        var service = ServiceReturning(122.07);
        var analyzer = new EssentiaBpmAnalyzer(service, NullLogger<EssentiaBpmAnalyzer>.Instance, DefaultSnapOpts());

        var result = await analyzer.AnalyzeAsync(FakeFile, new BpmTempoRange { Min = 110 });

        result.Bpm.Should().BeApproximately(122.07, 1e-6);
        result.WasSnapped.Should().BeFalse();
        result.IsForcedFallback.Should().BeFalse();
    }
}

/// <summary>NSubstitute fluent helper used only by this test file.</summary>
file static class SubstituteFluentExtensions
{
    public static T Configure<T>(this T substitute, Action<T> configure)
    {
        configure(substitute);
        return substitute;
    }
}
