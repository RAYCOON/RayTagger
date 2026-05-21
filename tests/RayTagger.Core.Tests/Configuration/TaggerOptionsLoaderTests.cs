using RayTagger.Core.Configuration;
using RayTagger.Core.Tests.Support;

namespace RayTagger.Core.Tests.Configuration;

public class TaggerOptionsLoaderTests
{
    [Fact]
    public void Loads_the_example_yaml_successfully()
    {
        // Example references env vars; provide stubs so the loader doesn't fail on them.
        var dotEnv = new Dictionary<string, string>
        {
            ["ACOUSTID_API_KEY"] = "stub-acoustid",
            ["DISCOGS_TOKEN"]    = "stub-discogs",
            ["LASTFM_API_KEY"]   = "stub-lastfm",
        };

        var yaml = File.ReadAllText(RepoRoot.Samples("tagger.example.yaml"));

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path, dotEnv);

        options.Version.Should().Be(1);
        options.Scan.Source.Should().NotBeNullOrWhiteSpace();
        options.Analysis.Bpm.Enabled.Should().BeTrue();
        options.Analysis.Bpm.Provider.Should().Be("essentia");
        options.Analysis.Bpm.MinConfidence.Should().BeApproximately(0.4, 0.001);
        options.Lookup.Providers.Should().BeEquivalentTo(["acoustid", "musicbrainz", "discogs", "lastfm"]);
        options.Lookup.ApiKeys.Acoustid.Should().Be("stub-acoustid");
        options.Write.TagFields["genre"].Should().BeEquivalentTo(["ID3:TCON", "VORBIS:GENRE"]);
    }

    [Fact]
    public void Missing_required_env_var_fails_with_helpful_message()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            lookup:
              api_keys:
                acoustid: "${env:DEFINITELY_NOT_SET_ABCXYZ}"
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Which.Message.Should().Contain("DEFINITELY_NOT_SET_ABCXYZ");
    }

    [Fact]
    public void Missing_scan_source_fails_validation()
    {
        const string yaml = """
            version: 1
            scan:
              source: ""
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var ex = act.Should().Throw<ConfigurationException>().Which;
        ex.Errors.Should().Contain(e => e.YamlPath == "scan.source");
    }

    [Fact]
    public void Unknown_format_in_scan_is_reported()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
              formats: ["mp3", "ogg"]
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var ex = act.Should().Throw<ConfigurationException>().Which;
        ex.Errors.Should().Contain(e => e.YamlPath == "scan.formats" && e.Reason.Contains("ogg", StringComparison.Ordinal));
    }

    [Fact]
    public void Min_confidence_outside_zero_to_one_is_reported()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              bpm:
                provider: aubio
                min_confidence: 1.5
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var ex = act.Should().Throw<ConfigurationException>().Which;
        ex.Errors.Should().Contain(e => e.YamlPath == "analysis.bpm.min_confidence");
    }

    [Fact]
    public void Relative_source_path_resolves_against_config_directory()
    {
        const string yaml = """
            version: 1
            scan:
              source: "./incoming"
            """;

        var tempDir = Path.Combine(Path.GetTempPath(), "tagger-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var options = TaggerOptionsLoader.LoadFromString(yaml, tempDir);

            options.Scan.Source.Should().Be(Path.GetFullPath(Path.Combine(tempDir, "incoming")));
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void Unknown_top_level_key_fails_loud()
    {
        // Regression for the "strict unknown-property handling" guarantee documented in CLAUDE.md.
        // A typo at the top level must surface immediately, not be silently dropped.
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            tpyo: oops
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>();
    }

    [Fact]
    public void Unknown_nested_key_fails_loud()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
              parellelism: 4
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>();
    }

    [Fact]
    public void Display_notation_parses_as_enum()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              key:
                provider: keyfinder-cli
                display_notation: standard
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        options.Analysis.Key.DisplayNotation.Should().Be(KeyDisplayNotation.Standard);
    }

    [Fact]
    public void Invalid_display_notation_value_fails_at_load_time()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              key:
                provider: keyfinder-cli
                display_notation: rainbow
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        // YamlDotNet rejects unknown enum values directly — the loader wraps that as a
        // ConfigurationException.
        act.Should().Throw<ConfigurationException>();
    }

    [Fact]
    public void Deserialises_tempo_ranges_by_genre_block_with_flow_style()
    {
        // Flow-style mapping (inline {min: ..., max: ...}) is what the example.yaml uses for
        // readability. Pins both the binding and the case-insensitive comparer fixup that
        // NormaliseDictionaryComparers applies post-deserialisation.
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              bpm:
                provider: essentia
                tempo_ranges_by_genre:
                  house:         { min: 110, max: 140 }
                  techno:        { min: 110, max: 150 }
                  drum and bass: { min: 130, max: 200 }
                tempo_range_fallback: { min: 60, max: 200 }
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        options.Analysis.Bpm.TempoRangesByGenre.Should().HaveCount(3);
        options.Analysis.Bpm.TempoRangesByGenre["house"].Min.Should().Be(110);
        options.Analysis.Bpm.TempoRangesByGenre["house"].Max.Should().Be(140);
        options.Analysis.Bpm.TempoRangesByGenre["drum and bass"].Max.Should().Be(200);

        // Case-insensitive lookup must work after deserialisation — the post-load fixup rebuilds
        // the dict with OrdinalIgnoreCase comparer (YamlDotNet drops the POCO's pre-set comparer).
        options.Analysis.Bpm.TempoRangesByGenre.ContainsKey("HOUSE").Should().BeTrue();
        options.Analysis.Bpm.TempoRangesByGenre.ContainsKey("House").Should().BeTrue();

        options.Analysis.Bpm.TempoRangeFallback.Should().NotBeNull();
        options.Analysis.Bpm.TempoRangeFallback!.Min.Should().Be(60);
        options.Analysis.Bpm.TempoRangeFallback.Max.Should().Be(200);
    }

    [Fact]
    public void Empty_tempo_ranges_by_genre_is_valid_and_leaves_dict_empty()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        options.Analysis.Bpm.TempoRangesByGenre.Should().BeEmpty();
        options.Analysis.Bpm.TempoRangeFallback.Should().BeNull();
    }

    [Fact]
    public void Tempo_range_with_min_gte_max_fails_validation()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              bpm:
                provider: essentia
                tempo_ranges_by_genre:
                  house: { min: 150, max: 120 }
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .WithMessage("*must be strictly less than max*");
    }

    [Fact]
    public void Tempo_range_with_only_min_or_only_max_fails_validation()
    {
        // Mixed configuration — one of min/max missing — is a misconfiguration the validator
        // should surface clearly.
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              bpm:
                provider: essentia
                tempo_ranges_by_genre:
                  house: { min: 110 }
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .WithMessage("*min and max must be set together*");
    }

    [Fact]
    public void Tempo_range_outside_plausible_bounds_fails_validation()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              bpm:
                provider: essentia
                tempo_ranges_by_genre:
                  techno: { min: 5, max: 400 }
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .WithMessage("*outside plausible musical bounds*");
    }
}
