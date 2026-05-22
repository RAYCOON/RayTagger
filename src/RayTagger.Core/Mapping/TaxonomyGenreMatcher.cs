namespace RayTagger.Core.Mapping;

/// <summary>
/// Public API for "given this label, which taxonomy parent genre matches it whole-word"?
/// Thin wrapper around the same <see cref="CompiledTaxonomy"/> internal that
/// <see cref="TaxonomyGenreResolver"/> uses — there's exactly one matching implementation
/// in the codebase. Built once per <see cref="Taxonomy"/> instance and reusable across
/// many label lookups; per-lookup cost is O(G × regex) where G is the number of parent
/// genres (~17 in the default taxonomy).
/// </summary>
/// <remarks>
/// Introduced for the B6.5 aggregation step in <c>RayTagger.Analysis.Genre</c> — the TF
/// classifier needs taxonomy-aware grouping to sum its top-K predictions by parent genre
/// (see <c>docs/PLAN_GENRE_CLASSIFICATION.md §4.0c</c>). Keeping the matcher separate from
/// the resolver means the resolver's bigger surface (subgenre search, existing-tag
/// protection, fallback path, trace generation) doesn't leak into the classifier.
/// </remarks>
public sealed class TaxonomyGenreMatcher
{
    private readonly CompiledTaxonomy _compiled;

    public TaxonomyGenreMatcher(Taxonomy taxonomy)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);
        _compiled = CompiledTaxonomy.Build(taxonomy);
    }

    /// <summary>
    /// Returns the parent genre name (canonical taxonomy spelling) whose whole-word
    /// pattern matches inside <paramref name="label"/>, with longest-match + YAML-order
    /// tiebreak. <c>null</c> when no parent matches — caller treats this as "label is
    /// outside the taxonomy" and skips aggregation for it.
    /// </summary>
    public string? MatchParent(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }
        var hits = _compiled.MatchGenres(label);
        if (hits.Count == 0)
        {
            return null;
        }
        return _compiled.PickLongestThenYamlOrder(hits);
    }
}
