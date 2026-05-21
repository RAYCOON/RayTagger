using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class AnalysisRunnerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    private static AnalysisRunner Build(
        IBpmAnalyzer? bpm = null,
        IKeyAnalyzer? key = null,
        IEnergyAnalyzer? energy = null,
        IFingerprintAnalyzer? fingerprint = null,
        ITempoRangeResolver? tempoRangeResolver = null) =>
        new(bpm, key, energy, fingerprint, tempoRangeResolver, NullLogger<AnalysisRunner>.Instance);

    [Fact]
    public async Task Returns_empty_result_when_all_analyzers_are_null()
    {
        var runner = Build();

        var result = await runner.RunAsync(FakeFile, TrackTags.Empty);

        result.Bpm.Bpm.Should().BeNull();
        result.Key.Key.Should().BeNull();
        result.Energy.Energy.Should().BeNull();
        result.Fingerprint.Chromaprint.Should().BeNull();
    }

    [Fact]
    public async Task Returns_partial_result_when_only_some_analyzers_present()
    {
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<BpmTempoRange?>(), Arg.Any<CancellationToken>())
            .Returns(new BpmResult(128, 0.9));

        var runner = Build(bpm: bpm);

        var result = await runner.RunAsync(FakeFile, TrackTags.Empty);

        result.Bpm.Bpm.Should().Be(128);
        result.Bpm.Confidence.Should().BeApproximately(0.9, 0.001);
        result.Key.Key.Should().BeNull();
    }

    [Fact]
    public async Task Per_analyzer_failure_is_isolated_and_does_not_throw()
    {
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<BpmTempoRange?>(), Arg.Any<CancellationToken>())
            .Returns<BpmResult>(_ => throw new AnalysisException("aubio not on PATH", analyzer: "aubio"));

        var key = Substitute.For<IKeyAnalyzer>();
        key.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new KeyResult(new MusicalKey("Am", "8A"), 0.85));

        var runner = Build(bpm: bpm, key: key);

        var result = await runner.RunAsync(FakeFile, TrackTags.Empty);

        result.Bpm.Bpm.Should().BeNull();
        result.Key.Key.Should().NotBeNull();
        result.Key.Key!.Camelot.Should().Be("8A");
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_runner()
    {
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<BpmTempoRange?>(), Arg.Any<CancellationToken>())
            .Returns<BpmResult>(_ => throw new OperationCanceledException());

        var runner = Build(bpm: bpm);

        Func<Task> act = () => runner.RunAsync(FakeFile, TrackTags.Empty);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Unexpected_exception_is_swallowed_and_yields_empty_result()
    {
        // Defensive: an analyzer impl that throws something other than AnalysisException must not
        // tank the whole pipeline.
        var bpm = Substitute.For<IBpmAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<BpmTempoRange?>(), Arg.Any<CancellationToken>())
            .Returns<BpmResult>(_ => throw new InvalidOperationException("bug"));

        var runner = Build(bpm: bpm);

        var result = await runner.RunAsync(FakeFile, TrackTags.Empty);

        result.Bpm.Bpm.Should().BeNull();
        result.Bpm.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task Resolved_tempo_range_is_forwarded_only_to_bpm_analyzer()
    {
        // The genre-resolved tempo hint is BPM-specific (the analyzer applies the fold algorithm
        // with it). Key/Energy don't need it — they read the same shared Essentia result keyed
        // by file + mtime.
        var resolver = Substitute.For<ITempoRangeResolver>();
        var range = new BpmTempoRange(110, 140);
        resolver.Resolve(Arg.Any<TrackTags>()).Returns(range);

        var bpm = Substitute.For<IBpmAnalyzer>();
        var key = Substitute.For<IKeyAnalyzer>();
        var energy = Substitute.For<IEnergyAnalyzer>();
        bpm.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<BpmTempoRange?>(), Arg.Any<CancellationToken>())
            .Returns(new BpmResult(125, 0.8));
        key.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new KeyResult(null, 0));
        energy.AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(new EnergyResult(null, 0));

        var runner = Build(bpm, key, energy, tempoRangeResolver: resolver);
        await runner.RunAsync(FakeFile, new TrackTags(Genre: "House"));

        await bpm.Received(1).AnalyzeAsync(Arg.Any<TrackFile>(), range, Arg.Any<CancellationToken>());
        await key.Received(1).AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>());
        await energy.Received(1).AnalyzeAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>());
    }
}
