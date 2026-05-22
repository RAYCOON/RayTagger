using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Core.Tests.Mapping;

public class TaxonomyGenreResolverTests
{
    private static Taxonomy MakeTaxonomy() => new()
    {
        Genres = ["House", "Techno", "Trance", "Drum and Bass", "Hip Hop", "R&B", "Pop", "Pop Rock"],
        Subgenres = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["House"]         = new[] { "Deep", "Tech", "Vocal", "Tropical", "Progressive" },
            ["Techno"]        = new[] { "Detroit", "Acid", "Minimal" },
            ["Drum and Bass"] = new[] { "Liquid", "Neurofunk" },
        },
    };

    private static GenreCandidate Cand(string value, double confidence = 0.8, string source = "musicbrainz")
        => new(value, confidence, source);

    private readonly TaxonomyGenreResolver _resolver = new();

    // -----------------------------------------------------------------------------------------
    // Genre matching — basics
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Übernimmt_match_wenn_existing_leer()
    {
        var result = _resolver.Resolve(
            [Cand("Vocal House", 0.9)], [], MakeTaxonomy(), existingGenre: null, existingSubgenre: null);

        result.ProposedGenre.Should().Be("House");
        result.ProposedSubgenre.Should().Be("Vocal");
        result.ChosenGenre.Should().Be("House");
        result.ChosenSubgenre.Should().Be("Vocal");
        result.FallbackApplied.Should().BeFalse();
    }

    [Fact]
    public void Übernimmt_match_wenn_existing_nicht_in_taxonomy()
    {
        var result = _resolver.Resolve(
            [Cand("Deep House", 0.9)], [], MakeTaxonomy(),
            existingGenre: "Electronic", existingSubgenre: null);

        result.ProposedGenre.Should().Be("House");
        result.ProposedSubgenre.Should().Be("Deep");
    }

    [Fact]
    public void Schützt_existing_wenn_in_taxonomy()
    {
        var result = _resolver.Resolve(
            [Cand("Techno", 0.9)], [], MakeTaxonomy(),
            existingGenre: "House", existingSubgenre: null);

        // existing "House" ist in Taxonomy → Genre wird nicht überschrieben
        result.ProposedGenre.Should().BeNull();
        result.ChosenGenre.Should().Be("Techno");   // Match war da, wurde aber verworfen
    }

    [Fact]
    public void Subgenre_suche_läuft_auch_wenn_genre_geschützt()
    {
        var result = _resolver.Resolve(
            [Cand("Vocal House", 0.9)], [], MakeTaxonomy(),
            existingGenre: "House", existingSubgenre: null);

        // Genre bleibt (geschützt), Subgenre wird übernommen
        result.ProposedGenre.Should().BeNull();
        result.ProposedSubgenre.Should().Be("Vocal");
        result.ChosenGenre.Should().Be("House");
        result.ChosenSubgenre.Should().Be("Vocal");
    }

    // -----------------------------------------------------------------------------------------
    // Whole-word semantics
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Whole_word_match_lehnt_substring_ab()
    {
        // "House" matched in "Vocal House" aber NICHT in "Househeads"
        var result = _resolver.Resolve(
            [Cand("Househeads", 0.9)], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ChosenGenre.Should().BeNull();
        result.FallbackApplied.Should().BeTrue();
        result.ProposedGenre.Should().Be("Househeads");   // Fallback: roh übernommen
    }

    [Fact]
    public void Multiword_genre_drum_and_bass_matched_korrekt()
    {
        var result = _resolver.Resolve(
            [Cand("Liquid Drum and Bass", 0.9)], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ProposedGenre.Should().Be("Drum and Bass");
        result.ProposedSubgenre.Should().Be("Liquid");
    }

    [Fact]
    public void Sonderzeichen_genre_rnb_matched_korrekt()
    {
        var result = _resolver.Resolve(
            [Cand("R&B/Soul", 0.9)], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ProposedGenre.Should().Be("R&B");
    }

    [Fact]
    public void Case_insensitiv_vergleichen_aber_yaml_case_übernehmen()
    {
        var result = _resolver.Resolve(
            [Cand("VOCAL HOUSE", 0.9)], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        // exakt wie in YAML, nicht UPPER
        result.ProposedGenre.Should().Be("House");
        result.ProposedSubgenre.Should().Be("Vocal");
    }

    // -----------------------------------------------------------------------------------------
    // Longest-match tiebreaker
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Längster_genre_match_gewinnt()
    {
        // Kandidat "Pop Rock Classic" matched beide: "Pop" und "Pop Rock". Längster gewinnt.
        var result = _resolver.Resolve(
            [Cand("Pop Rock Classic", 0.9)], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ChosenGenre.Should().Be("Pop Rock");
    }

    [Fact]
    public void Längstes_subgenre_gewinnt_bei_mehrfach_match()
    {
        // Künstliche Taxonomy mit zwei überlappenden Subgenres
        var t = new Taxonomy
        {
            Genres = ["House"],
            Subgenres = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["House"] = new[] { "Deep", "Deep Tech" },
            },
        };
        var result = _resolver.Resolve(
            [Cand("Deep Tech House", 0.9)], [], t,
            existingGenre: null, existingSubgenre: null);

        result.ProposedSubgenre.Should().Be("Deep Tech");
    }

    // -----------------------------------------------------------------------------------------
    // Subgenre source-fusion: rest of genre candidate + SubGenreCandidates
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Subgenre_kommt_aus_separatem_SubGenreCandidate()
    {
        // Genre matched aus GenreCandidate, Subgenre matched aus separatem SubGenreCandidate
        var result = _resolver.Resolve(
            genreCandidates: [Cand("House", 0.9)],
            subGenreCandidates: [Cand("Tech House", 0.85, "discogs")],
            MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ProposedGenre.Should().Be("House");
        result.ProposedSubgenre.Should().Be("Tech");
    }

    [Fact]
    public void Subgenre_aus_rest_überwiegt_bei_höherer_confidence()
    {
        // Beide Quellen liefern Match gleicher Länge ("Vocal" vs "Tech", beide 4 Zeichen?
        // "Vocal" = 5, "Tech" = 4 — nicht gleich. Längster gewinnt zuerst → "Vocal".
        // Setup so dass beide gleichlang sind: "Deep" vs "Tech" beide 4.
        var result = _resolver.Resolve(
            genreCandidates: [Cand("Tech House", 0.9)],
            subGenreCandidates: [Cand("Deep House", 0.85, "discogs")],
            MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        // Beide gleichlang (4). Tiebreaker: höhere Confidence → "Tech" (aus 0.9 rest) gewinnt.
        result.ProposedSubgenre.Should().Be("Tech");
    }

    // -----------------------------------------------------------------------------------------
    // Existing-Subgenre policy
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Existing_subgenre_in_taxonomy_wird_geschützt()
    {
        var result = _resolver.Resolve(
            [Cand("Vocal House", 0.9)], [], MakeTaxonomy(),
            existingGenre: "House", existingSubgenre: "Deep");

        // existing-Subgenre "Deep" ist gültig für House → schützen
        result.ProposedSubgenre.Should().BeNull();
        result.ChosenSubgenre.Should().Be("Vocal");
    }

    [Fact]
    public void Existing_subgenre_nicht_in_taxonomy_wird_ersetzt()
    {
        var result = _resolver.Resolve(
            [Cand("Vocal House", 0.9)], [], MakeTaxonomy(),
            existingGenre: "House", existingSubgenre: "Jazzy");

        result.ProposedSubgenre.Should().Be("Vocal");
    }

    // -----------------------------------------------------------------------------------------
    // Fallback path
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Kein_match_und_existing_leer_übernimmt_top1_roh()
    {
        var result = _resolver.Resolve(
            [Cand("Electronic", 0.9), Cand("IDM", 0.7)], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ChosenGenre.Should().BeNull();
        result.FallbackApplied.Should().BeTrue();
        result.ProposedGenre.Should().Be("Electronic");   // Top-1
        result.ProposedSubgenre.Should().BeNull();        // kein Fallback für Subgenre
    }

    [Fact]
    public void Kein_match_und_existing_voll_bleibt_existing()
    {
        var result = _resolver.Resolve(
            [Cand("Electronic", 0.9)], [], MakeTaxonomy(),
            existingGenre: "Indie Dance", existingSubgenre: null);

        result.ChosenGenre.Should().BeNull();
        result.ProposedGenre.Should().BeNull();           // bleibt
        result.FallbackApplied.Should().BeFalse();
    }

    [Fact]
    public void Leere_kandidatenliste_und_leeres_existing_ergibt_nichts()
    {
        var result = _resolver.Resolve(
            [], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ProposedGenre.Should().BeNull();
        result.ProposedSubgenre.Should().BeNull();
        result.FallbackApplied.Should().BeFalse();
        result.Trace.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Stop-at-first-match
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Stop_beim_ersten_kandidaten_mit_genre_match()
    {
        // Kandidat 1 matched kein Genre, Kandidat 2 matched, Kandidat 3 wird ignoriert.
        var result = _resolver.Resolve(
            [Cand("Electronic", 0.9), Cand("Deep House", 0.7), Cand("Tech House", 0.5)],
            [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.ChosenGenre.Should().Be("House");
        result.ChosenSubgenre.Should().Be("Deep");        // aus Kandidat 2, nicht 3
        result.MatchedCandidate!.Value.Should().Be("Deep House");
        result.Trace.Should().HaveCount(2);               // Kandidat 3 nicht inspiziert
        result.Trace[0].ApiValue.Should().Be("Electronic");
        result.Trace[0].MatchedGenres.Should().BeEmpty();
        result.Trace[1].ApiValue.Should().Be("Deep House");
        result.Trace[1].MatchedGenres.Should().BeEquivalentTo(["House"]);
    }

    // -----------------------------------------------------------------------------------------
    // Genre-cut-out before subgenre search
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Genre_substring_wird_vor_subgenre_suche_rausgeschnitten()
    {
        // Hypothetisches Subgenre "House" unter "Tech" — würde sonst doppelt matchen.
        var t = new Taxonomy
        {
            Genres = ["Tech"],
            Subgenres = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tech"] = new[] { "House", "Hard" },
            },
        };
        var result = _resolver.Resolve(
            [Cand("Tech House", 0.9)], [], t,
            existingGenre: null, existingSubgenre: null);

        // Genre = "Tech", danach rest = "House" — wir wollen NICHT "House" als Subgenre
        // (das wäre Selbst-Match). Sondern: rest enthält "House" → matched Subgenre "House".
        // Erwartung: ja, "House" als Subgenre wird zugewiesen — der Cut-Out entfernt nur "Tech".
        result.ProposedGenre.Should().Be("Tech");
        result.ProposedSubgenre.Should().Be("House");
    }

    [Fact]
    public void Mehrfaches_vorkommen_des_genres_wird_komplett_rausgeschnitten()
    {
        var t = new Taxonomy
        {
            Genres = ["House"],
            Subgenres = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["House"] = new[] { "Deep" },
            },
        };
        // pathological input
        var result = _resolver.Resolve(
            [Cand("Deep House House", 0.9)], [], t,
            existingGenre: null, existingSubgenre: null);

        result.ProposedGenre.Should().Be("House");
        result.ProposedSubgenre.Should().Be("Deep");
    }

    // -----------------------------------------------------------------------------------------
    // Empty / degenerate taxonomy
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Leere_taxonomy_verhält_sich_wie_kein_match()
    {
        var t = Taxonomy.Empty;
        var result = _resolver.Resolve(
            [Cand("Vocal House", 0.9)], [], t,
            existingGenre: null, existingSubgenre: null);

        result.ChosenGenre.Should().BeNull();
        result.FallbackApplied.Should().BeTrue();
        result.ProposedGenre.Should().Be("Vocal House");
    }

    // -----------------------------------------------------------------------------------------
    // Trace shape
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Trace_enthält_jede_inspizierte_kandidaten_zeile()
    {
        var result = _resolver.Resolve(
            [Cand("Vocal House", 0.9, "musicbrainz")], [], MakeTaxonomy(),
            existingGenre: null, existingSubgenre: null);

        result.Trace.Should().HaveCount(1);
        var entry = result.Trace[0];
        entry.ApiSource.Should().Be("musicbrainz");
        entry.Confidence.Should().Be(0.9);
        entry.MatchedGenres.Should().BeEquivalentTo(["House"]);
        entry.ChosenGenre.Should().Be("House");
        entry.MatchedSubgenres.Should().Contain("Vocal");
        entry.ChosenSubgenre.Should().Be("Vocal");
    }

    // -----------------------------------------------------------------------------------------
    // Caching invariant (different Taxonomy instance → fresh compilation)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Resolver_arbeitet_korrekt_mit_zwei_unterschiedlichen_taxonomies()
    {
        // Same resolver instance, two taxonomies — pinned via ConditionalWeakTable cache.
        var t1 = new Taxonomy { Genres = ["House"] };
        var t2 = new Taxonomy { Genres = ["Techno"] };

        var r1 = _resolver.Resolve([Cand("Vocal House", 0.9)], [], t1, null, null);
        var r2 = _resolver.Resolve([Cand("Vocal House", 0.9)], [], t2, null, null);

        r1.ProposedGenre.Should().Be("House");
        r2.ProposedGenre.Should().Be("Vocal House");   // Fallback, weil Techno nicht passt
    }

    // -----------------------------------------------------------------------------------------
    // Source-weighted ordering (B6.6) — TF aggregated > heuristic even at lower confidence
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Tf_aggregated_schlaegt_heuristik_trotz_niedrigerer_confidence()
    {
        // The M.ONDE - Miijetho case: heuristic emits Hip Hop with p=0.79 (BPM 95 lands in
        // its sweet spot), discogs-effnet aggregates Downtempo to p=0.38. Without source
        // weighting, pure confidence-sort would put Hip Hop first → Hip Hop wins.
        // With B6.6 source priority: TF-aggregated tier (80) beats heuristic tier (50)
        // regardless of raw confidence → Downtempo wins as intended.
        var taxonomy = new Taxonomy
        {
            Genres = ["House", "Hip Hop", "Downtempo"],
        };
        var candidates = new[]
        {
            new GenreCandidate("hip hop", 0.79, "classifier:heuristic"),
            new GenreCandidate("downtempo", 0.38, "classifier:essentia-tf-discogs-effnet:aggregated"),
        };

        var result = _resolver.Resolve(candidates, [], taxonomy, null, null);

        result.ProposedGenre.Should().Be("Downtempo",
            because: "TF-aggregated tier (80) outranks heuristic tier (50) before confidence comparison");
        result.MatchedCandidate!.Source.Should().Be("classifier:essentia-tf-discogs-effnet:aggregated");
    }

    [Fact]
    public void Provider_schlaegt_tf_aggregated_trotz_niedrigerer_confidence()
    {
        // Online provider tags (musicbrainz/discogs/lastfm) sit at priority 100 — they're
        // literal API responses with track-ID metadata, more authoritative than any ML guess.
        var taxonomy = new Taxonomy { Genres = ["House", "Downtempo"] };
        var candidates = new[]
        {
            new GenreCandidate("downtempo", 0.95, "classifier:essentia-tf-discogs-effnet:aggregated"),
            new GenreCandidate("house", 0.40, "musicbrainz"),
        };

        var result = _resolver.Resolve(candidates, [], taxonomy, null, null);

        result.ProposedGenre.Should().Be("House",
            because: "provider tier (100) outranks TF-aggregated tier (80)");
    }

    [Fact]
    public void Tf_aggregated_schlaegt_tf_aggregated_fallback()
    {
        // When two TF entries are present, :aggregated (tier 80) beats :aggregated-fallback
        // (tier 70). Lets the resolver prefer "model is confident" candidates over
        // "model is uncertain but here's the best guess" candidates.
        var taxonomy = new Taxonomy { Genres = ["House", "Downtempo"] };
        var candidates = new[]
        {
            new GenreCandidate("downtempo", 0.20, "classifier:essentia-tf-discogs-effnet:aggregated-fallback"),
            new GenreCandidate("house", 0.30, "classifier:essentia-tf-discogs-effnet:aggregated"),
        };

        var result = _resolver.Resolve(candidates, [], taxonomy, null, null);

        result.ProposedGenre.Should().Be("House");
    }

    [Fact]
    public void Innerhalb_gleicher_tier_entscheidet_confidence()
    {
        // Two heuristic candidates → both in tier 50 → confidence breaks the tie.
        var taxonomy = new Taxonomy { Genres = ["House", "Techno"] };
        var candidates = new[]
        {
            new GenreCandidate("house", 0.55, "classifier:heuristic"),
            new GenreCandidate("techno", 0.78, "classifier:heuristic"),
        };

        var result = _resolver.Resolve(candidates, [], taxonomy, null, null);

        result.ProposedGenre.Should().Be("Techno");
    }

    [Fact]
    public void Fallback_pfad_nutzt_sortierte_liste()
    {
        // No taxonomy match anywhere, slot is empty → fallback uses the sorted-list-top-1.
        // Should pick the provider candidate (tier 100), not the heuristic with higher p.
        var taxonomy = new Taxonomy { Genres = ["House"] };   // none of the candidates match
        var candidates = new[]
        {
            new GenreCandidate("gospel", 0.9, "classifier:heuristic"),
            new GenreCandidate("dancehall", 0.5, "musicbrainz"),
        };

        var result = _resolver.Resolve(candidates, [], taxonomy, null, null);

        result.FallbackApplied.Should().BeTrue();
        result.ProposedGenre.Should().Be("dancehall",
            because: "provider-tier candidate wins fallback even though heuristic confidence is higher");
        result.MatchedCandidate!.Source.Should().Be("musicbrainz");
    }

    [Theory]
    [InlineData("musicbrainz", 100)]
    [InlineData("discogs", 100)]
    [InlineData("acoustid", 100)]
    [InlineData("lastfm", 100)]
    [InlineData("classifier:essentia-tf-discogs-effnet:aggregated", 80)]
    [InlineData("classifier:essentia-tf-electronic:aggregated", 80)]
    [InlineData("classifier:essentia-tf-discogs-effnet:aggregated-fallback", 70)]
    [InlineData("classifier:essentia-tf-electronic", 60)]
    [InlineData("classifier:essentia-tf-jamendo", 60)]
    [InlineData("classifier:heuristic", 50)]
    [InlineData("classifier:future-unknown", 55)]
    [InlineData("", 70)]
    [InlineData("some-unknown-provider", 100)]
    public void Source_priority_table_ist_explizit_dokumentiert(string source, int expectedPriority)
    {
        // Documentation-as-test: any future refactor that changes the default priority values
        // trips this and forces a code-review discussion. See PLAN §4.0d.
        TaxonomyGenreResolver.SourcePriority(source, SourcePriorityOptions.Defaults).Should().Be(expectedPriority);
    }

    // -----------------------------------------------------------------------------------------
    // Source-priority CONFIGURABILITY (B6.6.1) — Design A: tier values tunable via tagger.yaml
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Custom_source_priority_kann_heuristik_ueber_tf_setzen()
    {
        // "Library experiments" scenario: user finds the heuristic more reliable than TF on
        // their corpus. Set heuristic > TF-aggregated. Now the order flips.
        var taxonomy = new Taxonomy { Genres = ["Hip Hop", "Downtempo"] };
        var candidates = new[]
        {
            new GenreCandidate("hip hop", 0.79, "classifier:heuristic"),
            new GenreCandidate("downtempo", 0.38, "classifier:essentia-tf-discogs-effnet:aggregated"),
        };
        var customOpts = new SourcePriorityOptions
        {
            // Heuristic now beats TF-aggregated; defaults are 50 vs 80.
            ClassifierHeuristic = 90,
            ClassifierAggregated = 60,
        };

        var result = _resolver.Resolve(candidates, [], taxonomy, null, null, customOpts);

        result.ProposedGenre.Should().Be("Hip Hop",
            because: "user policy puts heuristic tier (90) above TF-aggregated tier (60)");
    }

    [Fact]
    public void Source_priority_null_param_nutzt_defaults()
    {
        // Calling Resolve without the optional source_priority arg preserves the pre-B6.6.1
        // behaviour: defaults are baked in via SourcePriorityOptions.Defaults.
        var taxonomy = new Taxonomy { Genres = ["Hip Hop", "Downtempo"] };
        var candidates = new[]
        {
            new GenreCandidate("hip hop", 0.79, "classifier:heuristic"),
            new GenreCandidate("downtempo", 0.38, "classifier:essentia-tf-discogs-effnet:aggregated"),
        };

        var result = _resolver.Resolve(candidates, [], taxonomy, null, null);

        result.ProposedGenre.Should().Be("Downtempo",
            because: "default tiers put TF-aggregated (80) above heuristic (50)");
    }

    [Fact]
    public void Partial_priority_override_uses_defaults_for_unset_fields()
    {
        // YamlDotNet binding pattern: user sets only one tier; the others retain their
        // defaults. Resolver sees the partial-override config and uses defaults where
        // the user didn't specify.
        var partial = new SourcePriorityOptions { ClassifierHeuristic = 200 };

        partial.ClassifierHeuristic.Should().Be(200);
        partial.Provider.Should().Be(100, because: "untouched field keeps the default");
        partial.ClassifierAggregated.Should().Be(80);
        partial.ClassifierTfRaw.Should().Be(60);
    }
}
