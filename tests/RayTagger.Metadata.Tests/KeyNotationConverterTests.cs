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

    // Real-world DJ libraries (Mixed In Key, Traktor, …) often write Camelot notation into TKEY /
    // INITIALKEY even though the spec says those frames carry standard notation. The reader passes
    // the TKEY value into the `standard` slot — we have to accept Camelot there too or every
    // MIK-tagged file looks unkeyed in the diff view. This is the regression for the UI bug.
    [Theory]
    [InlineData("8A",  "Am",  "8A")]
    [InlineData("3A",  "Bbm", "3A")]
    [InlineData("11A", "F#m", "11A")]
    [InlineData("8B",  "C",   "8B")]
    public void Standard_slot_accepts_Camelot_notation(string camelotInStandardSlot, string expectedStd, string expectedCam)
    {
        var result = KeyNotationConverter.FromEither(camelotInStandardSlot, null);

        result.Should().NotBeNull();
        result!.Standard.Should().Be(expectedStd);
        result.Camelot.Should().Be(expectedCam);
    }

    [Theory]
    [InlineData("Am",  "Am",  "8A")]
    [InlineData("Dm",  "Dm",  "7A")]
    public void Camelot_slot_accepts_standard_notation(string standardInCamelotSlot, string expectedStd, string expectedCam)
    {
        var result = KeyNotationConverter.FromEither(null, standardInCamelotSlot);

        result.Should().NotBeNull();
        result!.Standard.Should().Be(expectedStd);
        result.Camelot.Should().Be(expectedCam);
    }

    // ===== CamelotSortKey =========================================================================

    [Theory]
    [InlineData("1A", "01A")]
    [InlineData("9B", "09B")]
    [InlineData("12A", "12A")]
    [InlineData("12B", "12B")]
    [InlineData("10A", "10A")]
    [InlineData("5a", "05A")]   // lower-case letter normalised to upper
    [InlineData("11b", "11B")]
    public void CamelotSortKey_zero_pads_single_digit_numbers(string camelot, string expectedSortKey)
    {
        KeyNotationConverter.CamelotSortKey(camelot).Should().Be(expectedSortKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CamelotSortKey_null_or_whitespace_returns_null(string? value)
    {
        KeyNotationConverter.CamelotSortKey(value).Should().BeNull();
    }

    [Fact]
    public void CamelotSortKey_passes_unexpected_shapes_through_unchanged()
    {
        // "12C" isn't a real Camelot code — pass through rather than silently mis-sorting.
        KeyNotationConverter.CamelotSortKey("12C").Should().Be("12C");
        // Single char without A/B suffix — degenerate, pass through.
        KeyNotationConverter.CamelotSortKey("A").Should().Be("A");
    }
}
