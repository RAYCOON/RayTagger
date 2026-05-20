using RayTagger.Metadata;

namespace RayTagger.Metadata.Tests;

/// <summary>
/// Unit tests for <see cref="TagLibTagReader"/>'s internal parsing helpers. Round-trip tests
/// (which actually open a TagLib# file) live in <see cref="TagLibTagWriterTests"/>; this file
/// pins regressions for the string parsers we own because TagLib# gets them wrong.
/// </summary>
public sealed class TagLibTagReaderTests
{
    /// <summary>
    /// Regression: TagLib#'s <c>Tag.BeatsPerMinute</c> getter mis-reads a TBPM frame of
    /// <c>"140.00"</c> as <c>14000</c> (it strips the decimal point and parses what's left as
    /// uint). Our replacement <see cref="TagLibTagReader.ParseBpm"/> must keep returning
    /// 140.0 — locks the workaround in across TagLib# upgrades.
    /// </summary>
    [Theory]
    [InlineData("140.00", 140.0)]
    [InlineData("94.92", 94.92)]
    [InlineData("173.4819641114", 173.4819641114)]
    [InlineData("125", 125.0)]
    [InlineData("125.5", 125.5)]
    [InlineData("  126.00  ", 126.0)]   // surrounding whitespace tolerated
    public void ParseBpm_handles_decimal_strings(string input, double expected)
    {
        TagLibTagReader.ParseBpm(input).Should().BeApproximately(expected, 0.0001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("0")]
    [InlineData("-5")]
    public void ParseBpm_returns_null_for_invalid_or_nonpositive(string? input)
    {
        TagLibTagReader.ParseBpm(input).Should().BeNull();
    }
}
