namespace RayTagger.Core.Configuration;

/// <summary>
/// A single validation problem found while loading a YAML configuration file.
/// </summary>
/// <param name="YamlPath">Dot-separated path into the YAML document (e.g. "lookup.api_keys.acoustid").</param>
/// <param name="Reason">Human-readable explanation.</param>
public sealed record ConfigurationError(string YamlPath, string Reason);

/// <summary>
/// Thrown when a configuration file is missing required values, references unknown keys, or has
/// other structural problems. Aggregates all errors found so the user sees the full picture in
/// one run rather than playing whack-a-mole.
/// </summary>
public sealed class ConfigurationException : Exception
{
    public IReadOnlyList<ConfigurationError> Errors { get; }

    public ConfigurationException(string message, IReadOnlyList<ConfigurationError> errors)
        : base(BuildMessage(message, errors))
    {
        Errors = errors;
    }

    public ConfigurationException(string message, Exception inner)
        : base(message, inner)
    {
        Errors = [];
    }

    public ConfigurationException(string message)
        : base(message)
    {
        Errors = [];
    }

    public ConfigurationException()
    {
        Errors = [];
    }

    private static string BuildMessage(string message, IReadOnlyList<ConfigurationError> errors)
    {
        if (errors.Count == 0)
        {
            return message;
        }

        var lines = new List<string> { message, string.Empty };
        for (var i = 0; i < errors.Count; i++)
        {
            lines.Add($"  {i + 1}. [{errors[i].YamlPath}] {errors[i].Reason}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
