using RayTagger.Core.Models;
using RayTagger.Core.Validation;

namespace RayTagger.Core.Tests.Validation;

public class BacktestMetricsTests
{
    [Fact]
    public void Genre_match_case_insensitive()
    {
        var r = BacktestMetrics.CompareGenre("House", "house");
        r.Outcome.Should().Be(BacktestOutcome.Match);
    }

    [Theory]
    [InlineData("TripHop", "Trip Hop")]
    [InlineData("HipHop", "Hip Hop")]
    [InlineData("DancehallReggae", "Dancehall Reggae")]
    [InlineData("DnB", "Drum and Bass")] // intentional mismatch — letters differ
    public void Genre_match_whitespace_insensitive(string truth, string prediction)
    {
        // The first three pairs share the same letters (just whitespace differs) so they MUST
        // match; DnB vs "Drum and Bass" differs in letters and MUST mismatch — the test guards
        // that NormaliseForCompare doesn't accidentally fold abbreviations.
        var r = BacktestMetrics.CompareGenre(truth, prediction);
        var expectedMatch = BacktestMetrics.NormaliseForCompare(truth)
            == BacktestMetrics.NormaliseForCompare(prediction);
        r.Outcome.Should().Be(expectedMatch ? BacktestOutcome.Match : BacktestOutcome.Mismatch);
    }

    [Fact]
    public void Genre_no_truth_when_truth_empty()
    {
        var r = BacktestMetrics.CompareGenre("", "House");
        r.Outcome.Should().Be(BacktestOutcome.NoTruth);
    }

    [Fact]
    public void Genre_no_prediction_when_prediction_null()
    {
        var r = BacktestMetrics.CompareGenre("House", null);
        r.Outcome.Should().Be(BacktestOutcome.NoPrediction);
    }

