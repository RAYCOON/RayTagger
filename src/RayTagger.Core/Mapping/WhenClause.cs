namespace RayTagger.Core.Mapping;

/// <summary>
/// Predicate block of a mapping rule. Sibling keys are combined with logical AND. All fields are
/// optional; an empty <c>WhenClause</c> matches every file (catch-all). See docs/ARCHITECTURE.md
/// §5.2 for predicate semantics.
/// </summary>
public sealed class WhenClause
{
    public GenrePattern? Genre { get; set; }
    public string? GenreRegex { get; set; }

    public GenrePattern? Subgenre { get; set; }
    public string? SubgenreRegex { get; set; }

    public NumericRange? Bpm { get; set; }

    public List<string> Key { get; set; } = [];
    public KeyMode? KeyMode { get; set; }

    public NumericRange? Energy { get; set; }

    public List<string> Artist { get; set; } = [];
    public string? ArtistRegex { get; set; }

    public string? PathGlob { get; set; }

    public List<WhenClause> AnyOf { get; set; } = [];
    public List<WhenClause> AllOf { get; set; } = [];
    public WhenClause? Not { get; set; }
}

public enum KeyMode
{
    Major,
    Minor,
}

/// <summary>Inclusive <c>{ min, max }</c> range used for BPM and Energy predicates.</summary>
public sealed class NumericRange
{
    public double Min { get; set; }
    public double Max { get; set; }
}
