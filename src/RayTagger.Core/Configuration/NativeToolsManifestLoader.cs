using RayTagger.Core.IO;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RayTagger.Core.Configuration;

/// <summary>
/// Loads a <c>native-tools.yaml</c> file into a <see cref="NativeToolsManifest"/>. Mirrors the
/// pipeline used by <see cref="TaggerOptionsLoader"/> (env-var substitution, validation aggregation,
/// path normalisation), so error messages and behaviour are consistent across both files.
/// </summary>
public static class NativeToolsManifestLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static NativeToolsManifest Load(string manifestPath, string? dotEnvPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"Native-tools manifest not found: {fullPath}");
        }

        var configDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new ConfigurationException($"Cannot determine directory of: {fullPath}");

        var dotEnv = LoadDotEnvIfPresent(dotEnvPath, configDirectory);
        var rawYaml = File.ReadAllText(fullPath);

        return LoadFromString(rawYaml, configDirectory, dotEnv, fullPath);
    }

    public static NativeToolsManifest LoadFromString(
        string yaml,
        string configDirectory,
        IReadOnlyDictionary<string, string>? dotEnv = null,
        string sourceDescription = "(in-memory)")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        var resolver = new EnvVarResolver(dotEnv);
        if (!resolver.TryResolve(yaml, out var substituted, out var missing))
        {
            var errors = missing
                .Select(name => new ConfigurationError(
                    "(env)",
                    $"Environment variable '{name}' is referenced via ${{env:{name}}} but is not set and has no .env fallback."))
                .ToList();
            throw new ConfigurationException(
                $"Native-tools manifest loading failed for {sourceDescription}.",
                errors);
        }

        NativeToolsManifest manifest;
        try
        {
            manifest = Deserializer.Deserialize<NativeToolsManifest>(substituted)
                ?? throw new ConfigurationException($"Native-tools manifest is empty: {sourceDescription}");
        }
        catch (YamlException ex)
        {
            throw new ConfigurationException(
                $"YAML deserialization failed for {sourceDescription} (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}",
                ex);
        }

        var errorsFound = NativeToolsManifestValidator.Validate(manifest);
        if (errorsFound.Count > 0)
        {
            throw new ConfigurationException(
                $"Native-tools manifest validation failed for {sourceDescription}.",
                errorsFound);
        }

        if (!string.IsNullOrWhiteSpace(manifest.CacheDirectory))
        {
            manifest.CacheDirectory = PathNormalizer.Normalize(manifest.CacheDirectory, configDirectory);
        }

        return manifest;
    }

    private static IReadOnlyDictionary<string, string>? LoadDotEnvIfPresent(string? explicitPath, string configDirectory)
    {
        var path = explicitPath ?? Path.Combine(configDirectory, ".env");
        return File.Exists(path) ? EnvVarResolver.LoadDotEnv(path) : null;
    }
}
