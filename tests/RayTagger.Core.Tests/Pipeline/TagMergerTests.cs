using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Core.Tests.Pipeline;

public class TagMergerTests
{
    // -------------------------------------------------------------------------------------
    // Default-confidence behaviour (existing_confidence = 1.0)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Analysis_fills_empty_existing_slot()
    {
        var existing = new TrackTags(Genre: "House");  // no BPM
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.9);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig());

        resolved.Bpm.Value.Should().Be(128);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Analysis);
    }

    [Fact]
    public void Existing_wins_when_present_under_default_existing_confidence()
    {
        // existing_confidence defaults to 1.0 → analyzer confidence ≤ 1.0 always loses.
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.9);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig());

        resolved.Bpm.Value.Should().Be(120);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void Low_confidence_analysis_is_rejected_even_when_existing_is_empty()
    {
        var existing = new TrackTags();  // no BPM
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.2);  // below default 0.6

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig());

        resolved.Bpm.Value.Should().BeNull();
        resolved.Bpm.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void Genre_passes_through_as_existing_without_lookup_or_analyzer()
    {
        // No analyzer + no lookup → existing genre flows through unchanged.
        var existing = new TrackTags(Genre: "Tech House");
        var analysis = AnalysisResult.Empty;

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig());

        resolved.Genre.Value.Should().Be("Tech House");
        resolved.Genre.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void Key_with_high_confidence_replaces_missing_existing_key()
    {
        var existing = new TrackTags();
        var analysis = MakeAnalysis(key: new MusicalKey("Am", "8A"), keyConfidence: 0.9);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig());

        resolved.Key.Value.Should().NotBeNull();
        resolved.Key.Value!.Camelot.Should().Be("8A");
        resolved.Key.Source.Should().Be(TagFieldSource.Analysis);
    }

    // -------------------------------------------------------------------------------------
    // Per-dimension existing_confidence — confidence-weighted overwrite control
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ExistingConfidence_zero_lets_usable_analyzer_overwrite_existing()
    {
        // existing_confidence=0 ≡ per-dimension always-overwrite for that dimension.
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.9);
        var config = DefaultAnalysisConfig();
        config.Bpm.ExistingConfidence = 0.0;

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, config);

        resolved.Bpm.Value.Should().Be(128);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Analysis);
    }

    [Fact]
    public void ExistingConfidence_one_reproduces_classic_skip_if_present()
    {
        // Default existing_confidence=1.0 must keep historical behaviour: existing wins.
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.95);
        var config = DefaultAnalysisConfig();
        config.Bpm.ExistingConfidence.Should().Be(1.0);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, config);

        resolved.Bpm.Value.Should().Be(120);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void ExistingConfidence_mid_keeps_existing_when_analyzer_below_threshold()
    {
        // existing_confidence=0.7, analyzer confidence=0.65 → existing wins.
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.65);
        var config = DefaultAnalysisConfig();
        config.Bpm.MinConfidence = 0.0;
        config.Bpm.ExistingConfidence = 0.7;

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, config);

        resolved.Bpm.Value.Should().Be(120);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void ExistingConfidence_mid_lets_analyzer_overwrite_when_above_threshold()
    {
        // existing_confidence=0.5, analyzer confidence=0.8 → analyzer wins.
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.8);
        var config = DefaultAnalysisConfig();
        config.Bpm.MinConfidence = 0.0;
        config.Bpm.ExistingConfidence = 0.5;

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, config);

        resolved.Bpm.Value.Should().Be(128);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Analysis);
    }

    [Fact]
    public void ExistingConfidence_zero_still_respects_min_confidence_floor()
    {
        // Even with existing_confidence=0, an analyzer below min_confidence is rejected — we
        // never push garbage onto the file just because the existing slot was permissive.
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.1);
        var config = DefaultAnalysisConfig();
        config.Bpm.MinConfidence = 0.4;
        config.Bpm.ExistingConfidence = 0.0;

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, config);

        resolved.Bpm.Value.Should().Be(120);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void ExistingConfidence_zero_for_reference_field_lets_analyzer_overwrite_key()
    {
        // Same semantics for reference-type fields (Key, Genre, ...).
        var existing = new TrackTags(Key: new MusicalKey("Am", "8A"));
        var analysis = MakeAnalysis(key: new MusicalKey("Em", "9A"), keyConfidence: 0.9);
        var config = DefaultAnalysisConfig();
        config.Key.ExistingConfidence = 0.0;

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, config);

        resolved.Key.Value!.Camelot.Should().Be("9A");
        resolved.Key.Source.Should().Be(TagFieldSource.Analysis);
    }

    [Fact]
    public void ExistingConfidence_per_dimension_BPM_zero_Key_one_mixed_outcome()
    {
        // The whole point: per-dimension independence. BPM can be permissive while Key stays
        // protected — the historic global policy could only flip both at once.
        var existing = new TrackTags(Bpm: 120, Key: new MusicalKey("Am", "8A"));
        var analysis = MakeAnalysis(
            bpm: 128, bpmConfidence: 0.9,
            key: new MusicalKey("Em", "9A"), keyConfidence: 0.9);
        var config = DefaultAnalysisConfig();
        config.Bpm.ExistingConfidence = 0.0;        // BPM: analyzer wins
        config.Key.ExistingConfidence = 1.0;        // Key: existing wins (default)

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, config);

        resolved.Bpm.Value.Should().Be(128);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Analysis);
        resolved.Key.Value!.Camelot.Should().Be("8A");
        resolved.Key.Source.Should().Be(TagFieldSource.Existing);
    }

    // -------------------------------------------------------------------------------------
    // Lookup-path existing_confidence (legacy path — resolver disabled)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void LookupExistingConfidence_zero_lets_lookup_overwrite_existing_genre()
    {
        // No resolver → MergeLookupString path. existing_confidence=0 means any usable lookup
        // candidate (confidence ≥ LegacyLookupMinConfidence) wins over existing.
        var existing = new TrackTags(Genre: "Pop");
        var lookup = new LookupResult(
            GenreCandidates: [new GenreCandidate("Tech House", 0.5, "musicbrainz")],
            SubGenreCandidates: []);

        var resolved = TagMerger.Merge(
            existing, AnalysisResult.Empty, lookup,
            DefaultAnalysisConfig(),
            taxonomy: null, resolver: null,
            lookupExistingConfidence: 0.0);

        resolved.Genre.Value.Should().Be("Tech House");
        resolved.Genre.Source.Should().Be(TagFieldSource.Lookup);
    }

    [Fact]
    public void LookupExistingConfidence_one_reproduces_classic_existing_protection()
    {
        var existing = new TrackTags(Genre: "Pop");
        var lookup = new LookupResult(
            GenreCandidates: [new GenreCandidate("Tech House", 0.95, "musicbrainz")],
            SubGenreCandidates: []);

        var resolved = TagMerger.Merge(
            existing, AnalysisResult.Empty, lookup,
            DefaultAnalysisConfig(),
            taxonomy: null, resolver: null,
            lookupExistingConfidence: 1.0);

        resolved.Genre.Value.Should().Be("Pop");
        resolved.Genre.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void LookupExistingConfidence_mid_keeps_existing_when_candidate_below_threshold()
    {
        var existing = new TrackTags(Genre: "Pop");
        var lookup = new LookupResult(
            GenreCandidates: [new GenreCandidate("Tech House", 0.55, "musicbrainz")],
            SubGenreCandidates: []);

        var resolved = TagMerger.Merge(
            existing, AnalysisResult.Empty, lookup,
            DefaultAnalysisConfig(),
            taxonomy: null, resolver: null,
            lookupExistingConfidence: 0.7);

        resolved.Genre.Value.Should().Be("Pop");
        resolved.Genre.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void LookupExistingConfidence_mid_lets_strong_candidate_overwrite()
    {
        var existing = new TrackTags(Genre: "Pop");
        var lookup = new LookupResult(
            GenreCandidates: [new GenreCandidate("Tech House", 0.9, "musicbrainz")],
            SubGenreCandidates: []);

        var resolved = TagMerger.Merge(
            existing, AnalysisResult.Empty, lookup,
            DefaultAnalysisConfig(),
            taxonomy: null, resolver: null,
            lookupExistingConfidence: 0.7);

        resolved.Genre.Value.Should().Be("Tech House");
        resolved.Genre.Source.Should().Be(TagFieldSource.Lookup);
    }

    [Fact]
    public void LookupExistingConfidence_zero_still_respects_LegacyLookupMinConfidence()
    {
        // Even with existing_confidence=0, a candidate below LegacyLookupMinConfidence (0.30)
        // is rejected — we never push noise like LastFm "favourite" tags onto the file.
        var existing = new TrackTags(Genre: "Pop");
        var lookup = new LookupResult(
            GenreCandidates: [new GenreCandidate("favourite", 0.05, "lastfm")],
            SubGenreCandidates: []);

        var resolved = TagMerger.Merge(
            existing, AnalysisResult.Empty, lookup,
            DefaultAnalysisConfig(),
            taxonomy: null, resolver: null,
            lookupExistingConfidence: 0.0);

        resolved.Genre.Value.Should().Be("Pop");
        resolved.Genre.Source.Should().Be(TagFieldSource.Existing);
    }

    private static AnalysisOptions DefaultAnalysisConfig() => new()
    {
        Bpm = new BpmAnalyzerOptions { Provider = "aubio", MinConfidence = 0.6 },
        Key = new KeyAnalyzerOptions { Provider = "keyfinder-cli", MinConfidence = 0.55 },
        Energy = new EnergyAnalyzerOptions { Provider = "essentia", MinConfidence = 0.5 },
        Fingerprint = new AnalyzerOptions { Provider = "chromaprint", MinConfidence = 0.0 },
    };

    private static AnalysisResult MakeAnalysis(
        double? bpm = null,
        double bpmConfidence = 0,
        MusicalKey? key = null,
        double keyConfidence = 0,
        int? energy = null,
        double energyConfidence = 0) =>
        new(
            new BpmResult(bpm, bpmConfidence),
            new KeyResult(key, keyConfidence),
            new EnergyResult(energy, energyConfidence),
            new FingerprintResult(null, 0),
            AcoustIdMbid: null);
}
