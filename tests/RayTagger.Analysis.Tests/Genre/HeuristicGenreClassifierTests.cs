using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Analysis.Genre;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests.Genre;

public class HeuristicGenreClassifierTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    // Helper to build an EssentiaResult with named DSP signals. Defaults are mid-range so a
    // single override drives the test scenario cleanly.
    private static EssentiaResult ER(
        double? bpm = null,
        string? keyScale = null,
        double? chordsChangesRate = null,
        double? spectralCentroid = null,
        double? dynamicComplexity = null,
        double? danceability = null,
        double? beatsLoudness = null) =>
        new(Bpm: bpm, BpmConfidence: null,
            KeyKey: null, KeyScale: keyScale, KeyStrength: null,
            SpectralEnergy: null, AverageLoudness: null,
            SpectralFlux: null, OnsetRate: null,
            Danceability: danceability, BeatsLoudness: beatsLoudness,
            SpectralCentroidMean: spectralCentroid,
            SpectralComplexityMean: null,
            DynamicComplexity: dynamicComplexity,
            ChordsChangesRate: chordsChangesRate,
            ChordsStrengthMean: null);

    [Fact]
    public void House_archetype_scores_highest_for_house()
    {
        // House: ~122 BPM, minor key, mid chord-changes-rate, low-ish spectral centroid,
        // mid dynamic complexity, danceable, decent beats loudness.
        var er = ER(bpm: 122.0, keyScale: "minor",
            chordsChangesRate: 0.07, spectralCentroid: 2000.0,
            dynamicComplexity: 4.0, danceability: 1.5, beatsLoudness: 0.15);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("House");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public void Techno_archetype_scores_highest_for_techno()
    {
        // Techno: ~135 BPM, low chord-changes-rate (loop-based), high spectral centroid,
        // low dynamic complexity, danceable, very high beats loudness.
        var er = ER(bpm: 135.0, keyScale: "minor",
            chordsChangesRate: 0.02, spectralCentroid: 3200.0,
            dynamicComplexity: 2.5, danceability: 1.4, beatsLoudness: 0.20);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("Techno");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public void Trance_archetype_scores_highest_for_trance()
    {
        // Trance: ~138 BPM, major key, high chord-changes-rate (progression-based),
        // mid spectral centroid, high dynamic complexity, mid danceability, mid beats loudness.
        var er = ER(bpm: 138.0, keyScale: "major",
            chordsChangesRate: 0.14, spectralCentroid: 2500.0,
            dynamicComplexity: 6.0, danceability: 0.9, beatsLoudness: 0.10);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("Trance");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public void Returns_zero_score_when_all_features_null()
    {
        var er = ER();

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        scored.Should().HaveCount(8);
        foreach (var (_, confidence) in scored)
        {
            confidence.Should().Be(0.0);
        }
    }

    [Fact]
    public void Lineup_covers_canonical_taxonomy_genres()
    {
        var scored = HeuristicGenreClassifier.ScoreAll(ER());
        var genres = scored.Select(s => s.Genre).ToList();

        genres.Should().BeEquivalentTo(
        [
            "House",
            "Techno",
            "Trance",
            "Drum and Bass",
            "Dubstep",
            "Hip Hop",
            "Ambient",
            "Downtempo",
        ]);
    }

    [Fact]
    public void Skips_null_features_rather_than_zeroing_them()
    {
        // Only BPM present, everything else null. The score must be the BPM contribution alone
        // (not averaged with zeros).
        var er = ER(bpm: 122.0);

        var house = HeuristicGenreClassifier.ScoreAll(er)
            .First(s => s.Genre == "House");

        house.Confidence.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public async Task ClassifyAsync_returns_candidates_above_min_confidence()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(ER(bpm: 122.0, keyScale: "minor",
                chordsChangesRate: 0.07, spectralCentroid: 2000.0,
                dynamicComplexity: 4.0, danceability: 1.5, beatsLoudness: 0.15));

        var classifier = new HeuristicGenreClassifier(
            service,
            new HeuristicClassifierOptions { Enabled = true, MinConfidence = 0.55 },
            NullLogger<HeuristicGenreClassifier>.Instance);

        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().NotBeEmpty();
        result.Candidates[0].Value.Should().Be("house");           // normalised (lowercase)
        result.Candidates[0].Source.Should().Be("classifier:heuristic");
        result.Candidates[0].Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public async Task ClassifyAsync_returns_empty_when_service_returns_null()
    {
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns((EssentiaResult?)null);

        var classifier = new HeuristicGenreClassifier(
            service,
            new HeuristicClassifierOptions { Enabled = true },
            NullLogger<HeuristicGenreClassifier>.Instance);

        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ClassifyAsync_drops_candidates_below_min_confidence()
    {
        // All-features-null → all scores 0 → no candidate above the (default 0.55) floor.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(ER());

        var classifier = new HeuristicGenreClassifier(
            service,
            new HeuristicClassifierOptions { Enabled = true, MinConfidence = 0.55 },
            NullLogger<HeuristicGenreClassifier>.Instance);

        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ClassifyAsync_drops_every_candidate_at_boundary_min_confidence_one()
    {
        // No real-world score can reach 1.0 across all 7 features (per-feature peaks aren't
        // all 1.0). At MinConfidence=1.0 the classifier should emit nothing — defensive
        // verification that the floor comparison is strictly "below", not "below or equal".
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(ER(bpm: 122.0, keyScale: "minor",
                chordsChangesRate: 0.07, spectralCentroid: 2000.0,
                dynamicComplexity: 4.0, danceability: 1.5, beatsLoudness: 0.15));

        var classifier = new HeuristicGenreClassifier(
            service,
            new HeuristicClassifierOptions { Enabled = true, MinConfidence = 1.0 },
            NullLogger<HeuristicGenreClassifier>.Instance);

        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Scoring_uses_per_feature_average_not_sum()
    {
        // Sanity: with one feature present scoring at 1.0, the genre score must equal 1.0
        // (average over the only present feature). If the implementation switched to a sum or
        // weighted aggregate it would emit a different value — guard against accidental drift.
        var er = ER(bpm: 122.0);   // only BPM, everything else null

        var house = HeuristicGenreClassifier.ScoreAll(er).First(s => s.Genre == "House");

        house.Confidence.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task ClassifyAsync_sorts_candidates_by_descending_confidence()
    {
        // A House-archetype track still produces some non-zero Techno/Trance scores; verify
        // the candidate list is ordered correctly.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(ER(bpm: 122.0, keyScale: "minor",
                chordsChangesRate: 0.07, spectralCentroid: 2000.0,
                dynamicComplexity: 4.0, danceability: 1.5, beatsLoudness: 0.15));

        var classifier = new HeuristicGenreClassifier(
            service,
            new HeuristicClassifierOptions { Enabled = true, MinConfidence = 0.0 },
            NullLogger<HeuristicGenreClassifier>.Instance);

        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().HaveCount(8);
        for (var i = 1; i < result.Candidates.Count; i++)
        {
            result.Candidates[i - 1].Confidence.Should().BeGreaterThanOrEqualTo(
                result.Candidates[i].Confidence,
                because: "candidates must be sorted by descending confidence");
        }
    }

    [Fact]
    public void Drum_and_bass_archetype_scores_highest_for_dnb()
    {
        // 172 BPM, minor, low chord motion (loop-based with samples), bright spectrum,
        // moderately complex dynamics from the breakbeat, very danceable, slamming beats.
        var er = ER(bpm: 172.0, keyScale: "minor",
            chordsChangesRate: 0.05, spectralCentroid: 3100.0,
            dynamicComplexity: 5.5, danceability: 1.6, beatsLoudness: 0.22);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("Drum and Bass");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public void Dubstep_archetype_scores_highest_for_dubstep()
    {
        // 141 BPM, minor, near-zero chord motion (bass design only), bright spectrum,
        // crushing dynamics (drops), reasonably danceable, very loud beats.
        var er = ER(bpm: 141.0, keyScale: "minor",
            chordsChangesRate: 0.02, spectralCentroid: 3000.0,
            dynamicComplexity: 6.5, danceability: 1.0, beatsLoudness: 0.20);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("Dubstep");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public void Hip_hop_archetype_scores_highest_for_hip_hop()
    {
        // 90 BPM, minor, mid chord motion (sampled chops), warm spectrum, moderate dynamics,
        // mid danceability (groove without 4/4 club drive), mid beats loudness.
        var er = ER(bpm: 90.0, keyScale: "minor",
            chordsChangesRate: 0.06, spectralCentroid: 1900.0,
            dynamicComplexity: 4.0, danceability: 0.95, beatsLoudness: 0.11);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("Hip Hop");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public void Ambient_archetype_scores_highest_for_ambient()
    {
        // No driving tempo (drone material), neutral key, low chord motion, warm spectrum,
        // very low dynamic complexity (washy), very low danceability, very quiet beats.
        var er = ER(bpm: 80.0, keyScale: "minor",
            chordsChangesRate: 0.03, spectralCentroid: 1500.0,
            dynamicComplexity: 1.5, danceability: 0.4, beatsLoudness: 0.04);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("Ambient");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public void Downtempo_archetype_scores_highest_for_downtempo()
    {
        // Characteristic downtempo / trip-hop: 92 BPM minor, rich chord progression
        // (>0.10) — Downtempo's high-band sweet spot — even mid dynamics, reserved-but-not-zero
        // danceability (just above the 0.7 mid floor), soft Low-band beat (still rhythmic, not
        // ambient-quiet). Pulls clear of both Hip Hop (which scores high on mid chord motion)
        // and Ambient (which dominates when danceability + beats drop below their thresholds).
        var er = ER(bpm: 92.0, keyScale: "minor",
            chordsChangesRate: 0.13, spectralCentroid: 2100.0,
            dynamicComplexity: 3.5, danceability: 0.75, beatsLoudness: 0.09);

        var scored = HeuristicGenreClassifier.ScoreAll(er);

        var top = scored.OrderByDescending(s => s.Confidence).First();
        top.Genre.Should().Be("Downtempo");
        top.Confidence.Should().BeGreaterThan(0.55);
    }

    [Fact]
    public async Task Drum_and_bass_normalises_to_lowercase_space_separated_label()
    {
        // Confirms the §5.1a label normaliser produces "drum and bass" — the taxonomy entry
        // "Drum and Bass" matches case-insensitively in the resolver's whole-word regex.
        var service = Substitute.For<IEssentiaAnalysisService>();
        service.RunAsync(Arg.Any<TrackFile>(), Arg.Any<CancellationToken>())
            .Returns(ER(bpm: 172.0, keyScale: "minor",
                chordsChangesRate: 0.05, spectralCentroid: 3100.0,
                dynamicComplexity: 5.5, danceability: 1.6, beatsLoudness: 0.22));

        var classifier = new HeuristicGenreClassifier(
            service,
            new HeuristicClassifierOptions { Enabled = true, MinConfidence = 0.55 },
            NullLogger<HeuristicGenreClassifier>.Instance);

        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().NotBeEmpty();
        result.Candidates[0].Value.Should().Be("drum and bass");
    }
}
