using System.Text.RegularExpressions;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RayTagger.Core.Configuration;

/// <summary>
/// Loads <c>tagger.yaml</c> into a <see cref="TaggerOptions"/> tree. Performs (in order):
/// <list type="number">
///   <item>Optional .env file load.</item>
///   <item><c>${env:VAR}</c> placeholder substitution on the raw YAML text.</item>
///   <item>YamlDotNet deserialization with strict unknown-property handling.</item>
///   <item>Semantic validation (required fields, enum values, range sanity).</item>
///   <item>Path normalisation against the config-file directory.</item>
/// </list>
/// Errors are aggregated into a single <see cref="ConfigurationException"/> so the user sees all
/// problems at once.
/// </summary>
public static class TaggerOptionsLoader
{
    private static readonly IDeserializer Deserializer = BuildDeserializer();

    /// <param name="configPath">Path to <c>tagger.yaml</c>. Relative paths resolve against the CWD.</param>
    /// <param name="dotEnvPath">
    /// Optional .env file path. If not provided, looks for <c>.env</c> in the same directory as
    /// <paramref name="configPath"/>.
    /// </param>
    public static TaggerOptions Load(string configPath, string? dotEnvPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fullConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullConfigPath))
        {
            throw new ConfigurationException($"Configuration file not found: {fullConfigPath}");
        }

        var configDirectory = Path.GetDirectoryName(fullConfigPath)
            ?? throw new ConfigurationException($"Cannot determine directory of: {fullConfigPath}");

        var dotEnv = LoadDotEnvIfPresent(dotEnvPath, configDirectory);
        var rawYaml = File.ReadAllText(fullConfigPath);

        return LoadFromString(rawYaml, configDirectory, dotEnv, fullConfigPath);
    }

    /// <summary>
    /// In-memory variant used by tests and callers that already have the YAML text. Performs the
    /// same env-var substitution, validation, and path normalisation as <see cref="Load"/>.
    /// </summary>
    /// <param name="yaml">Raw YAML text.</param>
    /// <param name="configDirectory">Absolute directory that relative paths resolve against.</param>
    /// <param name="dotEnv">Optional fallback env-var values (e.g. from a .env file).</param>
    /// <param name="sourceDescription">Identifier used in error messages (file path or "(in-memory)").</param>
    public static TaggerOptions LoadFromString(
        string yaml,
        string configDirectory,
        IReadOnlyDictionary<string, string>? dotEnv = null,
        string sourceDescription = "(in-memory)")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        var resolver = new EnvVarResolver(dotEnv);

        if (!resolver.TryResolve(yaml, out var substitutedYaml, out var missing))
        {
            var errors = missing
                .Select(name => new ConfigurationError(
                    YamlPath: "(env)",
                    Reason: $"Environment variable '{name}' is referenced via ${{env:{name}}} but is not set and has no .env fallback."))
                .ToList();
            throw new ConfigurationException(
                $"Configuration loading failed for {sourceDescription}.",
                errors);
        }

        // Detect the legacy `existing_tags_policy` key BEFORE deserialization. The key has been
        // removed from the POCO (in favour of per-dimension existing_confidence), so we strip
        // the line from the raw YAML to avoid YamlDotNet's strict unknown-property error and
        // capture the value separately for the migration step.
        var legacyPolicyValue = ExtractAndStripLegacyPolicy(ref substitutedYaml);

        TaggerOptions options;
        try
        {
            options = Deserializer.Deserialize<TaggerOptions>(substitutedYaml)
                ?? throw new ConfigurationException($"Configuration is empty: {sourceDescription}");
        }
        catch (YamlException ex)
        {
            throw new ConfigurationException(
                $"YAML deserialization failed for {sourceDescription} (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}",
                ex);
        }

        var validationErrors = TaggerOptionsValidator.Validate(options);
        if (validationErrors.Count > 0)
        {
            throw new ConfigurationException(
                $"Configuration validation failed for {sourceDescription}.",
                validationErrors);
        }

        NormalisePaths(options, configDirectory);
        LoadTaxonomyIfConfigured(options, sourceDescription);
        LoadEnergyCalibrationIfConfigured(options, sourceDescription);
        NormaliseDictionaryComparers(options);
        MigrateLegacyPolicyToExistingConfidence(options, legacyPolicyValue);

        return options;
    }

    /// <summary>
    /// Captures the value of a legacy <c>read.existing_tags_policy</c> entry and strips the line
    /// from the YAML so the (now policy-free) <see cref="TaggerOptions"/> POCO doesn't reject it
    /// as an unknown property. Returns the raw value (e.g. <c>"always_overwrite"</c>) or
    /// <c>null</c> when the key is absent.
    /// </summary>
    /// <remarks>
    /// The regex matches mapping entries only — leading whitespace then the literal
    /// <c>existing_tags_policy:</c> then the value. Commented-out lines (<c>#&#160;...</c>) don't
    /// trigger because <c>#</c> isn't whitespace. Multi-line/flow-style values aren't supported
    /// — the legacy YAML only ever set this as a single scalar token so the simple form covers
    /// every real-world config.
    /// </remarks>
    private static readonly Regex LegacyPolicyKeyRegex = new(
        @"^([ \t]*)existing_tags_policy[ \t]*:[ \t]*([^\s#]+)[ \t]*(#[^\r\n]*)?\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static string? ExtractAndStripLegacyPolicy(ref string yaml)
    {
        var match = LegacyPolicyKeyRegex.Match(yaml);
        if (!match.Success) return null;

        var value = match.Groups[2].Value;
        // Replace the entire matched line with an empty mapping-friendly placeholder. We can't
        // just delete the line because if `read:` had ONLY this key, the parent would become an
        // empty mapping (`read:\n`) — YamlDotNet accepts that, so we just blank the line.
        yaml = LegacyPolicyKeyRegex.Replace(yaml, string.Empty, 1);
        return value;
    }

    /// <summary>
    /// Migrates a user's <c>read.existing_tags_policy</c> setting (now stripped from the YAML
    /// before deserialization) to the equivalent per-dimension <c>existing_confidence</c> values
    /// and records a deprecation warning.
    /// </summary>
    /// <remarks>
    /// Mapping (proven by <c>PolicyEquivalenceTests</c> in the Core.Tests assembly):
    /// <list type="bullet">
    ///   <item><c>always_overwrite</c> → all <c>existing_confidence = 0.0</c>.</item>
    ///   <item><c>skip_if_present</c> / <c>fill_only_empty</c> → no change (defaults already
    ///   reproduce the legacy behaviour, just emit the warning).</item>
    ///   <item>Anything else → unknown-value warning, no migration.</item>
    /// </list>
    /// When the user has ALSO set explicit per-dimension <c>existing_confidence</c> values, the
    /// policy still wins — preserving the previous "policy is the master switch" contract.
    /// Users who want per-dimension overrides should drop the policy key entirely.
    /// </remarks>
    private static void MigrateLegacyPolicyToExistingConfidence(TaggerOptions options, string? legacyValue)
    {
        if (legacyValue is null)
        {
            // No legacy key in the YAML — nothing to migrate, no warning to emit.
            return;
        }

        // Lowercase via culture-invariant ToUpperInvariant + lower mapping would be the safe
        // option per CA1308, but YAML enum tokens are ASCII-only — the equality check below uses
        // OrdinalIgnoreCase to sidestep both the culture and the casing concerns at once.
        switch (legacyValue.Trim())
        {
            case var s when string.Equals(s, "always_overwrite", StringComparison.OrdinalIgnoreCase):
                options.Analysis.Bpm.ExistingConfidence = 0.0;
                options.Analysis.Key.ExistingConfidence = 0.0;
                options.Analysis.Energy.ExistingConfidence = 0.0;
                options.Lookup.ExistingConfidence = 0.0;
                options.Deprecations.Add(
                    "`read.existing_tags_policy: always_overwrite` is deprecated and has been " +
                    "removed in favour of per-dimension `existing_confidence`. Migrated to " +
                    "`analysis.{bpm,key,energy}.existing_confidence: 0.0` and " +
                    "`lookup.existing_confidence: 0.0`. Remove the `read.existing_tags_policy` " +
                    "key from tagger.yaml.");
                break;
            case var s when string.Equals(s, "skip_if_present", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(s, "fill_only_empty", StringComparison.OrdinalIgnoreCase):
                options.Deprecations.Add(
                    $"`read.existing_tags_policy: {legacyValue}` is deprecated and has been " +
                    "removed. The same behaviour is the default " +
                    "(`existing_confidence: 1.0` across all dimensions). Remove the key from " +
                    "tagger.yaml.");
                break;
            default:
                options.Deprecations.Add(
                    $"`read.existing_tags_policy: {legacyValue}` was not recognised. The key " +
                    "has been removed; use per-dimension `existing_confidence` instead.");
                break;
        }
    }

    /// <summary>
    /// YamlDotNet's default mapping deserialiser allocates a fresh <see cref="Dictionary{TKey,TValue}"/>
    /// with the ordinal-comparer rather than copying values into the POCO's pre-initialised
    /// (case-insensitive) instance. That silently breaks lookups like genre tag "House" against
    /// config key "house". Rebuild the affected dictionaries here so downstream consumers can rely
    /// on case-insensitive semantics without each having to redo the workaround.
    /// </summary>
    private static void NormaliseDictionaryComparers(TaggerOptions options)
    {
        options.Analysis.Bpm.TempoRangesByGenre = new Dictionary<string, RayTagger.Core.Models.BpmTempoRange>(
            options.Analysis.Bpm.TempoRangesByGenre,
            StringComparer.OrdinalIgnoreCase);
        options.Analysis.Bpm.SnapStepByGenre = new Dictionary<string, double>(
            options.Analysis.Bpm.SnapStepByGenre,
            StringComparer.OrdinalIgnoreCase);
        options.Mapping.SourcePriority.Providers = new Dictionary<string, int>(
            options.Mapping.SourcePriority.Providers,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads the external energy calibration profile when
    /// <see cref="EnergyAnalyzerOptions.CalibrationFile"/> is set and the file exists. A missing
    /// file is not an error — the analyzer falls back to the built-in defaults. Malformed YAML
    /// surfaces as a <see cref="ConfigurationException"/> so the user fixes it before the next scan.
    /// </summary>
    private static void LoadEnergyCalibrationIfConfigured(TaggerOptions options, string sourceDescription)
    {
        var path = options.Analysis.Energy.CalibrationFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }
        try
        {
            options.Analysis.Energy.LoadedCalibration = EnergyCalibrationProfileLoader.Load(path);
        }
        catch (ConfigurationException ex)
        {
            throw new ConfigurationException(
                $"Energy calibration file referenced by {sourceDescription} failed to load: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Loads the external taxonomy file when <c>taxonomy.file</c> is configured and stores the
    /// parsed snapshot back on <see cref="TaxonomyOptions.Loaded"/>. Failures surface as a
    /// <see cref="ConfigurationException"/> so the user sees the problem at config load,
    /// not the first time a rule tries to normalise something.
    /// </summary>
    private static void LoadTaxonomyIfConfigured(TaggerOptions options, string sourceDescription)
    {
        if (string.IsNullOrWhiteSpace(options.Taxonomy.File))
        {
            return;
        }
        try
        {
            options.Taxonomy.Loaded = TaxonomyLoader.Load(options.Taxonomy.File);
        }
        catch (ConfigurationException ex)
        {
            throw new ConfigurationException(
                $"Taxonomy file referenced by {sourceDescription} failed to load: {ex.Message}",
                ex);
        }
    }

    private static IReadOnlyDictionary<string, string>? LoadDotEnvIfPresent(string? explicitPath, string configDirectory)
    {
        var path = explicitPath ?? Path.Combine(configDirectory, ".env");
        return File.Exists(path) ? EnvVarResolver.LoadDotEnv(path) : null;
    }

    private static IDeserializer BuildDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

    private static void NormalisePaths(TaggerOptions options, string configDirectory)
    {
        options.Scan.Source = PathNormalizer.Normalize(options.Scan.Source, configDirectory);
        options.Mapping.RulesFile = PathNormalizer.Normalize(options.Mapping.RulesFile, configDirectory);

        if (!string.IsNullOrWhiteSpace(options.Lookup.Cache.Directory))
        {
            options.Lookup.Cache.Directory = PathNormalizer.Normalize(options.Lookup.Cache.Directory, configDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.Sort.Destination))
        {
            options.Sort.Destination = PathNormalizer.Normalize(options.Sort.Destination, configDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.Logging.File.Directory))
        {
            options.Logging.File.Directory = PathNormalizer.Normalize(options.Logging.File.Directory, configDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.Logging.Sqlite.Path))
        {
            options.Logging.Sqlite.Path = PathNormalizer.Normalize(options.Logging.Sqlite.Path, configDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.NativeTools.ManifestFile))
        {
            options.NativeTools.ManifestFile = PathNormalizer.Normalize(options.NativeTools.ManifestFile, configDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.Taxonomy.File))
        {
            options.Taxonomy.File = PathNormalizer.Normalize(options.Taxonomy.File, configDirectory);
        }

        if (!string.IsNullOrWhiteSpace(options.Analysis.Energy.CalibrationFile))
        {
            options.Analysis.Energy.CalibrationFile =
                PathNormalizer.Normalize(options.Analysis.Energy.CalibrationFile, configDirectory);
        }
    }
}
