using RayTagger.Core.Models;

namespace RayTagger.Core.Validation;

/// <summary>
/// Folds per-file comparison results into the aggregated <see cref="BacktestReport"/>. Pure
/// computation — no IO, no logging. Used by the CLI handler after it has run the pipeline against
/// every file in the reference library.
/// </summary>
public static class BacktestReportAggregator
{
    public static BacktestReport Build(
        string referenceRoot,
        IReadOnlyList<BacktestFileResult> files,
        DateTime generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceRoot);
        ArgumentNullException.ThrowIfNull(files);

        var genreMetrics = SumOutcomes(files, f => f.Genre);
        var subGenreMetrics = SumOutcomes(files, f => f.SubGenre);
        var bpmMetrics = SumOutcomes(files, f => f.Bpm, f => f.BpmTruthMatch);
        var keyMetrics = SumOutcomes(files, f => f.Key, f => f.KeyTruthMatch);
        var energyMetrics = SumOutcomes(files, f => f.Energy);
        var confusion = BuildGenreConfusion(files);
        var trace = BuildTraceAggregate(files);
        var thresholds = BuildClassifierThresholdSweeps(files);
        var bpmByGenre = BuildBpmDistributions(files);

        return new BacktestReport(
            GeneratedAt: generatedAt,
            ReferenceRoot: referenceRoot,
            Files: files,
            GenreMetrics: genreMetrics,
            SubGenreMetrics: subGenreMetrics,
            BpmMetrics: bpmMetrics,
            KeyMetrics: keyMetrics,
            EnergyMetrics: energyMetrics,
            GenreConfusion: confusion,
            Trace: trace,
            ClassifierThresholds: thresholds,
            BpmByGenre: bpmByGenre);
    }

    /// <summary>
    /// Per-genre BPM-distribution statistics from the genre-corrected Mixed-In-Key truth. The
    /// genre key is the truth subfolder name (case-insensitive). Tracks without a truth BPM or
    /// truth genre are excluded — they can't contribute to a distribution they don't belong to.
    /// </summary>
    private static Dictionary<string, BpmDistribution> BuildBpmDistributions(
        IReadOnlyList<BacktestFileResult> files)
    {
        var samples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.Truth.Genre)) continue;
            if (f.Truth.Bpm is not double bpm) continue;
            if (!samples.TryGetValue(f.Truth.Genre, out var list))
            {
                list = [];
                samples[f.Truth.Genre] = list;
            }
            list.Add(bpm);
        }

        var result = new Dictionary<string, BpmDistribution>(StringComparer.OrdinalIgnoreCase);
        foreach (var (genre, list) in samples)
        {
            list.Sort();
            var mean = list.Average();
            var variance = list.Sum(v => (v - mean) * (v - mean)) / list.Count;
            result[genre] = new BpmDistribution(
                Genre: genre,
                Count: list.Count,
                Mean: mean,
                StandardDeviation: Math.Sqrt(variance),
                Min: list[0],
                Max: list[^1],
                Percentile10: Percentile(list, 0.10),
                Percentile25: Percentile(list, 0.25),
                Median: Percentile(list, 0.50),
                Percentile75: Percentile(list, 0.75),
                Percentile90: Percentile(list, 0.90));
        }
        return result;
    }

    /// <summary>
    /// Linear-interpolation percentile over a pre-sorted list. NIST type-7 — matches numpy's
    /// default and is the convention almost every plotting library agrees on.
    /// </summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        var fraction = rank - lo;
        return sorted[lo] + fraction * (sorted[hi] - sorted[lo]);
    }

    /// <summary>
    /// Threshold sweep at 5 % granularity from 0.05 to 0.95 for each classifier. Used to read
    /// off the optimal <c>min_confidence</c> per TF model from the backtest report.
    /// </summary>
    private static readonly double[] SweepThresholds =
        [.. Enumerable.Range(1, 19).Select(i => i * 0.05)];

    private static Dictionary<string, ClassifierThresholdSweep> BuildClassifierThresholdSweeps(
        IReadOnlyList<BacktestFileResult> files)
    {
        // Bucket each file's top-candidate-per-classifier by classifier name. A track is only
        // a positive example when truth is available; tracks without truth are skipped from both
        // numerator and denominator (they can't be true-positive or false-negative).
        var byClassifier = new Dictionary<string, List<(string TruthGenre, GenreCandidate? Top)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.Truth.Genre)) continue;
            if (f.ClassifierTrace is null) continue;

            foreach (var entry in f.ClassifierTrace)
            {
                if (entry.Status != Pipeline.ClassifierTraceStatus.Ok || entry.Candidates.Count == 0)
                {
                    if (!byClassifier.TryGetValue(entry.ClassifierName, out var emptyList))
                    {
                        emptyList = [];
                        byClassifier[entry.ClassifierName] = emptyList;
                    }
                    emptyList.Add((f.Truth.Genre, null));
                    continue;
                }
                if (!byClassifier.TryGetValue(entry.ClassifierName, out var list))
                {
                    list = [];
                    byClassifier[entry.ClassifierName] = list;
                }
                // Top candidate = highest confidence. Classifier output is already sorted by the
                // runner, but defensively pick max in case that ever changes.
                var top = entry.Candidates
                    .OrderByDescending(c => c.Confidence)
                    .First();
                list.Add((f.Truth.Genre, top));
            }
        }

        var result = new Dictionary<string, ClassifierThresholdSweep>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, samples) in byClassifier)
        {
            var buckets = new List<ThresholdBucket>(SweepThresholds.Length);
            ThresholdBucket? best = null;
            foreach (var threshold in SweepThresholds)
            {
                var bucket = ComputeBucket(threshold, samples);
                buckets.Add(bucket);
                if (best is null || bucket.F1 > best.F1 || (bucket.F1 == best.F1 && bucket.Threshold > best.Threshold))
                {
                    best = bucket;
                }
            }
            result[name] = new ClassifierThresholdSweep(name, buckets, best?.Threshold, best?.F1);
        }
        return result;
    }

    private static ThresholdBucket ComputeBucket(
        double threshold,
        IReadOnlyList<(string TruthGenre, GenreCandidate? Top)> samples)
    {
        int tp = 0, fp = 0, fn = 0;
        foreach (var (truth, top) in samples)
        {
            // No prediction OR below threshold → either FN (when truth is present, which it
            // always is in this filtered list) or true-negative (we don't count those — recall
            // basis is "truth tracks", precision basis is "predictions made"). So predict-nothing
            // when truth-is-present is a False-Negative.
            var truthFolded = BacktestMetrics.NormaliseForCompare(truth);
            if (top is null || top.Confidence < threshold)
            {
                fn++;
                continue;
            }
            var predictedFolded = BacktestMetrics.NormaliseForCompare(top.Value);
            if (predictedFolded == truthFolded)
            {
                tp++;
            }
            else
            {
                fp++;
            }
        }

        var precision = (tp + fp) == 0 ? 0 : (double)tp / (tp + fp);
        var recall = (tp + fn) == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = (precision + recall) == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new ThresholdBucket(threshold, tp, fp, fn, precision, recall, f1);
    }

    /// <summary>
    /// Builds the cross-file trace aggregation: who-wins-when per source, plus resolver
    /// fallback counts. <see cref="BacktestFileResult.WinningGenreSource"/> is the source that
    /// supplied the genre the resolver applied — we tally wins/losses against the truth match.
    /// Tracks without a winning source (existing-tag passthrough, no candidates at all) are
    /// skipped so the win-rate denominator stays honest.
    /// </summary>
    private static TraceAggregate BuildTraceAggregate(IReadOnlyList<BacktestFileResult> files)
    {
        var stats = new Dictionary<string, (int wins, int losses)>(StringComparer.OrdinalIgnoreCase);
        int fallback = 0, aggregatedFallback = 0, cleanMatch = 0;

        foreach (var f in files)
        {
            if (!string.IsNullOrWhiteSpace(f.WinningGenreSource))
            {
                var key = f.WinningGenreSource;
                stats.TryGetValue(key, out var current);
                if (f.Genre.Outcome == BacktestOutcome.Match)
                {
                    stats[key] = (current.wins + 1, current.losses);
                }
                else if (f.Genre.Outcome == BacktestOutcome.Mismatch)
                {
                    stats[key] = (current.wins, current.losses + 1);
                }
                // Match/Mismatch only — NoTruth/NoPrediction/ToleranceMatch don't move the needle.
            }

            // Resolver-side: did it produce a clean taxonomy match, the "no match, top-1
            // fallback" path (FallbackApplied), or did a TF classifier emit an :aggregated-fallback
            // because no parent cleared the aggregate threshold?
            if (f.GenreLookupTrace is { Count: > 0 })
            {
                var winning = f.GenreLookupTrace[^1];   // resolver appends the matched candidate last
                if (winning.ChosenGenre is not null)
                {
                    cleanMatch++;
                }
                if (!string.IsNullOrEmpty(winning.ApiSource)
                    && winning.ApiSource.EndsWith(":aggregated-fallback", StringComparison.Ordinal))
                {
                    aggregatedFallback++;
                }
            }
            // FallbackApplied — surfaced by the source being a raw provider/classifier candidate
            // even though the entry's ChosenGenre is null (no taxonomy match anywhere). The
            // resolver still surfaces MatchedCandidate via the per-file WinningGenreSource, so
            // a null ChosenGenre + non-null WinningGenreSource is the signature.
            if (f.GenreLookupTrace is { Count: > 0 }
                && f.GenreLookupTrace[^1].ChosenGenre is null
                && !string.IsNullOrEmpty(f.WinningGenreSource))
            {
                fallback++;
            }
        }

        var sourceStats = stats.ToDictionary(
            kv => kv.Key,
            kv => new SourceStats(kv.Key, kv.Value.wins, kv.Value.losses),
            StringComparer.OrdinalIgnoreCase);

        return new TraceAggregate(sourceStats, fallback, aggregatedFallback, cleanMatch);
    }

    private static DimensionMetrics SumOutcomes(
        IReadOnlyList<BacktestFileResult> files,
        Func<BacktestFileResult, BacktestComparison> selector,
        Func<BacktestFileResult, TruthMatchSource>? truthMatchSelector = null)
    {
        int match = 0, tol = 0, miss = 0, noPred = 0, noTruth = 0;
        int primaryOnly = 0, secondaryOnly = 0, both = 0;
        var sourceCounts = new Dictionary<Models.TagFieldSource, int>();
        foreach (var f in files)
        {
            var comparison = selector(f);
            switch (comparison.Outcome)
            {
                case BacktestOutcome.Match: match++; break;
                case BacktestOutcome.ToleranceMatch: tol++; break;
                case BacktestOutcome.Mismatch: miss++; break;
                case BacktestOutcome.NoPrediction: noPred++; break;
                case BacktestOutcome.NoTruth: noTruth++; break;
            }
            if (comparison.Source is { } src)
            {
                sourceCounts.TryGetValue(src, out var c);
                sourceCounts[src] = c + 1;
            }
            if (truthMatchSelector is not null)
            {
                switch (truthMatchSelector(f))
                {
                    case TruthMatchSource.Primary: primaryOnly++; break;
                    case TruthMatchSource.Secondary: secondaryOnly++; break;
                    case TruthMatchSource.Both: both++; break;
                }
            }
        }
        return new DimensionMetrics(files.Count, match, tol, miss, noPred, noTruth, sourceCounts)
        {
            PrimaryOnlyMatches = primaryOnly,
            SecondaryOnlyMatches = secondaryOnly,
            BothMatches = both,
        };
    }

    private static Dictionary<string, GenreConfusion> BuildGenreConfusion(
        IReadOnlyList<BacktestFileResult> files)
    {
        var byTruth = new Dictionary<string, List<BacktestFileResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.Truth.Genre)) continue;
            if (!byTruth.TryGetValue(f.Truth.Genre, out var list))
            {
                list = [];
                byTruth[f.Truth.Genre] = list;
            }
            list.Add(f);
        }

        var result = new Dictionary<string, GenreConfusion>(StringComparer.OrdinalIgnoreCase);
        foreach (var (genre, rows) in byTruth)
        {
            int match = 0, miss = 0, noPred = 0;
            var predicted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                switch (r.Genre.Outcome)
                {
                    case BacktestOutcome.Match: match++; break;
                    case BacktestOutcome.Mismatch: miss++; break;
                    case BacktestOutcome.NoPrediction: noPred++; break;
                }
                var predLabel = r.Genre.Prediction?.ToString() ?? "(none)";
                predicted.TryGetValue(predLabel, out var count);
                predicted[predLabel] = count + 1;
            }
            result[genre] = new GenreConfusion(genre, rows.Count, match, miss, noPred, predicted);
        }
        return result;
    }
}
