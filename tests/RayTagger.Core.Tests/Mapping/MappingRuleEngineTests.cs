using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Core.Tests.Mapping;

public class MappingRuleEngineTests
{
    private static readonly TrackFile DefaultFile = new(
        "/library/Promo/track.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    private static readonly MappingRuleEngine Engine = new();

    // ===== Catch-all =================================================================================

    [Fact]
    public void Catch_all_with_omitted_when_matches_everything()
    {
        var rule = new MappingRule
        {
            Name = "fallback",
            When = null,
            Set = new SetClause { Genre = "Unsorted" },
        };

        var result = EvaluateSingle(rule, tags: WithGenre("anything"));

        result.Tags.Genre.Value.Should().Be("Unsorted");
        result.Tags.Genre.Source.Should().Be(TagFieldSource.Rules);
    }

    [Fact]
    public void Catch_all_with_empty_when_matches_everything()
    {
        var rule = new MappingRule
        {
            Name = "fallback",
            When = new WhenClause(),
            Set = new SetClause { Genre = "Unsorted" },
        };

        var result = EvaluateSingle(rule);

        result.Applied.Should().ContainSingle();
    }

    // ===== Genre literal / case sensitivity =========================================================

    [Fact]
    public void Genre_literal_matches_case_insensitive_by_default()
    {
        var rule = MakeRule("hit", when: new WhenClause { Genre = GenrePattern.FromLiterals(["House"]) },
            set: new SetClause { Genre = "Electronic" });

        var result = EvaluateSingle(rule, tags: WithGenre("HOUSE"));

        result.Applied.Should().ContainSingle();
        result.Tags.Genre.Value.Should().Be("Electronic");
    }

    [Fact]
    public void Genre_literal_respects_case_sensitive_default()
    {
        var rule = MakeRule("hit", when: new WhenClause { Genre = GenrePattern.FromLiterals(["House"]) },
            set: new SetClause { Genre = "Electronic" });

        var ruleset = new MappingRuleSet { Defaults = new MappingDefaults { CaseSensitive = true }, Rules = [rule] };
        var result = Engine.Evaluate(WithGenre("house"), existing: null, DefaultFile, ruleset);

        result.Applied.Should().BeEmpty();
    }

    // ===== Sentinels ================================================================================

    [Fact]
    public void Missing_sentinel_matches_when_genre_is_null_or_empty()
    {
        var rule = MakeRule("default-genre", when: new WhenClause { Genre = GenrePattern.Missing },
            set: new SetClause { Genre = "Unsorted" });

        var withNothing = EvaluateSingle(rule, tags: ResolvedTrackTags.Empty);
        withNothing.Applied.Should().ContainSingle();

        var withSomething = EvaluateSingle(rule, tags: WithGenre("Techno"));
        withSomething.Applied.Should().BeEmpty();
    }

    [Fact]
    public void Any_sentinel_matches_when_genre_is_non_empty()
    {
        var rule = MakeRule("tag-anything", when: new WhenClause { Subgenre = GenrePattern.Any },
            set: new SetClause { ExtraTags = { ["tagged"] = "yes" } });

        var hit = EvaluateSingle(rule, tags: ResolvedTrackTags.Empty with
        {
            SubGenre = new ResolvedField<string>("Anything", TagFieldSource.Existing, 1),
        });

        hit.Applied.Should().ContainSingle();
    }

    // ===== Regex ====================================================================================

    [Fact]
    public void Genre_regex_matches_with_case_insensitive_default()
    {
        var rule = MakeRule("dnb", when: new WhenClause { GenreRegex = "^(drum.?(and|n|&).?bass|dnb)$" },
            set: new SetClause { Genre = "Electronic", Subgenre = "Drum & Bass" });

        var result = EvaluateSingle(rule, tags: WithGenre("Drum and Bass"));

        result.Tags.Genre.Value.Should().Be("Electronic");
        result.Tags.SubGenre.Value.Should().Be("Drum & Bass");
    }

    [Fact]
    public void Regex_does_not_match_null_field()
    {
        var rule = MakeRule("any-regex", when: new WhenClause { GenreRegex = ".*" },
            set: new SetClause { Genre = "X" });

        var result = EvaluateSingle(rule, tags: ResolvedTrackTags.Empty);

        result.Applied.Should().BeEmpty();
    }

    // ===== BPM / Energy range =======================================================================

    [Fact]
    public void Bpm_range_matches_inclusively()
    {
        var rule = MakeRule("peak-time",
            when: new WhenClause { Bpm = new NumericRange { Min = 122, Max = 128 } },
            set: new SetClause { Subgenre = "Peak Time" });

        EvaluateSingle(rule, tags: WithBpm(122)).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithBpm(125)).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithBpm(128)).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithBpm(121.9)).Applied.Should().BeEmpty();
        EvaluateSingle(rule, tags: WithBpm(128.1)).Applied.Should().BeEmpty();
    }

    [Fact]
    public void Bpm_range_skips_rule_when_bpm_is_null()
    {
        // Documented semantics: missing signal MUST NOT cause a false match. Otherwise a tagger
        // run without analysis would mark every untagged file with the same fallback bucket.
        var rule = MakeRule("hi-bpm",
            when: new WhenClause { Bpm = new NumericRange { Min = 0, Max = 999 } },
            set: new SetClause { Genre = "Anything" });

        var result = EvaluateSingle(rule, tags: ResolvedTrackTags.Empty);

        result.Applied.Should().BeEmpty();
    }

    [Fact]
    public void Energy_range_matches_inclusively_and_skips_when_null()
    {
        var rule = MakeRule("loud",
            when: new WhenClause { Energy = new NumericRange { Min = 8, Max = 10 } },
            set: new SetClause { ExtraTags = { ["mood"] = "Peak Energy" } });

        EvaluateSingle(rule, tags: WithEnergy(9)).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithEnergy(7)).Applied.Should().BeEmpty();
        EvaluateSingle(rule, tags: ResolvedTrackTags.Empty).Applied.Should().BeEmpty();
    }

    // ===== Key / KeyMode ============================================================================

    [Fact]
    public void Key_predicate_matches_camelot_and_standard_notation()
    {
        var rule = MakeRule("a-minor-key",
            when: new WhenClause { Key = { "8A" } },
            set: new SetClause { ExtraTags = { ["mood"] = "Calm" } });

        EvaluateSingle(rule, tags: WithKey(new MusicalKey("Am", "8A"))).Applied.Should().ContainSingle();

        var standardOnly = MakeRule("am-rule",
            when: new WhenClause { Key = { "Am" } },
            set: new SetClause { ExtraTags = { ["mood"] = "Calm" } });

        EvaluateSingle(standardOnly, tags: WithKey(new MusicalKey("Am", "8A"))).Applied.Should().ContainSingle();
    }

    [Fact]
    public void KeyMode_minor_matches_camelot_A_suffix()
    {
        var rule = MakeRule("minor",
            when: new WhenClause { KeyMode = KeyMode.Minor },
            set: new SetClause { ExtraTags = { ["mood"] = "Dark" } });

        EvaluateSingle(rule, tags: WithKey(new MusicalKey("Am", "8A"))).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithKey(new MusicalKey("C", "8B"))).Applied.Should().BeEmpty();
    }

    // ===== Artist / Path glob =======================================================================

    [Fact]
    public void Artist_literal_matches_against_existing_track_tags()
    {
        var rule = MakeRule("known-artist",
            when: new WhenClause { Artist = { "Daft Punk" } },
            set: new SetClause { Genre = "Electronic" });

        var existing = TrackTags.Empty with { Artist = "Daft Punk" };
        var result = Engine.Evaluate(ResolvedTrackTags.Empty, existing, DefaultFile,
            new MappingRuleSet { Rules = [rule] });

        result.Applied.Should().ContainSingle();
    }

    [Fact]
    public void Path_glob_matches_against_track_file_path()
    {
        var rule = MakeRule("promo-folder",
            when: new WhenClause { PathGlob = "**/Promo/**" },
            set: new SetClause { ExtraTags = { ["label"] = "Promo" } });

        EvaluateSingle(rule).Applied.Should().ContainSingle();

        var nonPromo = new TrackFile("/library/Releases/track.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);
        var result = Engine.Evaluate(ResolvedTrackTags.Empty, existing: null, nonPromo,
            new MappingRuleSet { Rules = [rule] });
        result.Applied.Should().BeEmpty();
    }

    // ===== any_of / all_of / not ====================================================================

    [Fact]
    public void Any_of_combines_child_clauses_with_logical_or()
    {
        var rule = MakeRule("hip-hop-family",
            when: new WhenClause
            {
                AnyOf =
                [
                    new WhenClause { Genre = GenrePattern.FromLiterals(["Hip-Hop", "Rap"]) },
                    new WhenClause { GenreRegex = "^(trap|boom.?bap)$" },
                ],
            },
            set: new SetClause { Genre = "Hip-Hop" });

        EvaluateSingle(rule, tags: WithGenre("Rap")).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithGenre("Trap")).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithGenre("Jazz")).Applied.Should().BeEmpty();
    }

    [Fact]
    public void All_of_combines_child_clauses_with_logical_and()
    {
        var rule = MakeRule("strict",
            when: new WhenClause
            {
                AllOf =
                [
                    new WhenClause { Genre = GenrePattern.FromLiterals(["House"]) },
                    new WhenClause { Bpm = new NumericRange { Min = 122, Max = 128 } },
                ],
            },
            set: new SetClause { Subgenre = "Peak Time" });

        EvaluateSingle(rule, tags: WithGenre("House") with
        {
            Bpm = new ResolvedValueField<double>(125, TagFieldSource.Analysis, 1),
        }).Applied.Should().ContainSingle();

        // Only one of the two child clauses matches → all_of fails.
        EvaluateSingle(rule, tags: WithGenre("House")).Applied.Should().BeEmpty();
    }

    [Fact]
    public void Not_inverts_a_child_clause()
    {
        var rule = MakeRule("non-electronic-fallback",
            when: new WhenClause { Not = new WhenClause { Genre = GenrePattern.FromLiterals(["Electronic"]) } },
            set: new SetClause { ExtraTags = { ["family"] = "Other" } });

        EvaluateSingle(rule, tags: WithGenre("Jazz")).Applied.Should().ContainSingle();
        EvaluateSingle(rule, tags: WithGenre("Electronic")).Applied.Should().BeEmpty();
    }

    // ===== OnMatch ===================================================================================

    [Fact]
    public void OnMatch_stop_is_default_and_aborts_after_first_hit()
    {
        var first = MakeRule("a", when: new WhenClause { Genre = GenrePattern.FromLiterals(["House"]) },
            set: new SetClause { Genre = "First" });
        var second = MakeRule("b", when: new WhenClause { Genre = GenrePattern.FromLiterals(["House"]) },
            set: new SetClause { Genre = "Second" });

        var ruleset = new MappingRuleSet { Rules = [first, second] };
        var result = Engine.Evaluate(WithGenre("House"), existing: null, DefaultFile, ruleset);

        result.Applied.Should().ContainSingle().Which.RuleName.Should().Be("a");
        result.Tags.Genre.Value.Should().Be("First");
    }

    [Fact]
    public void OnMatch_continue_lets_subsequent_rules_refine_the_result()
    {
        var first = new MappingRule
        {
            Name = "broad",
            When = new WhenClause { Genre = GenrePattern.FromLiterals(["House", "Tech House"]) },
            Set = new SetClause { Genre = "Electronic" },
            OnMatch = OnMatch.Continue,
        };
        var second = MakeRule("specific", when: new WhenClause { Genre = GenrePattern.FromLiterals(["Electronic"]) },
            set: new SetClause { Subgenre = "House" });

        var ruleset = new MappingRuleSet { Rules = [first, second] };
        var result = Engine.Evaluate(WithGenre("Tech House"), existing: null, DefaultFile, ruleset);

        result.Applied.Should().HaveCount(2);
        result.Tags.Genre.Value.Should().Be("Electronic");
        result.Tags.SubGenre.Value.Should().Be("House");
    }

    // ===== Set actions ===============================================================================

    [Fact]
    public void Empty_string_in_set_clears_the_field()
    {
        var rule = MakeRule("clear-subgenre",
            when: new WhenClause { Genre = GenrePattern.FromLiterals(["House"]) },
            set: new SetClause { Subgenre = "" });

        var tags = WithGenre("House") with
        {
            SubGenre = new ResolvedField<string>("Old", TagFieldSource.Existing, 1),
        };
        var result = EvaluateSingle(rule, tags: tags);

        result.Tags.SubGenre.Value.Should().BeNull();
        result.Tags.SubGenre.Source.Should().Be(TagFieldSource.Rules);
    }

    [Fact]
    public void Set_extra_tags_populate_custom_field_with_rules_source()
    {
        var rule = MakeRule("label",
            when: new WhenClause { PathGlob = "**/Promo/**" },
            set: new SetClause { ExtraTags = { ["label"] = "Promo" } });

        var result = EvaluateSingle(rule);

        result.Tags.Custom.Should().ContainKey("label");
        result.Tags.Custom["label"].Value.Should().Be("Promo");
        result.Tags.Custom["label"].Source.Should().Be(TagFieldSource.Rules);
    }

    [Fact]
    public void Add_keyword_appends_to_existing_keywords_with_semicolon_separator()
    {
        var firstHit = MakeRule("first", when: new WhenClause { Genre = GenrePattern.Any },
            set: new SetClause { AddKeyword = "Tagged" });
        var secondHit = new MappingRule
        {
            Name = "second",
            When = new WhenClause { Genre = GenrePattern.Any },
            Set = new SetClause { AddKeyword = "Reviewed" },
            OnMatch = OnMatch.Continue,
        };
        // Make first one continue too, so both fire:
        firstHit.OnMatch = OnMatch.Continue;

        var ruleset = new MappingRuleSet { Rules = [firstHit, secondHit] };
        var result = Engine.Evaluate(WithGenre("House"), existing: null, DefaultFile, ruleset);

        result.Tags.Custom["keywords"].Value.Should().Be("Tagged;Reviewed");
    }

    // ===== Helpers ===================================================================================

    private static MappingRule MakeRule(string name, WhenClause when, SetClause set) =>
        new() { Name = name, When = when, Set = set };

    private static MappingEvaluationResult EvaluateSingle(MappingRule rule, ResolvedTrackTags? tags = null)
    {
        var ruleset = new MappingRuleSet { Rules = [rule] };
        return Engine.Evaluate(tags ?? ResolvedTrackTags.Empty, existing: null, DefaultFile, ruleset);
    }

    private static ResolvedTrackTags WithGenre(string genre) =>
        ResolvedTrackTags.Empty with { Genre = new ResolvedField<string>(genre, TagFieldSource.Existing, 1.0) };

    private static ResolvedTrackTags WithBpm(double bpm) =>
        ResolvedTrackTags.Empty with { Bpm = new ResolvedValueField<double>(bpm, TagFieldSource.Analysis, 1.0) };

    private static ResolvedTrackTags WithEnergy(int energy) =>
        ResolvedTrackTags.Empty with { Energy = new ResolvedValueField<int>(energy, TagFieldSource.Analysis, 1.0) };

    private static ResolvedTrackTags WithKey(MusicalKey key) =>
        ResolvedTrackTags.Empty with { Key = new ResolvedField<MusicalKey>(key, TagFieldSource.Analysis, 1.0) };
}
