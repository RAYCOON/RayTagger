using RayTagger.Core.Models;

namespace RayTagger.Core.Validation;

/// <summary>
/// Per-dimension comparison helpers between pipeline output and Mixed-In-Key truth.
/// Each method returns a <see cref="BacktestComparison"/> with the raw outcome plus enough
/// detail for the reporter to surface deltas (e.g. BPM mean |Δ|).
/// </summary>
public static class BacktestMetrics
{
    /// <summary>
    /// Genre match — case- AND whitespace-insensitive string compare. Whitespace is collapsed
    /// because subfolder-truth uses joined-word convention ("TripHop", "HipHop", "DancehallReggae")
    /// while the pipeline writes the spaced canonical form ("Trip Hop", "Hip Hop", "Dancehall
    /// Reggae"). Without this normalisation every spaced-name genre would count as mismatch even
    /// when the pipeline picked the correct canonical name.
    /// </summary>
    public static BacktestComparison CompareGenre(string? truth, string? prediction, Models.TagFieldSource? source = null)
    {
        if (string.IsNullOrWhiteSpace(truth)) return BacktestComparison.NoTruth;
        if (string.IsNullOrWhiteSpace(prediction)) return new BacktestComparison(BacktestOutcome.NoPrediction, truth, null);
        var match = NormaliseForCompare(truth) == NormaliseForCompare(prediction);
        return new BacktestComparison(
            match ? BacktestOutcome.Match : BacktestOutcome.Mismatch,
            truth, prediction) with
        { Source = source };
    }

    /// <summary>
    /// Folds a genre/subgenre label to its canonical compare form: lowercase, whitespace +
    /// hyphens stripped. "Trip Hop", "TripHop" and "trip-hop" all hash to "triphop". Public so
    /// the validate-report renderer can use the same folding as <see cref="CompareGenre"/> when
    /// filtering predictions for the confusion-matrix "top mispredictions" column.
    /// </summary>
    public static string NormaliseForCompare(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '_') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Subgenre match — same semantics as <see cref="CompareGenre"/>. Subgenre truth is only
    /// available when the file sat in a sub-subfolder; otherwise truth is null and the metric
    /// is skipped (NoTruth).
    /// </summary>
    public static BacktestComparison CompareSubGenre(string? truth, string? prediction, Models.TagFieldSource? source = null) =>
        CompareGenre(truth, prediction, source);

    /// <summary>
    /// BPM match within tolerance, with half/double-time fallback. Tolerance defaults to
    /// <paramref name="tolerance"/> BPM (e.g. 1.0). The half/double fallback catches cases where
    /// the pipeline emitted the doubled or halved rate vs MIK truth even after MIK's own
    /// genre-correction (some tracks slip through).
    /// </summary>
    public static BacktestComparison CompareBpm(double? truth, double? prediction, Models.TagFieldSource? source = null, double tolerance = 1.0)
    {
        if (truth is null) return BacktestComparison.NoTruth;
        if (prediction is null) return new BacktestComparison(BacktestOutcome.NoPrediction, truth, null);

        var t = truth.Value;
        var p = prediction.Value;

        var direct = Math.Abs(p - t);
        var doubled = Math.Abs(p - 2 * t);
        var halved = Math.Abs(p - t / 2);
        var minDelta = Math.Min(direct, Math.Min(doubled, halved));

        if (direct <= tolerance)
        {
            return new BacktestComparison(BacktestOutcome.Match, t, p) with { Delta = direct, Source = source };
        }
        if (doubled <= tolerance || halved <= tolerance)
        {
            // Half/double match — Tagger and MIK agreed on rhythm but disagree on rate convention.
            return new BacktestComparison(BacktestOutcome.ToleranceMatch, t, p) with { Delta = minDelta, Source = source };
        }
        return new BacktestComparison(BacktestOutcome.Mismatch, t, p) with { Delta = direct, Source = source };
    }

    /// <summary>
    /// Key match — exact Camelot match, with optional ±1 wheel-position neighbour tolerance.
    /// The truth-side notation is Camelot. The prediction-side <see cref="MusicalKey"/> carries
    /// both notations; we compare against its Camelot form.
    /// </summary>
    public static BacktestComparison CompareKey(string? truthCamelot, MusicalKey? prediction, Models.TagFieldSource? source = null)
    {
        if (string.IsNullOrWhiteSpace(truthCamelot)) return BacktestComparison.NoTruth;
        if (prediction is null) return new BacktestComparison(BacktestOutcome.NoPrediction, truthCamelot, null);

        var t = truthCamelot.ToUpperInvariant();
        var p = prediction.Camelot.ToUpperInvariant();

        if (string.Equals(t, p, StringComparison.Ordinal))
        {
            return new BacktestComparison(BacktestOutcome.Match, t, p) with { Source = source };
        }

        if (IsCamelotNeighbour(t, p))
        {
            return new BacktestComparison(BacktestOutcome.ToleranceMatch, t, p) with { Source = source };
        }
        return new BacktestComparison(BacktestOutcome.Mismatch, t, p) with { Source = source };
    }

