namespace RayTagger.Core.Mapping;

/// <summary>
/// Top-level mapping ruleset loaded from <c>mappings.yaml</c>. See docs/ARCHITECTURE.md §5 for the
/// evaluation model. This is data-only; the rule evaluator lives in Phase 3.
/// </summary>
public sealed class MappingRuleSet
{
    public int Version { get; set; } = 1;
    public MappingDefaults Defaults { get; set; } = new();
    public List<MappingRule> Rules { get; set; } = [];
}

public enum OnMatch
{
    Stop,
    Continue,
}

public sealed class MappingDefaults
{
    public OnMatch OnMatch { get; set; } = OnMatch.Stop;
    public bool CaseSensitive { get; set; }
}

public sealed class MappingRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Predicate. <c>null</c> (key omitted) or an empty <see cref="WhenClause"/> (<c>when: {}</c>)
    /// both mean catch-all — matches every file. The evaluator must treat both forms identically.
    /// </summary>
    public WhenClause? When { get; set; }

    public SetClause Set { get; set; } = new();

    /// <summary>Overrides <see cref="MappingDefaults.OnMatch"/> for this rule when present.</summary>
    public OnMatch? OnMatch { get; set; }

    /// <summary>Resolves the rule's own <see cref="OnMatch"/> or falls back to the ruleset defaults.</summary>
    public OnMatch EffectiveOnMatch(MappingDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return OnMatch ?? defaults.OnMatch;
    }
}
