using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Core.Tests.Pipeline;

public class TagPipelineTests
{
    [Fact]
    public async Task Yields_unchanged_outcome_when_file_has_only_existing_tags_and_dry_run_is_set()
    {
        var file = MakeTrackFile("song.mp3");
        var existing = new TrackTags(Title: "Song", Artist: "X", Genre: "House", Bpm: 124);

        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(file));
        var reader = Substitute.For<ITagReaderAdapter>();
        reader.Read(file.Path).Returns(existing);
        var writer = Substitute.For<ITagWriterAdapter>();

        var pipeline = new TagPipeline(discovery, reader, writer, NoopAnalysisRunner.Instance, NoopLookupRunner.Instance, new MappingRuleEngine(), NoopSortService.Instance, NullLogger<TagPipeline>.Instance);
        var options = MakeOptions(dryRun: true);

        var outcomes = await CollectAsync(pipeline.RunAsync(options, new MappingRuleSet()));

        outcomes.Should().HaveCount(1);
        outcomes[0].Status.Should().Be(PipelineStatus.Unchanged);
        outcomes[0].Errors.Should().BeEmpty();
        writer.DidNotReceive().Write(Arg.Any<string>(), Arg.Any<ResolvedTrackTags>(), Arg.Any<TaggerOptions>());
    }

    [Fact]
    public async Task Per_file_read_failure_is_isolated_to_that_file()
    {
        var goodFile = MakeTrackFile("good.mp3");
        var badFile = MakeTrackFile("bad.mp3");

        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(goodFile, badFile));
        var reader = Substitute.For<ITagReaderAdapter>();
        reader.Read(goodFile.Path).Returns(TrackTags.Empty);
        reader.Read(badFile.Path).Returns<TrackTags>(_ => throw new InvalidDataException("corrupt header"));
        var writer = Substitute.For<ITagWriterAdapter>();

        var pipeline = new TagPipeline(discovery, reader, writer, NoopAnalysisRunner.Instance, NoopLookupRunner.Instance, new MappingRuleEngine(), NoopSortService.Instance, NullLogger<TagPipeline>.Instance);

        var outcomes = await CollectAsync(pipeline.RunAsync(MakeOptions(dryRun: true), new MappingRuleSet()));

        outcomes.Should().HaveCount(2);
        outcomes[0].Status.Should().NotBe(PipelineStatus.Failed);
        outcomes[1].Status.Should().Be(PipelineStatus.Failed);
        outcomes[1].Errors.Should().ContainSingle(e => e.Stage == "Read");
    }

    [Fact]
    public async Task Cancellation_propagates_from_discovery()
    {
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(MakeTrackFile("a.mp3"), MakeTrackFile("b.mp3"), MakeTrackFile("c.mp3")));
        var reader = Substitute.For<ITagReaderAdapter>();
        reader.Read(Arg.Any<string>()).Returns(TrackTags.Empty);
        var writer = Substitute.For<ITagWriterAdapter>();

        var pipeline = new TagPipeline(discovery, reader, writer, NoopAnalysisRunner.Instance, NoopLookupRunner.Instance, new MappingRuleEngine(), NoopSortService.Instance, NullLogger<TagPipeline>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () =>
        {
            await foreach (var _ in pipeline.RunAsync(MakeOptions(dryRun: true), new MappingRuleSet(), cancellationToken: cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async IAsyncEnumerable<TrackFile> AsAsync(params TrackFile[] files)
    {
        foreach (var f in files)
        {
            yield return f;
        }
        await Task.CompletedTask;
    }

    private static async Task<List<PipelineOutcome>> CollectAsync(IAsyncEnumerable<PipelineOutcome> source)
    {
        var list = new List<PipelineOutcome>();
        await foreach (var o in source)
        {
            list.Add(o);
        }
        return list;
    }

    private static TrackFile MakeTrackFile(string name) =>
        new($"/fake/{name}", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    private static TaggerOptions MakeOptions(bool dryRun, int parallelism = 1) => new()
    {
        Scan = new ScanOptions { Source = "/fake", Parallelism = parallelism },
        Write = new WriteOptions { DryRun = dryRun },
    };

    [Fact]
    public async Task OnFileStarted_fires_once_per_file_before_outcome_in_sequential_path()
    {
        var files = Enumerable.Range(0, 5).Select(i => MakeTrackFile($"t{i}.mp3")).ToArray();
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(files));
        var reader = Substitute.For<ITagReaderAdapter>();
        foreach (var f in files) reader.Read(f.Path).Returns(TrackTags.Empty);
        var writer = Substitute.For<ITagWriterAdapter>();

        var pipeline = new TagPipeline(discovery, reader, writer, NoopAnalysisRunner.Instance,
            NoopLookupRunner.Instance, new MappingRuleEngine(), NoopSortService.Instance,
            NullLogger<TagPipeline>.Instance);

        var startedPaths = new List<string>();
        ValueTask Track(TrackFile f) { startedPaths.Add(f.Path); return ValueTask.CompletedTask; }

        var outcomes = await CollectAsync(pipeline.RunAsync(MakeOptions(dryRun: true), new MappingRuleSet(), Track));

        outcomes.Should().HaveCount(5);
        startedPaths.Should().Equal(files.Select(f => f.Path),
            because: "sequential path preserves discovery order and fires the callback exactly once per file");
    }

    [Fact]
    public async Task OnFileStarted_fires_once_per_file_in_parallel_path()
    {
        var files = Enumerable.Range(0, 12).Select(i => MakeTrackFile($"p{i}.mp3")).ToArray();
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(files));
        var reader = Substitute.For<ITagReaderAdapter>();
        foreach (var f in files) reader.Read(f.Path).Returns(TrackTags.Empty);
        var writer = Substitute.For<ITagWriterAdapter>();

        var pipeline = new TagPipeline(discovery, reader, writer, NoopAnalysisRunner.Instance,
            NoopLookupRunner.Instance, new MappingRuleEngine(), NoopSortService.Instance,
            NullLogger<TagPipeline>.Instance);

        var startedPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        ValueTask Track(TrackFile f) { startedPaths.Add(f.Path); return ValueTask.CompletedTask; }

        var outcomes = await CollectAsync(pipeline.RunAsync(MakeOptions(dryRun: true, parallelism: 4),
            new MappingRuleSet(), Track));

        outcomes.Should().HaveCount(12);
        startedPaths.Should().BeEquivalentTo(files.Select(f => f.Path),
            because: "parallel workers race on completion but each file is dequeued exactly once");
    }

    [Fact]
    public async Task OnFileStarted_exception_is_isolated_and_does_not_kill_workers()
    {
        // Buggy UI callback throws for one of the files. The pipeline must keep processing the
        // rest (per-file isolation, same guarantee ProcessFileAsync has had since day one).
        var files = Enumerable.Range(0, 6).Select(i => MakeTrackFile($"x{i}.mp3")).ToArray();
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(files));
        var reader = Substitute.For<ITagReaderAdapter>();
        foreach (var f in files) reader.Read(f.Path).Returns(TrackTags.Empty);
        var writer = Substitute.For<ITagWriterAdapter>();

        var pipeline = new TagPipeline(discovery, reader, writer, NoopAnalysisRunner.Instance,
            NoopLookupRunner.Instance, new MappingRuleEngine(), NoopSortService.Instance,
            NullLogger<TagPipeline>.Instance);

        ValueTask Track(TrackFile f)
        {
            if (f.Path.EndsWith("x3.mp3", StringComparison.Ordinal))
                throw new InvalidOperationException("synthetic UI callback bug");
            return ValueTask.CompletedTask;
        }

        var outcomes = await CollectAsync(pipeline.RunAsync(MakeOptions(dryRun: true, parallelism: 4),
            new MappingRuleSet(), Track));

        outcomes.Should().HaveCount(6,
            because: "the buggy callback on x3 must not starve any worker — every file still produces an outcome");
    }

    [Fact]
    public async Task Parallel_pipeline_processes_all_files_and_isolates_per_file_failures()
    {
        // Reuse the per-file isolation guarantee but force the channel-based fan-out (parallelism > 1).
        // The outcome ordering isn't deterministic — sort before asserting.
        var files = Enumerable.Range(0, 12).Select(i => MakeTrackFile($"f{i}.mp3")).ToArray();
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(files));

        var reader = Substitute.For<ITagReaderAdapter>();
        foreach (var file in files)
        {
            if (file.Path.Contains("f5", StringComparison.Ordinal))
            {
                reader.Read(file.Path).Returns<TrackTags>(_ => throw new InvalidDataException("synthetic"));
            }
            else
            {
                reader.Read(file.Path).Returns(TrackTags.Empty);
            }
        }

        var writer = Substitute.For<ITagWriterAdapter>();
        var pipeline = new TagPipeline(discovery, reader, writer, NoopAnalysisRunner.Instance, NoopLookupRunner.Instance, new MappingRuleEngine(), NoopSortService.Instance, NullLogger<TagPipeline>.Instance);

        var outcomes = await CollectAsync(pipeline.RunAsync(MakeOptions(dryRun: true, parallelism: 4), new MappingRuleSet()));

        outcomes.Should().HaveCount(12);
        outcomes.Count(o => o.Status == PipelineStatus.Failed).Should().Be(1);
        outcomes.Where(o => o.Status == PipelineStatus.Failed)
            .Should().AllSatisfy(o => o.File.Path.Should().Contain("f5"));
    }

    [Fact]
    public async Task Analyzer_forced_fallback_flows_to_outcome_only_when_analysis_supplied_the_final_value()
    {
        // Analyzer reports IsForcedFallback=true with raw=105 (e.g. genre range [130,200],
        // fold ×2 → 210 → still OOR → fallback). With no existing BPM on the file, the
        // analyzer's value passes the merge → outcome carries BpmIsForcedFallback=true.
        var file = MakeTrackFile("forced.mp3");
        var existing = new TrackTags(Genre: "Drum and Bass");
        var analysisRunner = new StubAnalysisRunner(new BpmResult(105.0, 0.7, WasSnapped: false, IsForcedFallback: true));

        var outcome = await RunSingle(file, existing, analysisRunner);

        outcome.BpmIsForcedFallback.Should().BeTrue();
        outcome.BpmWasSnapped.Should().BeFalse();
        outcome.Resolved.Bpm.Value.Should().Be(105.0);
    }

    [Fact]
    public async Task Analyzer_forced_fallback_is_dropped_when_existing_tag_wins_under_skip_if_present()
    {
        // Existing BPM 124 + skip_if_present → analyzer value never reaches resolved.Bpm, so
        // dark-blue must NOT fire even though the analyzer technically detected a forced fallback.
        var file = MakeTrackFile("ignored.mp3");
        var existing = new TrackTags(Genre: "Drum and Bass", Bpm: 124);
        var analysisRunner = new StubAnalysisRunner(new BpmResult(105.0, 0.7, WasSnapped: true, IsForcedFallback: true));

        var outcome = await RunSingle(file, existing, analysisRunner);

        outcome.BpmIsForcedFallback.Should().BeFalse(
            because: "existing-tag policy preserved 124; the analyzer's forced-fallback diagnostic doesn't apply to the value actually written");
        outcome.Resolved.Bpm.Value.Should().Be(124);
    }

    [Fact]
    public async Task Analyzer_snap_flag_propagates_when_analysis_value_passes_through()
    {
        // Analyzer pre-snapped 122.07 → 122.0 (in-range path). The pipeline-level snap is a
        // no-op on the already-on-grid value, so it needs to OR-in analysis.Bpm.WasSnapped.
        var file = MakeTrackFile("snapped.mp3");
        var existing = new TrackTags(Genre: "House");
        var analysisRunner = new StubAnalysisRunner(new BpmResult(122.0, 0.7, WasSnapped: true));

        var outcome = await RunSingle(file, existing, analysisRunner);

        outcome.BpmWasSnapped.Should().BeTrue();
        outcome.BpmIsForcedFallback.Should().BeFalse();
        outcome.Resolved.Bpm.Value.Should().Be(122.0);
    }

    [Fact]
    public async Task Pipeline_snap_still_fires_for_existing_bpm_drift()
    {
        // Existing tag = 126.01 (drift), analyzer empty → pipeline-level snap rounds to 126 and
        // sets BpmWasSnapped. This is the original behaviour and must still work end-to-end.
        var file = MakeTrackFile("existing.mp3");
        var existing = new TrackTags(Bpm: 126.01);

        var outcome = await RunSingle(file, existing, NoopAnalysisRunner.Instance);

        outcome.BpmWasSnapped.Should().BeTrue();
        outcome.BpmIsForcedFallback.Should().BeFalse();
        outcome.Resolved.Bpm.Value.Should().Be(126.0);
    }

    private static async Task<PipelineOutcome> RunSingle(TrackFile file, TrackTags existing, IAnalysisRunner runner)
    {
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(file));
        var reader = Substitute.For<ITagReaderAdapter>();
        reader.Read(file.Path).Returns(existing);
        var writer = Substitute.For<ITagWriterAdapter>();

        var pipeline = new TagPipeline(
            discovery, reader, writer, runner,
            NoopLookupRunner.Instance, new MappingRuleEngine(),
            NoopSortService.Instance, NullLogger<TagPipeline>.Instance);

        var outcomes = await CollectAsync(pipeline.RunAsync(MakeOptions(dryRun: true), new MappingRuleSet()));
        outcomes.Should().HaveCount(1);
        return outcomes[0];
    }

    /// <summary>Yields a single canned <see cref="BpmResult"/> for every track.</summary>
    private sealed class StubAnalysisRunner : IAnalysisRunner
    {
        private readonly BpmResult _bpm;
        public StubAnalysisRunner(BpmResult bpm) => _bpm = bpm;

        public Task<AnalysisResult> RunAsync(TrackFile file, TrackTags existing, CancellationToken cancellationToken = default)
            => Task.FromResult(new AnalysisResult(
                _bpm,
                new KeyResult(null, 0),
                new EnergyResult(null, 0),
                new FingerprintResult(null, 0),
                AcoustIdMbid: null));
    }
}