    /// <summary>
    /// Merges a primary and an optional secondary <see cref="BacktestComparison"/> with OR-logic:
    /// the result carries the BEST outcome of the two (Match &gt; ToleranceMatch &gt; Mismatch &gt;
    /// NoPrediction &gt; NoTruth) and a <see cref="TruthMatchSource"/> flag indicating which side
    /// (or both) actually matched the prediction. Used by the validate harness when a secondary
    /// truth root (e.g. Tagged_VDJ) is supplied so a track is counted as "match" if either truth
    /// agrees with the pipeline output.
    /// </summary>
    public static (BacktestComparison Combined, TruthMatchSource MatchedBy) PromoteWithSecondary(
        BacktestComparison primary, BacktestComparison? secondary)
    {
        ArgumentNullException.ThrowIfNull(primary);

        if (secondary is null)
        {
            var matched = IsMatchedOutcome(primary.Outcome) ? TruthMatchSource.Primary : TruthMatchSource.None;
            return (primary, matched);
        }

        var pRank = OutcomeRank(primary.Outcome);
        var sRank = OutcomeRank(secondary.Outcome);
        var combined = sRank > pRank ? secondary : primary;

        var pMatched = IsMatchedOutcome(primary.Outcome);
        var sMatched = IsMatchedOutcome(secondary.Outcome);
        var by = (pMatched, sMatched) switch
        {
            (true, true) => TruthMatchSource.Both,
            (true, false) => TruthMatchSource.Primary,
            (false, true) => TruthMatchSource.Secondary,
            _ => TruthMatchSource.None,
        };
        return (combined, by);
    }

    private static int OutcomeRank(BacktestOutcome outcome) => outcome switch
    {
        BacktestOutcome.Match => 4,
        BacktestOutcome.ToleranceMatch => 3,
        BacktestOutcome.Mismatch => 2,
        BacktestOutcome.NoPrediction => 1,
        _ => 0,
    };

    private static bool IsMatchedOutcome(BacktestOutcome outcome) =>
        outcome is BacktestOutcome.Match or BacktestOutcome.ToleranceMatch;

    /// <summary>
    /// Energy match within ±1 bucket — truth comes from MIK comment, prediction from the pipeline.
    /// Used for diagnostic reporting only; energy tuning is out-of-scope.
    /// </summary>
    public static BacktestComparison CompareEnergy(int? truth, int? prediction, Models.TagFieldSource? source = null)
    {
        if (truth is null) return BacktestComparison.NoTruth;
        if (prediction is null) return new BacktestComparison(BacktestOutcome.NoPrediction, truth, null);

        var delta = Math.Abs(truth.Value - prediction.Value);
        if (delta == 0) return new BacktestComparison(BacktestOutcome.Match, truth, prediction) with { Source = source };
        if (delta == 1) return new BacktestComparison(BacktestOutcome.ToleranceMatch, truth, prediction) with { Source = source };
        return new BacktestComparison(BacktestOutcome.Mismatch, truth, prediction) with { Source = source };
    }

    /// <summary>
    /// True when two Camelot codes sit within standard harmonic-mixing distance — the rule of
    /// thumb a DJ would accept as "still in key" when blending two tracks:
    /// <list type="bullet">
    ///   <item><b>Same number, different letter</b> (e.g. 8A ↔ 8B) — relative major/minor.</item>
    ///   <item><b>Same letter, ±1 or ±2 position</b> on the wheel (12 wraps to 1) — energy-same /
    ///   mild energy shift mix. Distance 1 covers classic harmonic-mixing pairs (8A ↔ 7A / 9A);
    ///   distance 2 catches the analyzer-drift case where Essentia lands one wheel-step off
    ///   from Mixed-In-Key's reading, which still sounds harmonically compatible.</item>
    /// </list>
    /// Diagonals (cross-letter ±1, e.g. 8A → 7B) and the "Energy Boost" (+7) are deliberately
    /// out of scope — they're DJ-mixing tricks rather than analyzer-tolerance signal.
    /// </summary>
    internal static bool IsCamelotNeighbour(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return false;
        var letterA = a[^1];
        var letterB = b[^1];
        if (!int.TryParse(a[..^1], out var numA)) return false;
        if (!int.TryParse(b[..^1], out var numB)) return false;

        // Relative major/minor — same number, different letter.
        if (numA == numB && letterA != letterB) return true;
        // Same letter — wheel-adjacent positions within distance ≤ 2 (12-wheel wraps both ways).
        // diff 1 = direct neighbour, diff 2 = two steps, diff 10 = two-step wrap (12→2),
        // diff 11 = one-step wrap (12→1).
        if (letterA != letterB) return false;
        var diff = Math.Abs(numA - numB);
        return diff is 1 or 2 or 10 or 11;
    }
}

/// <summary>
/// Outcome of a single dimension's comparison.
/// </summary>
public enum BacktestOutcome
{
    /// <summary>No truth available for this dimension (e.g. comment unparseable, no subgenre folder).</summary>
    NoTruth,
    /// <summary>Truth was available but the pipeline produced nothing.</summary>
    NoPrediction,
    /// <summary>Truth and prediction matched exactly (within strict tolerance for BPM).</summary>
    Match,
    /// <summary>Truth and prediction were close but not exact — within wider tolerance (e.g. half/double BPM, Camelot neighbour).</summary>
    ToleranceMatch,
    /// <summary>Truth and prediction disagreed beyond any tolerance.</summary>
    Mismatch,
}

/// <summary>
/// One row of the comparison table. <see cref="Delta"/> is populated for numeric dimensions only.
/// <see cref="Source"/> records which pipeline stage produced the prediction — null when there
/// was no prediction or when truth was unavailable.
/// </summary>
public sealed record BacktestComparison(BacktestOutcome Outcome, object? Truth, object? Prediction)
{
    public double? Delta { get; init; }
    public Models.TagFieldSource? Source { get; init; }

    public static BacktestComparison NoTruth { get; } = new(BacktestOutcome.NoTruth, null, null);
}
