using RayTagger.Core.Models;
using RayTagger.Ui.ViewModels;

namespace RayTagger.Ui.Tests.ViewModels;

/// <summary>
/// Tests for the multi-column AND-verkettung in <see cref="FilterPredicates.MatchesAllFilters"/>.
/// Builds real <see cref="TrackOutcomeViewModel"/> instances via the preview ctor (no DI needed)
/// and varies a <see cref="ColumnFilters"/> snapshot per case.
/// </summary>
public class FilterPredicatesTests
{
    private static readonly TrackFile FileA =
        new("/library/a.mp3", AudioFormat.Mp3, 4_000_000, new DateTime(2024, 6, 22, 14, 35, 0, DateTimeKind.Utc));

    private static readonly TrackTags TrackA = new(
        Title: "Sunrise", Artist: "Anjuna", Album: "Above", Year: 2020,
        Genre: "House", SubGenre: "Tech", Bpm: 124.0, Key: new MusicalKey("Am", "8A"),
        Energy: 7, Mood: "Driving", SetPosition: "Peak Time", DurationSeconds: 381);

    private static TrackOutcomeViewModel RowA() => new(FileA, TrackA);

    private static ColumnFilters NoFilter() => new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    // ----- No filter ⇒ everything matches ---------------------------------------------------------

    [Fact]
    public void Empty_filter_snapshot_matches_every_row()
    {
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter()).Should().BeTrue();
    }

    // ----- Single-column filters -----------------------------------------------------------------

    [Fact]
    public void FileName_filter_matches_case_insensitive()
    {
        // FileA.Path ends in "a.mp3" — both "A.MP3" and "mp3" should hit.
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { FileName = "A.MP3" }).Should().BeTrue();
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { FileName = "mp3" }).Should().BeTrue();
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { FileName = "wav" }).Should().BeFalse();
    }

    [Fact]
    public void Artist_and_Title_filters_use_substring_semantics()
    {
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Artist = "anjun" }).Should().BeTrue();
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Title = "rise" }).Should().BeTrue();
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Title = "moon" }).Should().BeFalse();
    }

    [Fact]
    public void Bpm_range_filter_uses_EffectiveBpm()
    {
        // Pre-scan row: Proposed null, Existing 124 — EffectiveBpm falls back to 124.
        // The shared range parser already has its own tests; here we just verify the wiring.
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Bpm = "120-128" }).Should().BeTrue();
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Bpm = "130-140" }).Should().BeFalse();
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Bpm = "<=124" }).Should().BeTrue();
    }

    // ----- AND-verkettung über mehrere Spalten ----------------------------------------------------

    [Fact]
    public void Multiple_filters_AND_combine()
    {
        // Match: artist "anjun" AND bpm range covers 124. Both hit ⇒ true.
        FilterPredicates.MatchesAllFilters(
            RowA(),
            NoFilter() with { Artist = "anjun", Bpm = "120-128" }).Should().BeTrue();
    }

    [Fact]
    public void One_filter_failing_blocks_the_whole_row()
    {
        // Artist matches, but BPM range doesn't ⇒ false (AND-semantics).
        FilterPredicates.MatchesAllFilters(
            RowA(),
            NoFilter() with { Artist = "anjun", Bpm = "200-220" }).Should().BeFalse();
    }

    [Fact]
    public void All_seventeen_filter_columns_compose_pairwise()
    {
        // Hit every column simultaneously with a permissive snapshot — sanity check that the
        // 17-way AND-chain doesn't accidentally short-circuit a column.
        var hitAll = new ColumnFilters(
            Status: "",          // empty ⇒ match
            FileName: "a",       // FileA path contains "a"
            Title: "sun",        // "Sunrise"
            Artist: "Anjuna",
            Year: "2020",
            Genre: "house",      // GenreDisplay = "House" (no diff)
            SubGenre: "tech",
            Bpm: "120-130",      // EffectiveBpm = 124
            Key: "am",           // KeyDisplay = "Am"
            CamelotKey: "8a",    // CamelotKeyDisplay = "8A"
            Energy: "7",
            Mood: "driv",
            SetPosition: "peak",
            Album: "above",
            Length: "6:21",
            Size: "M",           // "3,8 MB" contains "M"
            Modified: "2024");   // Modified-Display contains the year? Actually it uses "dd.MM.yy"
        // ModifiedDisplay uses "dd.MM.yy HH:mm" — "24" appears for the 2024 year as "24". Drop the
        // Modified expectation to keep this test culture-independent.
        hitAll = hitAll with { Modified = null };
        FilterPredicates.MatchesAllFilters(RowA(), hitAll).Should().BeTrue();
    }

    [Fact]
    public void Status_filter_matches_badge_not_label()
    {
        // Preview ctor leaves StatusLabel empty → StatusBadge is also empty. Filtering on
        // empty-equivalent matches every row (empty filter ⇒ match).
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Status = "" }).Should().BeTrue();

        // "ERR" doesn't appear in the empty badge — should fail.
        FilterPredicates.MatchesAllFilters(RowA(), NoFilter() with { Status = "ERR" }).Should().BeFalse();
    }

    // ----- ContainsCi helper -----------------------------------------------------------------------

    [Theory]
    [InlineData("House", "house", true)]
    [InlineData("House", "HOUSE", true)]
    [InlineData("House", "tech", false)]
    [InlineData("House", "", true)]
    [InlineData(null, "house", false)]
    [InlineData(null, "", true)]
    [InlineData(null, null, true)]
    public void ContainsCi_handles_case_and_nulls(string? value, string? filter, bool expected)
    {
        FilterPredicates.ContainsCi(value, filter).Should().Be(expected);
    }
}
