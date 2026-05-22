using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Analysis.Genre;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Analysis.Tests.Genre;

public class GenreClassifierRunnerTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    private static AnalysisResult EmptyAnalysis() => AnalysisResult.Empty;

    private sealed class StubClassifier : IGenreClassifier
    {
        private readonly Func<CancellationToken, Task<GenreClassificationResult>> _factory;
        public StubClassifier(string name, Func<Task<GenreClassificationResult>> factory)
        {
            Name = name;
            _factory = _ => factory();
        }
        public StubClassifier(string name, Func<CancellationToken, Task<GenreClassificationResult>> factory)
        {
            Name = name;
            _factory = factory;
        }
        public string Name { get; }
        public Task<GenreClassificationResult> ClassifyAsync(TrackFile file, CancellationToken cancellationToken)
            => _factory(cancellationToken);
    }

    [Fact]
    public async Task Empty_classifier_list_returns_empty()
    {
        var runner = new GenreClassifierRunner([], NullLogger<GenreClassifierRunner>.Instance);

        var result = await runner.RunAsync(FakeFile, EmptyAnalysis(), CancellationToken.None);

        result.Candidates.Should().BeEmpty();
        result.Trace.Should().BeEmpty();
    }

    [Fact]
    public async Task Aggregates_candidates_from_multiple_classifiers_sorted_by_confidence()
    {
        var a = new StubClassifier("a", () => Task.FromResult(new GenreClassificationResult([
            new GenreCandidate("house", 0.7, "classifier:a"),
        ])));
        var b = new StubClassifier("b", () => Task.FromResult(new GenreClassificationResult([
            new GenreCandidate("trance", 0.9, "classifier:b"),
            new GenreCandidate("techno", 0.5, "classifier:b"),
        ])));

        var runner = new GenreClassifierRunner([a, b], NullLogger<GenreClassifierRunner>.Instance);
        var result = await runner.RunAsync(FakeFile, EmptyAnalysis(), CancellationToken.None);

        result.Candidates.Should().HaveCount(3);
        result.Candidates[0].Confidence.Should().Be(0.9);
        result.Candidates[1].Confidence.Should().Be(0.7);
        result.Candidates[2].Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task One_failing_classifier_does_not_kill_the_others()
    {
        var ok = new StubClassifier("ok", () => Task.FromResult(new GenreClassificationResult([
            new GenreCandidate("house", 0.8, "classifier:ok"),
        ])));
        var fail = new StubClassifier("fail", () =>
            throw new InvalidOperationException("boom"));

        var runner = new GenreClassifierRunner([ok, fail], NullLogger<GenreClassifierRunner>.Instance);
        var result = await runner.RunAsync(FakeFile, EmptyAnalysis(), CancellationToken.None);

        result.Candidates.Should().ContainSingle()
            .Which.Value.Should().Be("house");

        result.Trace.Should().HaveCount(2);
        result.Trace.Should().Contain(e => e.ClassifierName == "ok" && e.Status == ClassifierTraceStatus.Ok);
        var failEntry = result.Trace.Single(e => e.ClassifierName == "fail");
        failEntry.Status.Should().Be(ClassifierTraceStatus.Failed);
        failEntry.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task Trace_status_no_hit_when_classifier_returns_empty()
    {
        var empty = new StubClassifier("empty", () => Task.FromResult(GenreClassificationResult.Empty));

        var runner = new GenreClassifierRunner([empty], NullLogger<GenreClassifierRunner>.Instance);
        var result = await runner.RunAsync(FakeFile, EmptyAnalysis(), CancellationToken.None);

        result.Candidates.Should().BeEmpty();
        result.Trace.Should().ContainSingle()
            .Which.Status.Should().Be(ClassifierTraceStatus.NoHit);
    }

    [Fact]
    public async Task Cancellation_bubbles_up()
    {
        var stuck = new StubClassifier("stuck", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return GenreClassificationResult.Empty;
        });

        using var cts = new CancellationTokenSource();
        var runner = new GenreClassifierRunner([stuck], NullLogger<GenreClassifierRunner>.Instance);
        cts.Cancel();

        var act = () => runner.RunAsync(FakeFile, EmptyAnalysis(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Noop_runner_returns_empty()
    {
        var result = await NoopGenreClassifierRunner.Instance.RunAsync(
            FakeFile, EmptyAnalysis(), CancellationToken.None);

        result.Should().BeSameAs(GenreClassifierRunResult.Empty);
        result.Candidates.Should().BeEmpty();
        result.Trace.Should().BeEmpty();
    }
}
