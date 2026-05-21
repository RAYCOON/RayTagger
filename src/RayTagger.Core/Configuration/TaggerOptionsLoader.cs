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

        return options;
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
