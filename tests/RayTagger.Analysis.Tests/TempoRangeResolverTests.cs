using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

public class TempoRangeResolverTests
{
    private static BpmAnalyzerOptions OptionsWith(params (string Genre, double Min, double Max)[] ranges)
    {
        var opts = new BpmAnalyzerOptions { Provider = "essentia" };
        foreach (var (g, lo, hi) in ranges)
        {
            opts.TempoRangesByGenre[g] = new BpmTempoRange(lo, hi);
        }
        return opts;
    }

    private static Taxonomy TaxonomyWith(params (string Alias, string Genre, string Sub)[] entries)
    {
        var dict = new Dictionary<string, (string Genre, string Subgenre)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, genre, sub) in entries)
        {
            dict[alias] = (genre, sub);
        }
        return new Taxonomy { NormaliseByAlias = dict };
    }

    private static TempoRangeResolver Build(BpmAnalyzerOptions opts, Taxonomy? taxonomy = null) =>
        new(opts, taxonomy ?? Taxonomy.Empty, NullLogger<TempoRangeResolver>.Instance);

    [Fact]
    public void Returns_null_when_no_ranges_configured()
    {
        var resolver = Build(new BpmAnalyzerOptions());
        resolver.Resolve(new TrackTags(Genre: "House")).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_genre_tag_empty()
    {
        var opts = OptionsWith(("House", 110, 140));
        var resolver = Build(opts);
        resolver.Resolve(new TrackTags(Genre: null)).Should().BeNull();
        resolver.Resolve(new TrackTags(Genre: "")).Should().BeNull();
        resolver.Resolve(new TrackTags(Genre: "   ")).Should().BeNull();
    }

    [Fact]
    public void Direct_exact_match_returns_range_when_no_taxonomy()
    {
        var opts = OptionsWith(("House", 110, 140));
        var resolver = Build(opts);
        var range = resolver.Resolve(new TrackTags(Genre: "House"));
        range.Should().Be(new BpmTempoRange(110, 140));
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var opts = OptionsWith(("House", 110, 140));
        var resolver = Build(opts);
        resolver.Resolve(new TrackTags(Genre: "house")).Should().Be(new BpmTempoRange(110, 140));
        resolver.Resolve(new TrackTags(Genre: "HOUSE")).Should().Be(new BpmTempoRange(110, 140));
        resolver.Resolve(new TrackTags(Genre: "HoUsE")).Should().Be(new BpmTempoRange(110, 140));
    }

    [Fact]
    public void Taxonomy_normalises_sub_genre_to_main_before_lookup()
    {
        var opts = OptionsWith(("House", 110, 140));
        var taxonomy = TaxonomyWith(
            ("Tech House", "House", "Tech"),
            ("Deep House", "House", "Deep"),
            ("Progressive House", "House", "Progressive"));
        var resolver = Build(opts, taxonomy);

        resolver.Resolve(new TrackTags(Genre: "Tech House")).Should().Be(new BpmTempoRange(110, 140));
        resolver.Resolve(new TrackTags(Genre: "Deep House")).Should().Be(new BpmTempoRange(110, 140));
        resolver.Resolve(new TrackTags(Genre: "Progressive House")).Should().Be(new BpmTempoRange(110, 140));
    }

    [Fact]
    public void Different_sub_genres_resolve_to_their_parent_ranges()
    {
        var opts = OptionsWith(
            ("House", 110, 140),
            ("Techno", 110, 150),
            ("Drum and Bass", 130, 200));
        var taxonomy = TaxonomyWith(
            ("Tech House", "House", "Tech"),
            ("Melodic Techno", "Techno", "Melodic"),
            ("Liquid", "Drum and Bass", "Liquid"));
        var resolver = Build(opts, taxonomy);

        resolver.Resolve(new TrackTags(Genre: "Tech House")).Should().Be(new BpmTempoRange(110, 140));
        resolver.Resolve(new TrackTags(Genre: "Melodic Techno")).Should().Be(new BpmTempoRange(110, 150));
        resolver.Resolve(new TrackTags(Genre: "Liquid")).Should().Be(new BpmTempoRange(130, 200));
    }

    [Fact]
    public void Returns_null_when_genre_not_in_map_and_no_fallback()
    {
        var opts = OptionsWith(("House", 110, 140));
        var resolver = Build(opts);
        resolver.Resolve(new TrackTags(Genre: "Ambient")).Should().BeNull();
    }

    [Fact]
    public void Returns_fallback_when_genre_not_matched()
    {
        var opts = OptionsWith(("House", 110, 140));
        opts.TempoRangeFallback = new BpmTempoRange(60, 200);
        var resolver = Build(opts);

        resolver.Resolve(new TrackTags(Genre: "Ambient"))
            .Should().Be(new BpmTempoRange(60, 200));
    }

    [Fact]
    public void Returns_fallback_when_genre_tag_missing()
    {
        var opts = OptionsWith(("House", 110, 140));
        opts.TempoRangeFallback = new BpmTempoRange(60, 200);
        var resolver = Build(opts);

        resolver.Resolve(new TrackTags(Genre: null))
            .Should().Be(new BpmTempoRange(60, 200));
    }

    [Fact]
    public void Genre_not_in_taxonomy_alias_table_falls_through_as_raw_string()
    {
        // If a genre isn't in the taxonomy's alias table, the raw string is used directly. This
        // matters for genres that are canonical AND not aliased (e.g. "House" itself).
        var opts = OptionsWith(("House", 110, 140));
        var taxonomy = TaxonomyWith(("Tech House", "House", "Tech"));
        var resolver = Build(opts, taxonomy);

        // "House" not in alias table → passthrough → matches "House" key in range map.
        resolver.Resolve(new TrackTags(Genre: "House"))
            .Should().Be(new BpmTempoRange(110, 140));
    }
}
