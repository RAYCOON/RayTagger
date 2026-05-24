namespace RayTagger.Core.Validation;

/// <summary>
/// Aggregated backtest result for one run. Carries enough detail for both the JSON and Markdown
/// reporters: per-file rows plus per-dimension and per-genre rollups.
/// </summary>
public sealed record BacktestReport(
    DateTime GeneratedAt,
    string ReferenceRoot,
    IReadOnlyList<BacktestFileResult> Files,
    DimensionMetrics GenreMetrics,
    DimensionMetrics SubGenreMetrics,
    DimensionMetrics BpmMetrics,
    DimensionMetrics KeyMetrics,
    DimensionMetrics EnergyMetrics,
    IReadOnlyDictionary<string, GenreConfusion> GenreConfusion,
    TraceAggregate Trace,
    IReadOnlyDictionary<string, ClassifierThresholdSweep> ClassifierThresholds,
    IReadOnlyDictionary<string, BpmDistribution> BpmByGenre);

/// <summary>
/// One row per file in the reference library — every dimension's comparison plus the truth
/// snapshot used. <c>Truth</c> may have null fields when partial truth is available.
/// <c>GenreLookupTrace</c> is the resolver's per-candidate audit (null if resolver inactive).
/// <c>WinningGenreSource</c> is the <c>Source</c> of the candidate that supplied the final
/// genre (e.g. <c>"musicbrainz"</c>, <c>"classifier:essentia-tf-discogs-effnet:aggregated"</c>);
/// null when the genre came from the existing tag or from a mapping rule.
/// </summary>
public sealed record BacktestFileResult(
    BacktestTruth Truth,
    BacktestComparison Genre,
    BacktestComparison SubGenre,
    BacktestComparison Bpm,
    BacktestComparison Key,
    BacktestComparison Energy,
    string? Error,
    IReadOnlyList<Mapping.CandidateTraceEntry>? GenreLookupTrace = null,
    string? WinningGenreSource = null,
    IReadOnlyList<Pipeline.ClassifierTraceEntry>? ClassifierTrace = null)
{
    /// <summary>
    /// Which truth-root supplied the matching value for BPM. <see cref="TruthMatchSource.Primary"/>
    /// = matched against the MIK truth only, <see cref="TruthMatchSource.Secondary"/> = matched
    /// against VDJ (and not MIK), <see cref="TruthMatchSource.Both"/> = both agreed,
    /// <see cref="TruthMatchSource.None"/> = neither matched (or VDJ not provided). Always
    /// <see cref="TruthMatchSource.None"/> when <c>--reference-vdj</c> is not used.
    /// </summary>
    public TruthMatchSource BpmTruthMatch { get; init; } = TruthMatchSource.None;

    /// <summary>Which truth-root supplied the matching value for Key. See <see cref="BpmTruthMatch"/>.</summary>
    public TruthMatchSource KeyTruthMatch { get; init; } = TruthMatchSource.None;
}

/// <summary>
/// Records which of the two truth roots agreed with the pipeline prediction, used by the
/// validate harness when a secondary reference (e.g. Tagged_VDJ) is supplied. Primary truth =
/// MIK (the existing reference); secondary truth = VDJ. The aggregator surfaces the per-source
/// match counts so the reader can see how many tracks the VDJ truth "rescued" from a primary-only
/// mismatch (and vice versa).
/// </summary>
public enum TruthMatchSource
{
    /// <summary>No secondary truth available, or neither truth matched the prediction.</summary>
    None = 0,
    /// <summary>Only the primary truth (MIK) matched.</summary>
    Primary = 1,
    /// <summary>Only the secondary truth (VDJ) matched — the OR-rule rescue.</summary>
    Secondary = 2,
    /// <summary>Both truth roots agreed with the prediction.</summary>
    Both = 3,
}

/// <summary>
/// Cross-file aggregation of the per-track trace data — who-wins-when statistics for providers
/// and classifiers, plus how often the taxonomy resolver had to fall back vs. cleanly match.
/// </summary>
public sealed record TraceAggregate(
    IReadOnlyDictionary<string, SourceStats> BySource,
    int ResolverFallbackCount,
    int ResolverAggregatedFallbackCount,
    int ResolverCleanMatchCount);

