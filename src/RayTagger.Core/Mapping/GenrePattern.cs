namespace RayTagger.Core.Mapping;

/// <summary>
/// Polymorphic predicate value for genre/sub-genre fields. YAML accepts one of:
/// <list type="bullet">
///   <item>A list of literal names: <c>genre: ["House", "Tech House"]</c></item>
///   <item>The sentinel <c>:missing:</c> — matches when the resolved field is null or empty.</item>
///   <item>The sentinel <c>:any:</c> — matches any non-empty value.</item>
///   <item>A single literal string (treated as a one-element list).</item>
/// </list>
/// </summary>
public sealed class GenrePattern
{
    public const string MissingSentinel = ":missing:";
    public const string AnySentinel = ":any:";

    public bool MatchMissing { get; init; }
    public bool MatchAny { get; init; }
    public IReadOnlyList<string> Values { get; init; } = [];

    public static GenrePattern Missing { get; } = new() { MatchMissing = true };
    public static GenrePattern Any { get; } = new() { MatchAny = true };

    public static GenrePattern FromLiterals(IEnumerable<string> values) =>
        new() { Values = values.ToList() };
}
