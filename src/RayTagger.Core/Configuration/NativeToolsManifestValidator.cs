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

        if (manifest.Tools.Count == 0)
        {
            errors.Add(new ConfigurationError(
                "tools",
                "At least one tool entry is required."));
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

        return errors;
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
