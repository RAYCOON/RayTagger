namespace RayTagger.Core.Mapping;

/// <summary>
/// Action block of a mapping rule. Empty string clears the field. <see cref="ExtraTags"/> captures
/// any <c>tag.&lt;name&gt;</c> keys flattened into a dictionary by the loader.
/// </summary>
public sealed class SetClause
{
    public string? Genre { get; set; }
    public string? Subgenre { get; set; }

    /// <summary>Values for keys written as <c>tag.NAME: VALUE</c> in YAML.</summary>
    public Dictionary<string, string> ExtraTags { get; set; } = new(StringComparer.Ordinal);

    public string? AddKeyword { get; set; }
}
