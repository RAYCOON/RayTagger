using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace RayTagger.Core.IO;

/// <summary>
/// Substitutes <c>${env:VAR_NAME}</c> placeholders in a YAML source string. Resolution order
/// (highest wins): process environment, optional .env file. Missing required variables produce a
/// structured error so the caller can fail loud with a clear pointer to the missing key.
/// </summary>
/// <remarks>
/// We substitute on the raw YAML text rather than on deserialized values so the substituted form
/// is what YamlDotNet sees — keeps the type-coercion logic free of placeholder handling and means
/// errors are caught before deserialization runs.
/// </remarks>
public sealed partial class EnvVarResolver
{
    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvVarPattern();

    private readonly IReadOnlyDictionary<string, string> _dotEnvFallback;

    /// <param name="dotEnvFallback">
    /// Optional values from a .env file. Process environment takes precedence over these.
    /// Pass <c>null</c> or empty when no .env is loaded.
    /// </param>
    public EnvVarResolver(IReadOnlyDictionary<string, string>? dotEnvFallback = null)
    {
        _dotEnvFallback = dotEnvFallback ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns <paramref name="rawText"/> with every <c>${env:VAR}</c> placeholder substituted.
    /// </summary>
    /// <param name="rawText">Source string (e.g. raw YAML contents).</param>
    /// <param name="resolved">On success, the fully-substituted text.</param>
    /// <param name="missing">On failure, the list of variable names that could not be resolved.</param>
    public bool TryResolve(string rawText, [NotNullWhen(true)] out string? resolved, out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var missingList = new List<string>();
        // Process line by line so we can skip the portion after a YAML comment marker. Substituting
        // inside comments would (a) be invisible to the user and (b) make documentation comments
        // that mention the ${env:NAME} syntax accidentally trigger lookups.
        var lines = rawText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var commentStart = FindCommentStart(line);
            if (commentStart < 0)
            {
                lines[i] = SubstituteOne(line, missingList);
            }
            else
            {
                var prefix = SubstituteOne(line[..commentStart], missingList);
                lines[i] = prefix + line[commentStart..];
            }
        }

        if (missingList.Count > 0)
        {
            resolved = null;
            // Dedup so the same variable referenced N times doesn't surface as N identical
            // error lines. Preserve first-seen order so the user can match against the YAML.
            missing = [.. missingList.Distinct(StringComparer.Ordinal)];
            return false;
        }

        resolved = string.Join('\n', lines);
        missing = [];
        return true;
    }

    private string SubstituteOne(string fragment, List<string> missingList) =>
        EnvVarPattern().Replace(fragment, match =>
        {
            var name = match.Groups[1].Value;
            var value = LookupValue(name);
            if (value is null)
            {
                missingList.Add(name);
                return match.Value;
            }
            return value;
        });

    /// <summary>
    /// Returns the index of the YAML comment marker '#' or -1 if there is no comment on this line.
    /// YAML treats '#' as a comment only when at the start of a line or preceded by whitespace,
    /// so we conservatively detect that pattern. Quoted-string parsing is intentionally omitted —
    /// values containing ' # ' as data would be misclassified, but that's an extreme edge case in
    /// configuration YAML.
    /// </summary>
    private static int FindCommentStart(string line)
    {
        if (line.Length == 0)
        {
            return -1;
        }
        if (line[0] == '#')
        {
            return 0;
        }
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '#' && (line[i - 1] == ' ' || line[i - 1] == '\t'))
            {
                return i;
            }
        }
        return -1;
    }

    private string? LookupValue(string name)
    {
        var fromEnv = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        return _dotEnvFallback.TryGetValue(name, out var fromDotEnv) ? fromDotEnv : null;
    }

    /// <summary>
    /// Loads a .env file into a key-value dictionary. Lines starting with <c>#</c> or empty lines
    /// are ignored. Values may be wrapped in single or double quotes; quotes are stripped.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadDotEnv(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            dict[key] = value;
        }

        return dict;
    }
}
