using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Tests.Support;

namespace RayTagger.Core.Tests.Mapping;

public class MappingRulesLoaderTests
{
    [Fact]
    public void Loads_the_example_yaml_successfully()
    {
        var yaml = File.ReadAllText(RepoRoot.Samples("mappings.example.yaml"));

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Version.Should().Be(1);
        ruleSet.Defaults.OnMatch.Should().Be(OnMatch.Stop);
        ruleSet.Rules.Should().NotBeEmpty();
        ruleSet.Rules.Should().Contain(r => r.Name == "Tech House — peak time");
        ruleSet.Rules.Should().Contain(r => r.Name == "Default: still no genre → 'Unsorted'");
    }

    [Fact]
    public void Missing_sentinel_parses_into_MatchMissing_pattern()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "fallback"
                when:
                  genre: ":missing:"
                set:
                  genre: "Unsorted"
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Rules[0].When!.Genre!.MatchMissing.Should().BeTrue();
        ruleSet.Rules[0].When!.Genre!.MatchAny.Should().BeFalse();
        ruleSet.Rules[0].When!.Genre!.Values.Should().BeEmpty();
    }

    [Fact]
    public void Any_sentinel_parses_into_MatchAny_pattern()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "any-genre-rule"
                when:
                  genre: ":any:"
                set:
                  tag.processed: "true"
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Rules[0].When!.Genre!.MatchAny.Should().BeTrue();
        ruleSet.Rules[0].Set.ExtraTags.Should().Contain("processed", "true");
    }

    [Fact]
    public void Unknown_sentinel_produces_validation_error()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "bad"
                when:
                  genre: ":unknown:"
                set:
                  genre: "X"
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e =>
                e.YamlPath == "rules[0].when.genre" &&
                e.Reason.Contains(":unknown:", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_when_block_is_catch_all()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "catch-all"
                when: {}
                set:
                  genre: "Default"
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        var rule = ruleSet.Rules[0];
        rule.When.Should().NotBeNull();
        rule.When!.Genre.Should().BeNull();
        rule.When.Bpm.Should().BeNull();
    }

    [Fact]
    public void Missing_when_key_is_catch_all_via_null()
    {
        // YAML omits `when:` entirely — the rule should still parse and act as catch-all.
        const string yaml = """
            version: 1
            rules:
              - name: "catch-all-no-when"
                set:
                  genre: "Default"
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Rules[0].When.Should().BeNull();
    }

    [Fact]
    public void Inverted_bpm_range_is_reported()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "bad-range"
                when:
                  bpm: { min: 130, max: 120 }
                set:
                  genre: "X"
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e =>
                e.YamlPath == "rules[0].when.bpm" &&
                e.Reason.Contains("max", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_rule_name_is_reported()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "same"
                when: { genre: ["A"] }
                set: { genre: "X" }
              - name: "same"
                when: { genre: ["B"] }
                set: { genre: "Y" }
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e =>
                e.YamlPath == "rules[1].name" &&
                e.Reason.Contains("Duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_when_predicate_key_is_reported()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "typo"
                when:
                  geenre: ["House"]
                set:
                  genre: "X"
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e =>
                e.YamlPath == "rules[0].when.geenre");
    }

    [Fact]
    public void Tag_dot_prefix_keys_flow_into_ExtraTags()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "high-energy"
                when:
                  energy: { min: 9, max: 10 }
                set:
                  tag.mood: "Peak Energy"
                  tag.label: "Banger"
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Rules[0].Set.ExtraTags.Should().Contain("mood", "Peak Energy");
        ruleSet.Rules[0].Set.ExtraTags.Should().Contain("label", "Banger");
    }

    [Fact]
    public void On_match_continue_parses()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "keep-going"
                when: { genre: ["A"] }
                set:  { genre: "B" }
                on_match: continue
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Rules[0].OnMatch.Should().Be(OnMatch.Continue);
    }

    [Fact]
    public void Unknown_top_level_key_is_reported()
    {
        const string yaml = """
            version: 1
            typo_here: foo
            rules: []
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e =>
                e.YamlPath == "typo_here" &&
                e.Reason.Contains("Unknown key", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_key_inside_rule_is_reported()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "r1"
                whan: { genre: ["A"] }
                set:  { genre: "X" }
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e =>
                e.YamlPath == "rules[0].whan");
    }

    [Fact]
    public void Invalid_regex_in_genre_regex_is_reported_at_load_time()
    {
        const string yaml = """
            version: 1
            rules:
              - name: "bad-regex"
                when:
                  genre_regex: "(?i)^[unclosed"
                set:
                  genre: "X"
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e =>
                e.YamlPath == "rules[0].when.genre_regex" &&
                e.Reason.Contains("Invalid regex", StringComparison.Ordinal));
    }

    [Fact]
    public void EffectiveOnMatch_falls_back_to_ruleset_defaults()
    {
        const string yaml = """
            version: 1
            defaults:
              on_match: continue
            rules:
              - name: "no-on-match-on-rule"
                when: { genre: ["A"] }
                set:  { genre: "X" }
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Rules[0].OnMatch.Should().BeNull();
        ruleSet.Rules[0].EffectiveOnMatch(ruleSet.Defaults).Should().Be(OnMatch.Continue);
    }

    [Fact]
    public void EffectiveOnMatch_prefers_rule_value_over_defaults()
    {
        const string yaml = """
            version: 1
            defaults:
              on_match: continue
            rules:
              - name: "rule-says-stop"
                when: { genre: ["A"] }
                set:  { genre: "X" }
                on_match: stop
            """;

        var ruleSet = MappingRulesLoader.LoadFromString(yaml);

        ruleSet.Rules[0].EffectiveOnMatch(ruleSet.Defaults).Should().Be(OnMatch.Stop);
    }

    [Fact]
    public void Sentinel_inside_a_list_surfaces_a_configuration_error()
    {
        // Regression: `genre: [":any:"]` used to be silently treated as "match a genre named
        // literally `:any:`" — i.e. never. The fixture mappings hit this and `normalise_genre`
        // never fired. Now the loader rejects it at load time so the next author can't trip on
        // the same footgun.
        const string yaml = """
            version: 1
            rules:
              - name: "normalise"
                when: { genre: [":any:"] }
                set:  { normalise_genre: true }
            """;

        var act = () => MappingRulesLoader.LoadFromString(yaml);

        act.Should().Throw<ConfigurationException>()
            .Which.Errors.Should().Contain(e => e.Reason.Contains("Sentinel ':any:'"));
    }
}
