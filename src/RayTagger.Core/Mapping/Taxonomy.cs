namespace RayTagger.Core.Mapping;

/// <summary>
/// Canonical vocabulary for genre / sub-genre / mood / set-position fields, plus a normalisation
/// table that splits messy upstream strings (<c>"Tech House"</c>, <c>"Funky House"</c>) into a
/// clean canonical pair (<c>genre=House</c>, <c>subgenre=Tech</c>). Loaded from a YAML file
/// referenced via <c>tagger.yaml: taxonomy.file</c>; consulted by the rule engine via the
/// <c>normalise_genre: true</c> action and, when <see cref="Enforce"/> is true, by the
/// mapping-rules loader which rejects any <c>set:</c> value outside the declared allowlists.
/// </summary>
/// <remarks>
/// Why a separate file rather than inline in <c>tagger.yaml</c>: the vocabulary is the user's
/// long-term taxonomy decision and tends to live longer than any individual rule set. Sharing it
/// across multiple <c>mappings.yaml</c> files (a library DJ might have one set of rules for
/// promo-folder triage and another for set-prep) wants a single source of truth.
/// </remarks>
public sealed class Taxonomy
{
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Subgenres { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each entry maps one or more input aliases to a canonical (genre, subgenre) pair.
    /// The list form (rather than a flat string→pair map) lets the user spell out every
    /// variation a label might use without duplicating the canonical destination — e.g.
    /// <c>aliases: ["Funky House", "Funk House"]</c> both point at <c>House/Funk</c>.
    /// </summary>
    public IReadOnlyList<NormaliseEntry> Normalise { get; init; } = [];

    public IReadOnlyList<string> Moods { get; init; } = [];
    public IReadOnlyList<string> SetPositions { get; init; } = [];

    /// <summary>
    /// When true, every <c>set: { genre|subgenre|mood|set_position: X }</c> in a mappings file
    /// is rejected at load time if <c>X</c> isn't in the corresponding allowlist. Catches
    /// taxonomy drift early — a typo'd "Hyperdrive" mood becomes a load error, not a silent
    /// rogue value polluting the library.
    /// </summary>
    public bool Enforce { get; init; }

    /// <summary>Fast lookup built from <see cref="Normalise"/>.</summary>
    public IReadOnlyDictionary<string, (string Genre, string Subgenre)> NormaliseByAlias { get; init; }
        = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

    public static Taxonomy Empty { get; } = new();
}

/// <summary>
/// One entry in <see cref="Taxonomy.Normalise"/>. Each entry produces a single canonical pair
/// but accepts multiple input forms.
/// </summary>
public sealed class NormaliseEntry
{
    public string Genre { get; init; } = string.Empty;
    public string Subgenre { get; init; } = string.Empty;
    public IReadOnlyList<string> Aliases { get; init; } = [];
}