/// <summary>
/// Win/loss counts for one candidate source (provider name like <c>"musicbrainz"</c>, or
/// classifier suffix like <c>"classifier:essentia-tf-discogs-effnet:aggregated"</c>). Wins =
/// the source supplied the candidate that the resolver applied AND the genre matched truth.
/// Losses = source supplied the winning candidate but the genre disagreed with truth.
/// </summary>
public sealed record SourceStats(string Source, int Wins, int Losses)
{
    public int Total => Wins + Losses;
    public double WinRate => Total == 0 ? 0 : (double)Wins / Total;
}

/// <summary>
/// Per-classifier threshold sweep — for each of a fixed set of <c>min_confidence</c> values,
/// precision/recall/F1 against the truth genre. Lets the backtest reader read off the optimal
/// <c>min_confidence</c> for each TF model directly from the report. <see cref="OptimalThreshold"/>
/// is the bucket that maximises F1; ties broken by higher threshold (more conservative is safer
/// when F1 is flat).
/// </summary>
public sealed record ClassifierThresholdSweep(
    string ClassifierName,
    IReadOnlyList<ThresholdBucket> Buckets,
    double? OptimalThreshold,
    double? OptimalF1);

/// <summary>
/// One row of a classifier's threshold sweep. The classifier's top-candidate-per-track is
/// considered a positive prediction iff its confidence is &gt;= <see cref="Threshold"/>; the
/// prediction matches truth iff the (whitespace-folded) candidate value equals truth genre.
/// </summary>
public sealed record ThresholdBucket(
    double Threshold,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    double Precision,
    double Recall,
    double F1);

/// <summary>
/// BPM distribution statistics for one genre subfolder — derived from genre-corrected
/// Mixed-In-Key BPM truth across every track in that folder. Used to surface realistic
/// <c>tempo_ranges_by_genre</c> values for <c>tagger.yaml</c>: the suggested range is
/// (<see cref="Percentile10"/>, <see cref="Percentile90"/>), which catches the typical
/// "felt-tempo" envelope while ignoring outliers.
/// </summary>
public sealed record BpmDistribution(
    string Genre,
    int Count,
    double Mean,
    double StandardDeviation,
    double Min,
    double Max,
    double Percentile10,
    double Percentile25,
    double Median,
    double Percentile75,
    double Percentile90);

/// <summary>
/// Rollup counts per outcome bucket for one dimension. Recall/precision/F1 are derived in the
/// reporter — the raw counts are what's persisted. <see cref="SourceCounts"/> records how many
/// predictions came from each pipeline stage (Analysis/Lookup/Rules/Existing) so the reader
/// can see whether the dimension is being genuinely analyzed or just passed through from disk.
/// </summary>
public sealed record DimensionMetrics(
    int Total,
    int Matches,
    int ToleranceMatches,
    int Mismatches,
    int NoPrediction,
    int NoTruth,
    IReadOnlyDictionary<Models.TagFieldSource, int> SourceCounts)
{
    /// <summary>
    /// Matches resolved by the primary truth only (MIK) when a secondary VDJ truth was also
    /// evaluated. Zero when <c>--reference-vdj</c> was not supplied.
    /// </summary>
    public int PrimaryOnlyMatches { get; init; }

    /// <summary>Matches resolved by the secondary truth only (VDJ) — the OR-rule rescue.</summary>
    public int SecondaryOnlyMatches { get; init; }

    /// <summary>Matches where both truth roots agreed with the prediction.</summary>
    public int BothMatches { get; init; }

    public static DimensionMetrics Empty { get; } = new(
        0, 0, 0, 0, 0, 0,
        new Dictionary<Models.TagFieldSource, int>());

    /// <summary>Tracks where truth was available — basis for recall/precision.</summary>
    public int Evaluable => Total - NoTruth;

    /// <summary>Fraction of evaluable tracks that matched exactly. 0 when no evaluables.</summary>
    public double ExactMatchRate => Evaluable == 0 ? 0 : (double)Matches / Evaluable;

    /// <summary>Fraction matching including tolerance. 0 when no evaluables.</summary>
    public double ToleranceMatchRate => Evaluable == 0 ? 0 : (double)(Matches + ToleranceMatches) / Evaluable;
}

/// <summary>
/// Per-truth-genre confusion stats: how many of the N tracks tagged as &lt;genre&gt; in the reference
/// did the pipeline classify correctly, mis-classify, or miss entirely. <see cref="Predicted"/>
/// records the distribution of pipeline predictions for this truth-genre's tracks.
/// </summary>
public sealed record GenreConfusion(
    string TruthGenre,
    int Total,
    int Matches,
    int Mismatches,
    int NoPrediction,
    IReadOnlyDictionary<string, int> Predicted);
