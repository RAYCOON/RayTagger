using RayTagger.Core.Validation;

namespace RayTagger.Core.Tests.Validation;

public class MixedInKeyCommentParserTests
{
    [Theory]
    [InlineData("120.00 - 9A - 6", 120.00, "9A", 6)]
    [InlineData("87.50 - 11A - 7", 87.50, "11A", 7)]
    [InlineData("147.89 - 8A - 4", 147.89, "8A", 4)]
    [InlineData("90.00 - 11B - 10", 90.00, "11B", 10)]
    public void Parses_canonical_format(string comment, double bpm, string key, int energy)
    {
        var result = MixedInKeyCommentParser.TryParseComment(comment);
        result.Should().NotBeNull();
        result!.Bpm.Should().BeApproximately(bpm, 0.001);
        result.CamelotKey.Should().Be(key);
        result.Energy.Should().Be(energy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("120 9A 6")]             // wrong separator
    [InlineData("120 - 9A")]              // missing energy
    [InlineData("120 - 9A - 6 - extra")] // too many parts
    [InlineData("abc - 9A - 6")]          // bad BPM
    [InlineData("120 - 13A - 6")]         // invalid Camelot
    [InlineData("120 - 9C - 6")]          // invalid Camelot letter
    [InlineData("120 - 9A - 11")]         // energy out of range
    [InlineData("120 - 9A - 0")]          // energy out of range
    [InlineData("120 - 9A - x")]          // bad energy
    public void Rejects_invalid_formats(string? comment)
    {
        var result = MixedInKeyCommentParser.TryParseComment(comment);
        result.Should().BeNull();
    }

    [Fact]
    public void Genre_correction_doubles_DnB_below_100()
    {
        var truth = new MixedInKeyTruth(86.0, "8A", 6);
        var corrected = MixedInKeyCommentParser.ApplyGenreCorrection(truth, "Drum and Bass");

        corrected.Bpm.Should().BeApproximately(172.0, 0.001);
        corrected.BpmWasCorrected.Should().BeTrue();
    }

    [Fact]
    public void Genre_correction_halves_DubStep_above_100()
    {
        var truth = new MixedInKeyTruth(140.0, "8A", 6);
        var corrected = MixedInKeyCommentParser.ApplyGenreCorrection(truth, "DubStep");

        corrected.Bpm.Should().BeApproximately(70.0, 0.001);
        corrected.BpmWasCorrected.Should().BeTrue();
    }

    [Fact]
    public void Genre_correction_skips_DnB_when_already_correct()
    {
        var truth = new MixedInKeyTruth(172.0, "8A", 6);
        var corrected = MixedInKeyCommentParser.ApplyGenreCorrection(truth, "Drum and Bass");

        corrected.Bpm.Should().BeApproximately(172.0, 0.001);
        corrected.BpmWasCorrected.Should().BeFalse();
    }

    [Fact]
    public void Genre_correction_skips_DubStep_when_already_correct()
    {
        var truth = new MixedInKeyTruth(75.0, "8A", 6);
        var corrected = MixedInKeyCommentParser.ApplyGenreCorrection(truth, "DubStep");

        corrected.Bpm.Should().BeApproximately(75.0, 0.001);
        corrected.BpmWasCorrected.Should().BeFalse();
    }

    [Theory]
    [InlineData("House")]
    [InlineData("Techno")]
    [InlineData("Trance")]
    [InlineData("TripHop")]
    [InlineData("HipHop")]
    [InlineData("")]
    [InlineData(null)]
    public void Genre_correction_passes_other_genres_through(string? genre)
    {
        var truth = new MixedInKeyTruth(125.0, "8A", 6);
        var corrected = MixedInKeyCommentParser.ApplyGenreCorrection(truth, genre);

        corrected.Bpm.Should().BeApproximately(125.0, 0.001);
        corrected.BpmWasCorrected.Should().BeFalse();
    }

    [Theory]
    [InlineData("1A", true)]
    [InlineData("12A", true)]
    [InlineData("1B", true)]
    [InlineData("12B", true)]
    [InlineData("9a", true)]    // lower-case accepted via ToUpperInvariant
    [InlineData("0A", false)]
    [InlineData("13A", false)]
    [InlineData("1C", false)]
    [InlineData("A", false)]
    [InlineData("12", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCamelotKey_validates_correctly(string? value, bool expected)
    {
        // ToUpper before to match the parser's internal behaviour.
        var normalized = value?.ToUpperInvariant();
        MixedInKeyCommentParser.IsCamelotKey(normalized).Should().Be(expected);
    }
}
