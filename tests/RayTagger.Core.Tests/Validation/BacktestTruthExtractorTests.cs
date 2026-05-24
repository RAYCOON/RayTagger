using RayTagger.Core.Validation;

namespace RayTagger.Core.Tests.Validation;

public class BacktestTruthExtractorTests
{
    private static readonly string TaggedRoot = Path.Combine("/", "music", "Tagged");

    [Fact]
    public void Extracts_genre_from_first_subfolder()
    {
        var path = Path.Combine(TaggedRoot, "House", "Artist - Track.mp3");
        var truth = BacktestTruthExtractor.Extract(TaggedRoot, path, "120.00 - 9A - 6");

        truth.Genre.Should().Be("House");
        truth.SubGenre.Should().BeNull();
        truth.Bpm.Should().Be(120.0);
        truth.CamelotKey.Should().Be("9A");
        truth.Energy.Should().Be(6);
    }

    [Fact]
    public void Extracts_subgenre_from_second_subfolder()
    {
        var path = Path.Combine(TaggedRoot, "House", "Deep", "Artist - Track.mp3");
        var truth = BacktestTruthExtractor.Extract(TaggedRoot, path, "120.00 - 9A - 6");

        truth.Genre.Should().Be("House");
        truth.SubGenre.Should().Be("Deep");
    }

    [Fact]
    public void Applies_DnB_BPM_correction_when_below_100()
    {
        var path = Path.Combine(TaggedRoot, "Drum and Bass", "Artist - Track.mp3");
        var truth = BacktestTruthExtractor.Extract(TaggedRoot, path, "86.00 - 8A - 6");

        truth.Bpm.Should().BeApproximately(172.0, 0.001);
        truth.BpmWasCorrected.Should().BeTrue();
    }

    [Fact]
    public void Applies_DubStep_BPM_correction_when_above_100()
    {
        var path = Path.Combine(TaggedRoot, "DubStep", "Artist - Track.mp3");
        var truth = BacktestTruthExtractor.Extract(TaggedRoot, path, "140.00 - 8A - 6");

        truth.Bpm.Should().BeApproximately(70.0, 0.001);
        truth.BpmWasCorrected.Should().BeTrue();
    }

    [Fact]
    public void Returns_partial_truth_when_comment_missing()
    {
        var path = Path.Combine(TaggedRoot, "House", "Artist - Track.mp3");
        var truth = BacktestTruthExtractor.Extract(TaggedRoot, path, comment: null);

        truth.Genre.Should().Be("House");
        truth.Bpm.Should().BeNull();
        truth.CamelotKey.Should().BeNull();
        truth.Energy.Should().BeNull();
    }

    [Fact]
    public void Returns_empty_genre_when_file_sits_directly_under_root()
    {
        var path = Path.Combine(TaggedRoot, "stray.mp3");
        var truth = BacktestTruthExtractor.Extract(TaggedRoot, path, "120 - 9A - 6");

        truth.Genre.Should().BeEmpty();
        truth.SubGenre.Should().BeNull();
    }
}
