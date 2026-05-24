using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Validation;

namespace RayTagger.Core.Tests.Validation;

public class BacktestReportAggregatorTests
{
    [Fact]
    public void Aggregates_per_dimension_outcomes()
    {
        var truth = new BacktestTruth("/a.mp3", "House", null, 120.0, "8A", 6, false);

        var files = new List<BacktestFileResult>
        {
            Result(truth, genre: BacktestOutcome.Match, bpm: BacktestOutcome.Match),
            Result(truth, genre: BacktestOutcome.Mismatch, bpm: BacktestOutcome.ToleranceMatch),
            Result(truth, genre: BacktestOutcome.NoPrediction, bpm: BacktestOutcome.NoTruth),
        };

        var report = BacktestReportAggregator.Build("/ref", files, DateTime.UtcNow);

        report.GenreMetrics.Total.Should().Be(3);
        report.GenreMetrics.Matches.Should().Be(1);
        report.GenreMetrics.Mismatches.Should().Be(1);
        report.GenreMetrics.NoPrediction.Should().Be(1);
        report.GenreMetrics.Evaluable.Should().Be(3);
        report.GenreMetrics.ExactMatchRate.Should().BeApproximately(1.0 / 3, 0.001);

        report.BpmMetrics.NoTruth.Should().Be(1);
        report.BpmMetrics.Evaluable.Should().Be(2);
        report.BpmMetrics.ToleranceMatchRate.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void Counts_sources_per_dimension()
    {
        var truth = new BacktestTruth("/a.mp3", "House", null, 120.0, "8A", 6, false);

        var files = new List<BacktestFileResult>
        {
            Result(truth, genre: BacktestOutcome.Match, genreSource: TagFieldSource.Lookup),
            Result(truth, genre: BacktestOutcome.Match, genreSource: TagFieldSource.Lookup),
            Result(truth, genre: BacktestOutcome.Mismatch, genreSource: TagFieldSource.Existing),
            Result(truth, genre: BacktestOutcome.NoPrediction, genreSource: null),
        };

        var report = BacktestReportAggregator.Build("/ref", files, DateTime.UtcNow);

        report.GenreMetrics.SourceCounts[TagFieldSource.Lookup].Should().Be(2);
        report.GenreMetrics.SourceCounts[TagFieldSource.Existing].Should().Be(1);
        report.GenreMetrics.SourceCounts.Should().NotContainKey(TagFieldSource.Analysis);
        report.GenreMetrics.SourceCounts.Should().NotContainKey(TagFieldSource.Rules);
    }

    [Fact]
    public void Builds_genre_confusion_per_truth_genre()
    {
        var housetruth = new BacktestTruth("/a.mp3", "House", null, 120.0, "8A", 6, false);
        var technotruth = new BacktestTruth("/b.mp3", "Techno", null, 130.0, "8A", 6, false);

        var files = new List<BacktestFileResult>
        {
            Result(housetruth, genre: BacktestOutcome.Match, predicted: "House"),
            Result(housetruth, genre: BacktestOutcome.Mismatch, predicted: "Tech House"),
            Result(housetruth, genre: BacktestOutcome.Mismatch, predicted: "Deep House"),
            Result(technotruth, genre: BacktestOutcome.Match, predicted: "Techno"),
        };

        var report = BacktestReportAggregator.Build("/ref", files, DateTime.UtcNow);

        report.GenreConfusion.Should().ContainKey("House");
        report.GenreConfusion["House"].Total.Should().Be(3);
        report.GenreConfusion["House"].Matches.Should().Be(1);
        report.GenreConfusion["House"].Mismatches.Should().Be(2);
        report.GenreConfusion["House"].Predicted.Should().ContainKey("Tech House");
        report.GenreConfusion["House"].Predicted["Tech House"].Should().Be(1);

        report.GenreConfusion.Should().ContainKey("Techno");
        report.GenreConfusion["Techno"].Total.Should().Be(1);
    }

    [Fact]
    public void Aggregates_source_wins_and_losses_from_WinningGenreSource()
    {
        var truth = new BacktestTruth("/a.mp3", "House", null, 120.0, "8A", 6, false);

        var files = new List<BacktestFileResult>
        {
            Result(truth, genre: BacktestOutcome.Match, winningGenreSource: "musicbrainz"),
            Result(truth, genre: BacktestOutcome.Match, winningGenreSource: "musicbrainz"),
            Result(truth, genre: BacktestOutcome.Mismatch, winningGenreSource: "lastfm"),
            Result(truth, genre: BacktestOutcome.Match, winningGenreSource: "classifier:heuristic"),
            // No winning source — passthrough, must not skew the stats.
            Result(truth, genre: BacktestOutcome.NoPrediction, winningGenreSource: null),
        };

        var report = BacktestReportAggregator.Build("/ref", files, DateTime.UtcNow);

        report.Trace.BySource.Should().ContainKey("musicbrainz");
        report.Trace.BySource["musicbrainz"].Wins.Should().Be(2);
        report.Trace.BySource["musicbrainz"].Losses.Should().Be(0);

        report.Trace.BySource.Should().ContainKey("lastfm");
        report.Trace.BySource["lastfm"].Wins.Should().Be(0);
        report.Trace.BySource["lastfm"].Losses.Should().Be(1);

        report.Trace.BySource.Should().ContainKey("classifier:heuristic");
        report.Trace.BySource["classifier:heuristic"].Wins.Should().Be(1);
    }

    [Fact]
    public void Trace_aggregate_counts_aggregated_fallback_suffix()
    {
        var truth = new BacktestTruth("/a.mp3", "House", null, 120.0, "8A", 6, false);

        var traceWithFallbackSuffix = new List<CandidateTraceEntry>
        {
            new("Electronic", "classifier:essentia-tf-discogs-effnet:aggregated-fallback",
                0.2, [], null, [], null),
        };

        var files = new List<BacktestFileResult>
        {
            Result(
                truth, genre: BacktestOutcome.Mismatch,
                winningGenreSource: "classifier:essentia-tf-discogs-effnet:aggregated-fallback",
                trace: traceWithFallbackSuffix),
        };

        var report = BacktestReportAggregator.Build("/ref", files, DateTime.UtcNow);

        report.Trace.ResolverAggregatedFallbackCount.Should().Be(1);
    }

    private static BacktestFileResult Result(
        BacktestTruth truth,
        BacktestOutcome genre = BacktestOutcome.NoTruth,
        BacktestOutcome bpm = BacktestOutcome.NoTruth,
        string? predicted = null,
        TagFieldSource? genreSource = null,
        string? winningGenreSource = null,
        IReadOnlyList<CandidateTraceEntry>? trace = null)
    {
        var genreCmp = new BacktestComparison(genre, truth.Genre, predicted ?? truth.Genre)
            with { Source = genreSource };
        return new BacktestFileResult(
            Truth: truth,
            Genre: genreCmp,
            SubGenre: BacktestComparison.NoTruth,
            Bpm: new BacktestComparison(bpm, truth.Bpm, null),
            Key: BacktestComparison.NoTruth,
            Energy: BacktestComparison.NoTruth,
            Error: null,
            GenreLookupTrace: trace,
            WinningGenreSource: winningGenreSource);
    }
}
