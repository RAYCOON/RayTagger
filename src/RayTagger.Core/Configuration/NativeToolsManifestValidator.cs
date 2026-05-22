using System.Text.RegularExpressions;

namespace RayTagger.Core.Configuration;

/// <summary>
/// Pure validation pass over a deserialized <see cref="NativeToolsManifest"/>. Returns every
/// problem rather than failing fast — see <see cref="TaggerOptionsValidator"/> for the same
/// pattern.
/// </summary>
internal static partial class NativeToolsManifestValidator
{
    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static IReadOnlyList<ConfigurationError> Validate(NativeToolsManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var errors = new List<ConfigurationError>();

        if (manifest.SchemaVersion != 1)
        {
            errors.Add(new ConfigurationError(
                "schema_version",
                $"Only schema_version=1 is supported, found {manifest.SchemaVersion}."));
        }

        if (manifest.Tools.Count == 0 && manifest.Models.Count == 0)
        {
            errors.Add(new ConfigurationError(
                "tools",
                "At least one tool or model entry is required."));
            return errors;
        }

        foreach (var (toolName, entry) in manifest.Tools)
        {
            var prefix = $"tools.{toolName}";

            if (string.IsNullOrWhiteSpace(toolName))
            {
                errors.Add(new ConfigurationError("tools", "Tool key cannot be empty."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Version))
            {
                errors.Add(new ConfigurationError($"{prefix}.version",
                    "Version label is required (used as cache subdirectory)."));
            }

            if (entry.Sources.Count == 0)
            {
                errors.Add(new ConfigurationError($"{prefix}.sources",
                    "At least one platform source is required."));
                continue;
            }

            foreach (var (rid, source) in entry.Sources)
            {
                ValidateSource($"{prefix}.sources.{rid}", rid, source, errors);
            }
        }

        foreach (var (modelKey, entry) in manifest.Models)
        {
            var prefix = $"models.{modelKey}";

            if (string.IsNullOrWhiteSpace(modelKey))
            {
                errors.Add(new ConfigurationError("models", "Model key cannot be empty."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Version))
            {
                errors.Add(new ConfigurationError($"{prefix}.version",
                    "Version label is required (used as cache-bust marker)."));
            }

            if (entry.Files.Count == 0)
            {
                errors.Add(new ConfigurationError($"{prefix}.files",
                    "At least one file is required."));
                continue;
            }

            for (var i = 0; i < entry.Files.Count; i++)
            {
                ValidateModelFile($"{prefix}.files[{i}]", entry.Files[i], errors);
            }
        }

        return errors;
    }

    private static void ValidateModelFile(string prefix, NativeModelFile file, List<ConfigurationError> errors)
    {
        if (string.IsNullOrWhiteSpace(file.Url))
        {
            errors.Add(new ConfigurationError($"{prefix}.url", "URL is required."));
        }
        else if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add(new ConfigurationError($"{prefix}.url",
                $"Must be an absolute http(s) URL, got '{file.Url}'."));
        }
        else if (uri.Scheme == Uri.UriSchemeHttp)
        {
            errors.Add(new ConfigurationError($"{prefix}.url",
                "Plain HTTP is refused — model downloads must use HTTPS."));
        }

        if (string.IsNullOrWhiteSpace(file.Sha256))
        {
            errors.Add(new ConfigurationError($"{prefix}.sha256",
                "SHA-256 is required (lowercase hex, 64 chars)."));
        }
        else if (!Sha256Pattern().IsMatch(file.Sha256))
        {
            errors.Add(new ConfigurationError($"{prefix}.sha256",
                $"Must be 64 lowercase hex chars, got '{file.Sha256}'."));
        }

        if (!string.IsNullOrEmpty(file.RenameTo))
        {
            // rename_to must be a SIMPLE filename — no slashes, no .., no rooted paths. The
            // bootstrapper joins it onto the cache directory; a path-traversal value here
            // would let a malicious manifest write outside the cache root.
            if (file.RenameTo.Contains('/', StringComparison.Ordinal)
                || file.RenameTo.Contains('\\', StringComparison.Ordinal)
                || file.RenameTo.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(file.RenameTo))
            {
                errors.Add(new ConfigurationError($"{prefix}.rename_to",
                    $"Must be a simple filename without path separators or '..', got '{file.RenameTo}'."));
            }
        }
    }

    private static void ValidateSource(
        string prefix,
        string rid,
        NativeToolSource source,
        List<ConfigurationError> errors)
    {
        if (string.IsNullOrWhiteSpace(rid))
        {
            errors.Add(new ConfigurationError(prefix, "RID key cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(source.Url))
        {
            errors.Add(new ConfigurationError($"{prefix}.url", "URL is required."));
        }
        else if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add(new ConfigurationError($"{prefix}.url",
                $"Must be an absolute http(s) URL, got '{source.Url}'."));
        }
        else if (uri.Scheme == Uri.UriSchemeHttp)
        {
            errors.Add(new ConfigurationError($"{prefix}.url",
                "Plain HTTP is refused — bootstrapping downloads must use HTTPS."));
        }

        if (string.IsNullOrWhiteSpace(source.Sha256))
        {
            errors.Add(new ConfigurationError($"{prefix}.sha256",
                "SHA-256 is required (lowercase hex, 64 chars)."));
        }
        else if (!Sha256Pattern().IsMatch(source.Sha256))
        {
            errors.Add(new ConfigurationError($"{prefix}.sha256",
                $"Must be 64 lowercase hex chars, got '{source.Sha256}'."));
        }

        if (source.ArchiveFormat == NativeToolArchiveFormat.None
            && !string.IsNullOrWhiteSpace(source.BinaryPath))
        {
            errors.Add(new ConfigurationError($"{prefix}.binary_path",
                "binary_path is only meaningful when archive_format is tar_gz or zip."));
        }
    }
}
