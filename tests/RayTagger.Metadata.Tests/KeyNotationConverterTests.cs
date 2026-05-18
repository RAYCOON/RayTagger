using RayTagger.Core.Models;

namespace RayTagger.Metadata.Tests;

/// <summary>
/// Tests for <see cref="KeyNotationConverter"/>. The converter moved from Metadata to Core in
/// Phase 2 (analyzers also need it), so it's a public type now — no reflection needed.
/// </summary>
public class KeyNotationConverterTests
{
    [Theory]
    [InlineData("Am",  "8A")]
    [InlineData("am",  "8A")]
    [InlineData(" Am ", "8A")]
    [InlineData("F#m", "11A")]
    [InlineData("Gbm", "11A")]
    [InlineData("C",   "8B")]
    [InlineData("F#",  "2B")]
    [InlineData("Gb",  "2B")]
    public void Derives_camelot_from_standard(string standardInput, string expectedCamelot)
    {
        var result = KeyNotationConverter.FromEither(standardInput, null);

        result.Should().NotBeNull();
        result!.Camelot.Should().Be(expectedCamelot);
    }

    [Theory]
    [InlineData("8A",  "Am")]
    [InlineData("8a",  "Am")]
    [InlineData(" 11B ", "A")]
    [InlineData("11A", "F#m")]
    public void Derives_standard_from_camelot(string camelotInput, string expectedStandard)
    {
        var result = KeyNotationConverter.FromEither(null, camelotInput);

        result.Should().NotBeNull();
        result!.Standard.Should().Be(expectedStandard);
    }

    [Fact]
    public void Returns_null_for_unrecognised_input()
    {
        KeyNotationConverter.FromEither("Hbm", null).Should().BeNull();
        KeyNotationConverter.FromEither(null, "99Z").Should().BeNull();
        KeyNotationConverter.FromEither(null, null).Should().BeNull();
        KeyNotationConverter.FromEither("", "").Should().BeNull();
    }

    [Fact]
    public void Prefers_explicit_standard_over_derived_camelot_when_both_supplied()
    {
        var result = KeyNotationConverter.FromEither("Am", "8A");

        result.Should().NotBeNull();
        result!.Standard.Should().Be("Am");
        result.Camelot.Should().Be("8A");
    }

    [Fact]
    public void Invalid_camelot_with_valid_standard_falls_back_to_derived_camelot()
    {
        var result = KeyNotationConverter.FromEither("Am", "99X");

        result.Should().NotBeNull();
        result!.Standard.Should().Be("Am");
        result.Camelot.Should().Be("8A");
    }

    [Fact]
    public void Invalid_standard_with_valid_camelot_falls_back_to_derived_standard()
    {
        var result = KeyNotationConverter.FromEither("not-a-key", "8A");

        result.Should().NotBeNull();
        result!.Standard.Should().Be("Am");
        result.Camelot.Should().Be("8A");
    }
}
