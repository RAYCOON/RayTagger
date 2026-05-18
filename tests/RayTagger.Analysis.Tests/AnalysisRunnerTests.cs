using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class AnalysisRunnerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    [Fact]
    public async Task Returns_empty_result_when_all_analyzers_are_null()
    {
        var runner = new AnalysisRunner(null, null, null, null, NullLogger<AnalysisRunner>.Instance);

        var result = await runner.RunAsync(FakeFile);

        result.Bpm.Bpm.Should().BeNull();
        result.Key.Key.Should().BeNull();
        result.Energy.Energy.Should().BeNull();
        result.Fingerprint.Chromaprint.Should().BeNull();
    }

    [Fact]
    public async Task Returns_partial_result_when_only_some_analyzers_present()
    {
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new BpmResult(128, 0.9));

        var runner = new AnalysisRunner(bpm, null, null, null, NullLogger<AnalysisRunner>.Instance);

        var result = await runner.RunAsync(FakeFile);

        result.Bpm.Bpm.Should().Be(128);
        result.Bpm.Confidence.Should().BeApproximately(0.9, 0.001);
        result.Key.Key.Should().BeNull();
    }

    [Fact]
    public async Task Per_analyzer_failure_is_isolated_and_does_not_throw()
    {
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns<BpmResult>(_ => throw new AnalysisException("aubio not on PATH", analyzer: "aubio"));

        var key = Substitute.For<IKeyAnalyzer>();
        key.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new KeyResult(new MusicalKey("Am", "8A"), 0.85));

        var runner = new AnalysisRunner(bpm, key, null, null, NullLogger<AnalysisRunner>.Instance);

        var result = await runner.RunAsync(FakeFile);

        result.Bpm.Bpm.Should().BeNull();
        result.Key.Key.Should().NotBeNull();
        result.Key.Key!.Camelot.Should().Be("8A");
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_runner()
    {
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns<BpmResult>(_ => throw new OperationCanceledException());

        var runner = new AnalysisRunner(bpm, null, null, null, NullLogger<AnalysisRunner>.Instance);

        Func<Task> act = () => runner.RunAsync(FakeFile);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Unexpected_exception_is_swallowed_and_yields_empty_result()
    {
        // Defensive: an analyzer impl that throws something other than AnalysisException must not
        // tank the whole pipeline.
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns<BpmResult>(_ => throw new InvalidOperationException("bug"));

        var runner = new AnalysisRunner(bpm, null, null, null, NullLogger<AnalysisRunner>.Instance);

        var result = await runner.RunAsync(FakeFile);

        result.Bpm.Bpm.Should().BeNull();
        result.Bpm.Confidence.Should().Be(0);
    }
}
