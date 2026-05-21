using RayTagger.Ui.ViewModels;

namespace RayTagger.Ui.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="FilterPredicates.MatchesBpmFilter"/>. The full ScanViewModel pipeline
/// isn't exercised here (it has DI dependencies); we test the pure parsing helper directly.
/// </summary>
public class ScanViewModelFilterTests
{
    // ----- Empty / whitespace filters always match ------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_filter_matches_any_bpm(string? filter)
    {
        FilterPredicates.MatchesBpmFilter(124.5, filter, "124,5").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(null, filter, "").Should().BeTrue();
    }

    // ----- Range "min-max" -----------------------------------------------------------------------

    [Theory]
    [InlineData(100.0, "100-130", true)]
    [InlineData(130.0, "100-130", true)]
    [InlineData(115.0, "100-130", true)]
    [InlineData(99.9, "100-130", false)]
    [InlineData(130.1, "100-130", false)]
    public void Range_filter_includes_endpoints(double bpm, string filter, bool expected)
    {
        FilterPredicates.MatchesBpmFilter(bpm, filter, bpm.ToString()).Should().Be(expected);
    }

    [Fact]
    public void Range_endpoints_are_order_independent()
    {
        // "128-120" should behave like "120-128".
        FilterPredicates.MatchesBpmFilter(125, "128-120", "125").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(119, "128-120", "119").Should().BeFalse();
    }

    [Fact]
    public void Range_with_decimal_separators_DE_and_EN()
    {
        FilterPredicates.MatchesBpmFilter(120.5, "120,3-120,8", "120,5").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(120.5, "120.3-120.8", "120,5").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(120.2, "120,3-120,8", "120,2").Should().BeFalse();
    }

    [Fact]
    public void Range_with_null_bpm_does_not_match()
    {
        // Pre-scan rows (no proposed BPM yet) shouldn't sneak through a numeric range filter.
        FilterPredicates.MatchesBpmFilter(null, "100-130", "").Should().BeFalse();
    }

    // ----- Comparisons <, <=, >, >= -------------------------------------------------------------

    [Theory]
    [InlineData(99.0, "<100", true)]
    [InlineData(100.0, "<100", false)]
    [InlineData(100.0, "<=100", true)]
    [InlineData(100.1, "<=100", false)]
    [InlineData(101.0, ">100", true)]
    [InlineData(100.0, ">100", false)]
    [InlineData(100.0, ">=100", true)]
    [InlineData(99.9, ">=100", false)]
    public void Comparison_operators(double bpm, string filter, bool expected)
    {
        FilterPredicates.MatchesBpmFilter(bpm, filter, bpm.ToString()).Should().Be(expected);
    }

    [Fact]
    public void Comparison_allows_whitespace_around_operator()
    {
        FilterPredicates.MatchesBpmFilter(150, "  >  128 ", "150").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(120, "  >  128 ", "120").Should().BeFalse();
    }

    [Fact]
    public void Comparison_with_null_bpm_does_not_match()
    {
        FilterPredicates.MatchesBpmFilter(null, "<100", "").Should().BeFalse();
        FilterPredicates.MatchesBpmFilter(null, ">=128", "").Should().BeFalse();
    }

    // ----- Fallback to contains-match on display string -----------------------------------------

    [Fact]
    public void Non_numeric_input_falls_back_to_contains_on_display()
    {
        // Display "124 → 128" — plain "12" should still match like the other text filters.
        FilterPredicates.MatchesBpmFilter(128, "12", "124 → 128").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(128, "→", "124 → 128").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(128, "99", "124 → 128").Should().BeFalse();
    }

    [Fact]
    public void Plain_number_does_not_engage_range_or_comparison()
    {
        // "120" alone is treated as a substring filter — matches the display "120", not as
        // "BPM == 120" (no operator → contains semantics like all other columns).
        FilterPredicates.MatchesBpmFilter(120, "120", "120").Should().BeTrue();
        FilterPredicates.MatchesBpmFilter(118, "120", "118").Should().BeFalse();
        // Display "1200" would substring-match "120" — that's intentional contains behaviour.
        FilterPredicates.MatchesBpmFilter(1200, "120", "1200").Should().BeTrue();
    }
}
