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

        return errors;
    }

    private static void ValidateScan(ScanOptions scan, List<ConfigurationError> errors)
    {
        if (string.IsNullOrWhiteSpace(scan.Source))
        {
            errors.Add(new ConfigurationError("scan.source", "Source directory is required."));
        }

        if (scan.Parallelism < 1)
        {
            errors.Add(new ConfigurationError(
                "scan.parallelism",
                $"Must be >= 1 (got {scan.Parallelism})."));
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