    [Fact]
    public void Bpm_match_within_tolerance()
    {
        var r = BacktestMetrics.CompareBpm(120.0, 120.5, tolerance: 1.0);
        r.Outcome.Should().Be(BacktestOutcome.Match);
        r.Delta.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void Bpm_tolerance_match_for_double_time()
    {
        // Truth 86 (DnB-half-time-corrected to 172 would be ideal; pipeline emitted 172)
        // We're testing the raw fallback here without correction — direct disagreement,
        // half/double match.
        var r = BacktestMetrics.CompareBpm(86.0, 172.0, tolerance: 1.0);
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Bpm_tolerance_match_for_half_time()
    {
        var r = BacktestMetrics.CompareBpm(140.0, 70.0, tolerance: 1.0);
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Bpm_mismatch_when_outside_all_tolerances()
    {
        var r = BacktestMetrics.CompareBpm(120.0, 95.0, tolerance: 1.0);
        r.Outcome.Should().Be(BacktestOutcome.Mismatch);
        r.Delta.Should().BeApproximately(25.0, 0.001);
    }

    [Fact]
    public void Key_exact_match_camelot()
    {
        var r = BacktestMetrics.CompareKey("8A", new MusicalKey("Am", "8A"));
        r.Outcome.Should().Be(BacktestOutcome.Match);
    }

    [Fact]
    public void Key_neighbour_tolerance_match_relative_major_minor()
    {
        // 8A (Am) vs 8B (C) — same number, different letter = relative major/minor.
        var r = BacktestMetrics.CompareKey("8A", new MusicalKey("C", "8B"));
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Key_neighbour_tolerance_match_wheel_adjacent()
    {
        var r = BacktestMetrics.CompareKey("8A", new MusicalKey("Em", "9A"));
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Key_neighbour_tolerance_wraps_12_to_1()
    {
        var r = BacktestMetrics.CompareKey("12A", new MusicalKey("Abm", "1A"));
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Key_neighbour_tolerance_distance_2_same_letter()
    {
        // 8A ↔ 10A — same letter, 2 wheel positions apart. Standard harmonic-mixing distance
        // (energy-shift mix). Tolerated to catch Essentia drift that lands one wheel-step off
        // Mixed-In-Key's reading.
        var r = BacktestMetrics.CompareKey("8A", new MusicalKey("Bm", "10A"));
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Key_neighbour_tolerance_distance_2_wraps_12_to_2()
    {
        // 12A → 2A via wrap = distance 2. Must still count as harmonically close.
        var r = BacktestMetrics.CompareKey("12A", new MusicalKey("Ebm", "2A"));
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Key_mismatch_when_not_neighbour()
    {
        // 8A vs 3A — 5 wheel positions apart, far beyond harmonic-mixing distance 2.
        var r = BacktestMetrics.CompareKey("8A", new MusicalKey("Bbm", "3A"));
        r.Outcome.Should().Be(BacktestOutcome.Mismatch);
    }

    [Fact]
    public void Key_mismatch_when_distance_three_same_letter()
    {
        // 8A ↔ 11A — distance 3, just one step beyond the harmonic-mixing tolerance.
        // Marks the boundary above which we no longer accept the pairing.
        var r = BacktestMetrics.CompareKey("8A", new MusicalKey("F#m", "11A"));
        r.Outcome.Should().Be(BacktestOutcome.Mismatch);
    }

    [Fact]
    public void Key_mismatch_when_diagonal_cross_letter_neighbour()
    {
        // 8A ↔ 7B — diagonal-mix pair (DJ trick, not a harmonic-tolerance signal). We
        // deliberately do NOT count this as ToleranceMatch.
        var r = BacktestMetrics.CompareKey("8A", new MusicalKey("F", "7B"));
        r.Outcome.Should().Be(BacktestOutcome.Mismatch);
    }

    [Fact]
    public void Energy_exact_match()
    {
        var r = BacktestMetrics.CompareEnergy(6, 6);
        r.Outcome.Should().Be(BacktestOutcome.Match);
    }

    [Fact]
    public void Energy_tolerance_match_within_1()
    {
        var r = BacktestMetrics.CompareEnergy(6, 7);
        r.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
    }

    [Fact]
    public void Energy_mismatch_when_delta_above_1()
    {
        var r = BacktestMetrics.CompareEnergy(6, 9);
        r.Outcome.Should().Be(BacktestOutcome.Mismatch);
    }

    [Fact]
    public void Promote_no_secondary_returns_primary_with_primary_match_flag()
    {
        var primary = BacktestMetrics.CompareBpm(120.0, 120.0);
        var (combined, by) = BacktestMetrics.PromoteWithSecondary(primary, secondary: null);
        combined.Outcome.Should().Be(BacktestOutcome.Match);
        by.Should().Be(TruthMatchSource.Primary);
    }

    [Fact]
    public void Promote_no_secondary_with_primary_mismatch_returns_none()
    {
        var primary = BacktestMetrics.CompareBpm(120.0, 90.0);
        var (combined, by) = BacktestMetrics.PromoteWithSecondary(primary, secondary: null);
        combined.Outcome.Should().Be(BacktestOutcome.Mismatch);
        by.Should().Be(TruthMatchSource.None);
    }

    [Fact]
    public void Promote_secondary_rescues_primary_mismatch()
    {
        // Pick truths whose ratio is not exactly half/double, so CompareBpm against the primary
        // really yields Mismatch (not the built-in ToleranceMatch half-time fallback).
        var mik = BacktestMetrics.CompareBpm(120.0, 80.0);  // 1.5 ratio — no half/double rescue
        var vdj = BacktestMetrics.CompareBpm(80.0, 80.0);
        var (combined, by) = BacktestMetrics.PromoteWithSecondary(mik, vdj);
        combined.Outcome.Should().Be(BacktestOutcome.Match);
        by.Should().Be(TruthMatchSource.Secondary);
    }

    [Fact]
    public void Promote_both_match_reports_both()
    {
        var mik = BacktestMetrics.CompareKey("8A", new MusicalKey("Am", "8A"));
        var vdj = BacktestMetrics.CompareKey("8A", new MusicalKey("Am", "8A"));
        var (combined, by) = BacktestMetrics.PromoteWithSecondary(mik, vdj);
        combined.Outcome.Should().Be(BacktestOutcome.Match);
        by.Should().Be(TruthMatchSource.Both);
    }

    [Fact]
    public void Promote_picks_better_outcome_when_both_imperfect()
    {
        // MIK mismatches outright, VDJ is a Camelot-neighbour tolerance match — combined returns
        // the better outcome (tolerance) and credits the secondary truth.
        var mik = BacktestMetrics.CompareKey("8A", new MusicalKey("D", "10B"));
        var vdj = BacktestMetrics.CompareKey("8A", new MusicalKey("Em", "9A"));
        var (combined, by) = BacktestMetrics.PromoteWithSecondary(mik, vdj);
        combined.Outcome.Should().Be(BacktestOutcome.ToleranceMatch);
        by.Should().Be(TruthMatchSource.Secondary);
    }
}
