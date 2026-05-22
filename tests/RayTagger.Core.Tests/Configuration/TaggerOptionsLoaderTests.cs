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
    public void Genre_classifier_section_round_trips()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              genre_classifier:
                heuristic:
                  enabled: true
                  min_confidence: 0.62
                tensorflow:
                  genre_electronic:
                    enabled: true
                    min_confidence: 0.65
                  mtg_jamendo:
                    enabled: false
                    min_confidence: 0.50
                  discogs_effnet:
                    enabled: true
                    min_confidence: 0.55
                  python_executable: "/opt/python3"
                  script_path: "/opt/raytagger/genre_classifier.py"
                  models_directory: "/var/models"
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var gc = options.Analysis.GenreClassifier;
        gc.Heuristic.Enabled.Should().BeTrue();
        gc.Heuristic.MinConfidence.Should().BeApproximately(0.62, 0.001);
        gc.Tensorflow.GenreElectronic.Enabled.Should().BeTrue();
        gc.Tensorflow.GenreElectronic.MinConfidence.Should().BeApproximately(0.65, 0.001);
        gc.Tensorflow.MtgJamendo.Enabled.Should().BeFalse();
        gc.Tensorflow.DiscogsEffnet.Enabled.Should().BeTrue();
        gc.Tensorflow.PythonExecutable.Should().Be("/opt/python3");
        gc.Tensorflow.ScriptPath.Should().Be("/opt/raytagger/genre_classifier.py");
        gc.Tensorflow.ModelsDirectory.Should().Be("/var/models");
    }

    [Fact]
    public void Genre_classifier_section_defaults_when_missing()
    {
        // No analysis.genre_classifier block at all → default-constructed POCOs, all flags off.
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var gc = options.Analysis.GenreClassifier;
        gc.Heuristic.Enabled.Should().BeFalse();
        gc.Heuristic.MinConfidence.Should().BeApproximately(0.55, 0.001);
        gc.Tensorflow.GenreElectronic.Enabled.Should().BeFalse();
        gc.Tensorflow.MtgJamendo.Enabled.Should().BeFalse();
        gc.Tensorflow.DiscogsEffnet.Enabled.Should().BeFalse();

        // Asymmetric defaults — see B3 in docs/PLAN_GENRE_CLASSIFICATION.md.
        // genre_electronic floor is raised because it overlaps with the Phase A heuristic;
        // mtg_jamendo and discogs_effnet stay lower because they cover unique territory.
        gc.Tensorflow.GenreElectronic.MinConfidence.Should().BeApproximately(0.65, 0.001);
        gc.Tensorflow.MtgJamendo.MinConfidence.Should().BeApproximately(0.50, 0.001);
        gc.Tensorflow.DiscogsEffnet.MinConfidence.Should().BeApproximately(0.50, 0.001);
    }

    [Fact]
    public void Each_tensorflow_model_has_independently_configurable_min_confidence()
    {
        // Behavioural test: the user can set a different min_confidence per model and the
        // loader plumbs them through cleanly. Catches accidental shared-state regressions
        // (e.g. if someone refactors TensorflowModelOptions into a singleton).
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              genre_classifier:
                tensorflow:
                  genre_electronic:
                    enabled: true
                    min_confidence: 0.80
                  mtg_jamendo:
                    enabled: true
                    min_confidence: 0.35
                  discogs_effnet:
                    enabled: true
                    min_confidence: 0.42
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);
        var gc = options.Analysis.GenreClassifier;

        gc.Tensorflow.GenreElectronic.MinConfidence.Should().BeApproximately(0.80, 0.001);
        gc.Tensorflow.MtgJamendo.MinConfidence.Should().BeApproximately(0.35, 0.001);
        gc.Tensorflow.DiscogsEffnet.MinConfidence.Should().BeApproximately(0.42, 0.001);
    }

    [Fact]
    public void Tensorflow_genre_electronic_default_min_confidence_is_higher_than_other_models()
    {
        // Design-documentation test. If a future refactor unifies the defaults, this test
        // fires and forces the change to go through review with the original rationale in
        // mind: genre_electronic overlaps with the Phase A heuristic, so its floor is raised
        // to act as a sanity-check / second opinion. See docs/PLAN_GENRE_CLASSIFICATION.md §4.0.
        var defaults = new TensorflowClassifierOptions();

        defaults.GenreElectronic.MinConfidence
            .Should().BeGreaterThan(defaults.MtgJamendo.MinConfidence,
                because: "genre_electronic overlaps with the Phase A heuristic — its floor is raised on purpose");
        defaults.GenreElectronic.MinConfidence
            .Should().BeGreaterThan(defaults.DiscogsEffnet.MinConfidence,
                because: "genre_electronic offers no unique coverage vs. heuristic, unlike discogs_effnet's subgenres");
    }

    [Fact]
    public void Heuristic_classifier_min_confidence_outside_zero_to_one_is_reported()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              genre_classifier:
                heuristic:
                  min_confidence: 1.5
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var ex = act.Should().Throw<ConfigurationException>().Which;
        ex.Errors.Should().Contain(e => e.YamlPath == "analysis.genre_classifier.heuristic.min_confidence");
    }

    [Fact]
    public void Tensorflow_genre_electronic_min_confidence_outside_zero_to_one_is_reported()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            analysis:
              genre_classifier:
                tensorflow:
                  genre_electronic:
                    min_confidence: -0.1
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var ex = act.Should().Throw<ConfigurationException>().Which;
        ex.Errors.Should().Contain(e => e.YamlPath == "analysis.genre_classifier.tensorflow.genre_electronic.min_confidence");
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

    [Fact]
    public void Rate_limits_default_to_published_provider_policies()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        options.Lookup.RateLimits.AcoustidMs.Should().Be(RateLimitsOptions.DefaultAcoustidMs);
        options.Lookup.RateLimits.MusicbrainzMs.Should().Be(RateLimitsOptions.DefaultMusicbrainzMs);
        options.Lookup.RateLimits.DiscogsMs.Should().Be(RateLimitsOptions.DefaultDiscogsMs);
        options.Lookup.RateLimits.LastfmMs.Should().Be(RateLimitsOptions.DefaultLastfmMs);
    }

    [Fact]
    public void Rate_limits_can_be_overridden_per_provider()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            lookup:
              rate_limits:
                acoustid_ms:    500
                musicbrainz_ms: 2000
                discogs_ms:     1500
                lastfm_ms:      300
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        options.Lookup.RateLimits.AcoustidMs.Should().Be(500);
        options.Lookup.RateLimits.MusicbrainzMs.Should().Be(2000);
        options.Lookup.RateLimits.DiscogsMs.Should().Be(1500);
        options.Lookup.RateLimits.LastfmMs.Should().Be(300);
    }

    [Fact]
    public void Partial_rate_limits_block_keeps_other_defaults()
    {
        // Only override MB — the other three must retain their documented defaults so a sloppy
        // user can't accidentally disable AcoustID throttling by writing only one entry.
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            lookup:
              rate_limits:
                musicbrainz_ms: 1500
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        options.Lookup.RateLimits.MusicbrainzMs.Should().Be(1500);
        options.Lookup.RateLimits.AcoustidMs.Should().Be(RateLimitsOptions.DefaultAcoustidMs);
        options.Lookup.RateLimits.DiscogsMs.Should().Be(RateLimitsOptions.DefaultDiscogsMs);
        options.Lookup.RateLimits.LastfmMs.Should().Be(RateLimitsOptions.DefaultLastfmMs);
    }

    [Fact]
    public void Negative_rate_limit_fails_validation()
    {
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            lookup:
              rate_limits:
                musicbrainz_ms: -100
            """;

        var act = () => TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        var ex = act.Should().Throw<ConfigurationException>().Which;
        ex.Errors.Should().Contain(e =>
            e.YamlPath == "lookup.rate_limits.musicbrainz_ms"
            && e.Reason.Contains(">= 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_rate_limit_is_accepted()
    {
        // 0 = "no client-side throttling". Foot-gun, but valid — the user might be testing
        // against a local MB mirror where rate-limiting isn't enforced.
        const string yaml = """
            version: 1
            scan:
              source: "~/music"
            lookup:
              rate_limits:
                musicbrainz_ms: 0
            """;

        var options = TaggerOptionsLoader.LoadFromString(yaml, RepoRoot.Path);

        options.Lookup.RateLimits.MusicbrainzMs.Should().Be(0);
    }
}
