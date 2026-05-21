namespace RayTagger.Core.Configuration;

/// <summary>
/// Pure validation pass over a deserialized <see cref="TaggerOptions"/>. Returns every problem
/// found rather than failing fast — so the user sees the full picture in one report.
/// </summary>
internal static class TaggerOptionsValidator
{
    public static IReadOnlyList<ConfigurationError> Validate(TaggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ConfigurationError>();

        if (options.Version != 1)
        {
            errors.Add(new ConfigurationError(
                "version",
                $"Only version 1 is supported, found {options.Version}."));
        }

        ValidateScan(options.Scan, errors);
        ValidateAnalysis(options.Analysis, errors);
        ValidateLookup(options.Lookup, errors);
        ValidateSort(options.Sort, errors);
        ValidateWrite(options.Write, errors);

        return errors;
    }

    private static void ValidateWrite(WriteOptions write, List<ConfigurationError> errors)
    {
        // Parse the raw tag-fields dictionary into the typed map. Any token errors are recorded
        // here; the typed map is assigned back so downstream stages don't re-parse.
        write.TagFieldMap = TagFieldMapBuilder.Build(write.TagFields, errors);
    }

    private static void ValidateScan(ScanOptions scan, List<ConfigurationError> errors)
    {
        if (string.IsNullOrWhiteSpace(scan.Source))
        {
            errors.Add(new ConfigurationError("scan.source", "Source directory is required."));
        }

        if (scan.Parallelism < 0)
        {
            errors.Add(new ConfigurationError(
                "scan.parallelism",
                $"Must be >= 0 (got {scan.Parallelism}). 0 = auto (Environment.ProcessorCount)."));
        }

        if (scan.Formats.Count == 0)
        {
            errors.Add(new ConfigurationError("scan.formats", "At least one format must be enabled."));
        }
        else
        {
            var allowed = new[] { "mp3", "flac", "aiff" };
            foreach (var fmt in scan.Formats)
            {
                if (!allowed.Contains(fmt, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(new ConfigurationError(
                        "scan.formats",
                        $"Unsupported format '{fmt}'. Allowed: {string.Join(", ", allowed)}."));
                }
            }
        }
    }

    private static void ValidateAnalysis(AnalysisOptions analysis, List<ConfigurationError> errors)
    {
        // DisplayNotation is now an enum; YamlDotNet rejects unknown values at deserialization
        // time, so no string-based validation is needed here.
        ValidateAnalyzer(analysis.Bpm, "analysis.bpm", errors);
        if (analysis.Bpm.SnapTolerancePercent < 0)
        {
            errors.Add(new ConfigurationError(
                "analysis.bpm.snap_tolerance_percent",
                $"Must be >= 0 (got {analysis.Bpm.SnapTolerancePercent}). 0 disables snapping."));
        }
        if (analysis.Bpm.SnapStep <= 0)
        {
            errors.Add(new ConfigurationError(
                "analysis.bpm.snap_step",
                $"Must be > 0 (got {analysis.Bpm.SnapStep}). 1.0 = integer snap, 0.5 = half-BPM snap."));
        }
        foreach (var (genre, range) in analysis.Bpm.TempoRangesByGenre)
        {
            ValidateTempoRange(range, $"analysis.bpm.tempo_ranges_by_genre.{genre}", errors);
        }
        if (analysis.Bpm.TempoRangeFallback is not null)
        {
            ValidateTempoRange(analysis.Bpm.TempoRangeFallback, "analysis.bpm.tempo_range_fallback", errors);
        }
        ValidateAnalyzer(analysis.Key, "analysis.key", errors);
        ValidateAnalyzer(analysis.Energy, "analysis.energy", errors);
        ValidateAnalyzer(analysis.Fingerprint, "analysis.fingerprint", errors);
    }

    private static void ValidateAnalyzer(AnalyzerOptions analyzer, string pathPrefix, List<ConfigurationError> errors)
    {
        if (analyzer.Enabled && string.IsNullOrWhiteSpace(analyzer.Provider))
        {
            errors.Add(new ConfigurationError(
                $"{pathPrefix}.provider",
                "Provider name is required when enabled."));
        }

        if (analyzer.MinConfidence is < 0 or > 1)
        {
            errors.Add(new ConfigurationError(
                $"{pathPrefix}.min_confidence",
                $"Must be in [0,1], got {analyzer.MinConfidence}."));
        }

        if (analyzer.TimeoutSeconds < 1)
        {
            errors.Add(new ConfigurationError(
                $"{pathPrefix}.timeout_seconds",
                $"Must be >= 1, got {analyzer.TimeoutSeconds}."));
        }
    }

    private static void ValidateLookup(LookupOptions lookup, List<ConfigurationError> errors)
    {
        if (lookup.Cache.TtlDays < 0)
        {
            errors.Add(new ConfigurationError(
                "lookup.cache.ttl_days",
                $"Must be >= 0, got {lookup.Cache.TtlDays}."));
        }

        if (lookup.Enabled && lookup.Providers.Count == 0)
        {
            errors.Add(new ConfigurationError(
                "lookup.providers",
                "At least one provider must be listed when lookup is enabled."));
        }
    }

    // Plausible musical tempo bounds — anything below 30 or above 300 BPM is either a
    // mis-configured range or a special-case the user probably wants to handle explicitly.
    // Essentia's own range is 40-208; we widen slightly so users with very slow/fast material
    // (e.g. drone, hardcore) don't trip the validator unnecessarily.
    private const double MinPlausibleBpm = 30;
    private const double MaxPlausibleBpm = 300;

    private static void ValidateTempoRange(
        RayTagger.Core.Models.BpmTempoRange range, string pathPrefix, List<ConfigurationError> errors)
    {
        // Both bounds must be specified together — a half-specified range carries no usable
        // signal for the fold algorithm.
        if (range.Min.HasValue != range.Max.HasValue)
        {
            errors.Add(new ConfigurationError(
                pathPrefix,
                "min and max must be set together; provide both or neither."));
            return;
        }
        if (!range.HasRange) return;   // empty entry — treat as "no range" (validation no-op)

        var min = range.Min!.Value;
        var max = range.Max!.Value;

        // NaN/Infinity slip through every comparison (any comparison with NaN is false per IEEE 754),
        // so guard them first. YAML can't express these values, but programmatic constructors can,
        // and an undetected NaN would later confuse the histogram-range logic in Essentia.
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            errors.Add(new ConfigurationError(
                pathPrefix,
                $"min/max must be finite numbers, got [{min}, {max}]."));
            return;
        }
        if (min >= max)
        {
            errors.Add(new ConfigurationError(
                pathPrefix,
                $"min ({min}) must be strictly less than max ({max})."));
        }
        if (min < MinPlausibleBpm || max > MaxPlausibleBpm)
        {
            errors.Add(new ConfigurationError(
                pathPrefix,
                $"Range [{min}, {max}] is outside plausible musical bounds [{MinPlausibleBpm}, {MaxPlausibleBpm}]."));
        }
    }

    private static void ValidateSort(SortOptions sort, List<ConfigurationError> errors)
    {
        if (sort.Enabled && string.IsNullOrWhiteSpace(sort.Destination))
        {
            errors.Add(new ConfigurationError(
                "sort.destination",
                "Destination is required when sort is enabled."));
        }

        if (sort.Enabled && string.IsNullOrWhiteSpace(sort.Pattern))
        {
            errors.Add(new ConfigurationError(
                "sort.pattern",
                "Pattern is required when sort is enabled."));
        }
    }
}
