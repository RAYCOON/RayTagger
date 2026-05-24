using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Core.Mapping;

/// <summary>
/// Resolves API-supplied genre / sub-genre candidates against a controlled <see cref="Taxonomy"/>
/// via whole-word case-insensitive matching, "longest match wins" tiebreak, and an
/// existing-tag-protection rule. See <c>docs/ARCHITECTURE.md</c> (Genre Resolution section) or
/// the README for the full spec.
/// </summary>
/// <remarks>
/// The resolver is a singleton — compiled regex patterns and lookup sets are cached per
/// <see cref="Taxonomy"/> instance via <see cref="ConditionalWeakTable{TKey,TValue}"/>, so a
/// taxonomy reload (new Taxonomy instance) transparently invalidates the cache without leaking
/// patterns from the old instance.
/// </remarks>
public sealed class TaxonomyGenreResolver
{
    /// <summary>
    /// Minimum confidence the top-1 candidate must clear before the resolver's "no taxonomy
    /// match" fallback emits it into an empty-genre slot. Without this floor a low-confidence
    /// user-tag (LastFm "favourite"/"seen-live" with confidence ~0.05) would silently land in
    /// the genre slot just because the slot was empty. Set low (0.20) — moderate-confidence
    /// provider hits should still rescue empty slots, this only blocks pure noise.
    /// </summary>
    public const double FallbackMinConfidence = 0.20;

    private readonly ConditionalWeakTable<Taxonomy, CompiledTaxonomy> _cache = new();

    /// <summary>
    /// Runs the full resolution pipeline for one track: genre-match search across all candidates,
    /// subgenre-match search across the matching candidate's rest + the API's
    /// <see cref="LookupResult.SubGenreCandidates"/>, then existing-tag-protection.
    /// </summary>
    public TaxonomyGenreResolution Resolve(
        IReadOnlyList<GenreCandidate> genreCandidates,
        IReadOnlyList<GenreCandidate> subGenreCandidates,
        Taxonomy taxonomy,
        string? existingGenre,
        string? existingSubgenre,
        SourcePriorityOptions? sourcePriority = null)
    {
        ArgumentNullException.ThrowIfNull(genreCandidates);
        ArgumentNullException.ThrowIfNull(subGenreCandidates);
        ArgumentNullException.ThrowIfNull(taxonomy);

        var compiled = _cache.GetValue(taxonomy, CompiledTaxonomy.Build);
        var priorityOpts = sourcePriority ?? SourcePriorityOptions.Defaults;

        // Step 0: source-weighted re-ordering (B6.6 + B6.6.1). Heuristic confidence (average
        // of 7 feature scores in [0,1]) and TF-aggregated confidence (softmax mass summed by
        // taxonomy parent, ~0.1-0.5 for clear winners) live on different scales — sorting
        // purely by Confidence is unfair to TF. Tier values come from
        // <see cref="SourcePriorityOptions"/> (configurable per tagger.yaml, see §4.0d);
        // defaults match the hardcoded table from B6.6.
        var sortedCandidates = genreCandidates
            .Select((c, idx) => (Candidate: c, OriginalIndex: idx))
            .OrderByDescending(t => SourcePriority(t.Candidate.Source, priorityOpts))
            .ThenByDescending(t => t.Candidate.Confidence)
            .ThenBy(t => t.OriginalIndex)
            .Select(t => t.Candidate)
            .ToList();

        // Step 1: walk candidates in (source-priority, confidence) order, find the first one
        // that matches at least one taxonomy genre. Trace every inspected candidate (even
        // those that didn't match) so the UI can show "tried 3, none matched" debug info.
        var trace = new List<CandidateTraceEntry>();
        string? chosenGenre = null;
        string gcRest = string.Empty;
        GenreCandidate? matchedCandidate = null;

        foreach (var gc in sortedCandidates)
        {
            var genreHits = compiled.MatchGenres(gc.Value);
            if (genreHits.Count == 0)
            {
                trace.Add(new CandidateTraceEntry(
                    gc.Value, gc.Source, gc.Confidence, [], null, [], null));
                continue;
            }

            chosenGenre = compiled.PickLongestThenYamlOrder(genreHits);
            gcRest = CompiledTaxonomy.RemoveGenreWord(gc.Value, chosenGenre);
            matchedCandidate = gc;

            // Subgenre search runs immediately so we can include the result in the trace row.
            var subHitsForTrace = CollectSubgenreMatches(compiled, chosenGenre, gcRest, subGenreCandidates);
            var chosenSubForTrace = subHitsForTrace.Count > 0
                ? subHitsForTrace
                    .OrderByDescending(t => t.Sub.Length)
                    .ThenByDescending(t => t.Confidence)
                    .ThenBy(t => compiled.SubgenreOrderIndex(chosenGenre, t.Sub))
                    .First().Sub
                : null;

            trace.Add(new CandidateTraceEntry(
                gc.Value, gc.Source, gc.Confidence,
                genreHits, chosenGenre,
                subHitsForTrace.Select(t => t.Sub).Distinct(StringComparer.Ordinal).ToList(),
                chosenSubForTrace));

            // Match found — break per spec ("stop at first candidate with genre match").
            break;
        }

        string? chosenSubgenre = null;
        if (chosenGenre is not null)
        {
            // Re-pick from the same hits gathered during the trace pass (idempotent — same data
            // gives same answer). Yes, this duplicates the work above; the savings vs the
            // simpler "collect then pick" flow aren't worth the conditional.
            var subHits = CollectSubgenreMatches(compiled, chosenGenre, gcRest, subGenreCandidates);
            chosenSubgenre = subHits.Count > 0
                ? subHits
                    .OrderByDescending(t => t.Sub.Length)
                    .ThenByDescending(t => t.Confidence)
                    .ThenBy(t => compiled.SubgenreOrderIndex(chosenGenre, t.Sub))
                    .First().Sub
                : null;
        }

        // Step 3: apply existing-tag-protection.
        string? proposedGenre;
        var fallbackApplied = false;
        if (chosenGenre is not null)
        {
            proposedGenre = (string.IsNullOrEmpty(existingGenre) || !compiled.IsKnownGenre(existingGenre))
                ? chosenGenre
                : null;
        }
        else if (string.IsNullOrEmpty(existingGenre)
                 && sortedCandidates.Count > 0
                 && sortedCandidates[0].Confidence >= FallbackMinConfidence)
        {
            // Fallback: no taxonomy match anywhere, but the slot is empty — take the top-1
            // candidate from the SORTED list (highest source-priority + highest confidence)
            // so the user gets *something* (which they can later add to the taxonomy or
            // rewrite via a mapping rule). Subgenre has no fallback — it has no anchor.
            //
            // Gated by FallbackMinConfidence: without that floor a LastFm user-tag with
            // confidence ~0.05 (e.g. "favourite", "seen-live") would silently land in the
            // genre slot. The threshold is intentionally low — moderate-confidence provider
            // hits should still rescue an empty slot, just not noise.
            proposedGenre = sortedCandidates[0].Value;
            fallbackApplied = true;
            // Surface the source so callers can attribute the value (UI, write-stage confidence).
            matchedCandidate = sortedCandidates[0];
        }
        else
        {
            proposedGenre = null;
        }

        string? proposedSubgenre = null;
        if (chosenSubgenre is not null && chosenGenre is not null)
        {
            proposedSubgenre = (string.IsNullOrEmpty(existingSubgenre)
                                || !compiled.IsKnownSubgenre(chosenGenre, existingSubgenre))
                ? chosenSubgenre
                : null;
        }

        return new TaxonomyGenreResolution(
            proposedGenre,
            proposedSubgenre,
            chosenGenre,
            chosenSubgenre,
            fallbackApplied,
            matchedCandidate,
            trace);
    }

    /// <summary>
    /// Source-priority tier for ordering candidates before the first-match-wins walk. Higher
    /// number = considered first. Tier values come from <paramref name="opts"/> so users can
    /// override the defaults in <c>tagger.yaml</c> under <c>mapping.source_priority</c>.
    /// See <c>docs/PLAN_GENRE_CLASSIFICATION.md §4.0d</c> for rationale.
    /// </summary>
    /// <remarks>
    /// Classifier bucket assignment is hardcoded (per-suffix routing); provider candidates check
    /// <see cref="SourcePriorityOptions.Providers"/> first for a name-keyed override before
    /// falling back to <see cref="SourcePriorityOptions.Provider"/>.
    /// </remarks>
    internal static int SourcePriority(string source, SourcePriorityOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        if (string.IsNullOrEmpty(source))
        {
            // Unknown / empty source — treat as fallback tier so it doesn't accidentally
            // beat provider hits without explicit policy.
            return opts.ClassifierAggregatedFallback;
        }
        // Check the more-specific suffix first. Today `:aggregated-fallback` doesn't end with
        // `:aggregated` (it ends with `-fallback`), so the order accidentally works either way,
        // but a future `:aggregated-strict`-style suffix would silently fall through to the
        // provider default if `:aggregated` were checked first.
        if (source.EndsWith(":aggregated-fallback", StringComparison.Ordinal))
        {
            return opts.ClassifierAggregatedFallback;
        }
        if (source.EndsWith(":aggregated", StringComparison.Ordinal))
        {
            return opts.ClassifierAggregated;
        }
        if (source.StartsWith("classifier:essentia-tf-", StringComparison.Ordinal))
        {
            return opts.ClassifierTfRaw;
        }
        if (source.StartsWith("classifier:heuristic", StringComparison.Ordinal))
        {
            return opts.ClassifierHeuristic;
        }
        if (source.StartsWith("classifier:", StringComparison.Ordinal))
        {
            return opts.ClassifierOther;
        }
        // Provider candidate: check the per-name override map before falling back to the
        // generic Provider tier. The lookup runs on the SOURCE string verbatim (provider names
        // like "musicbrainz", "discogs") — the Dictionary's StringComparer.OrdinalIgnoreCase
        // is set in TaggerOptionsLoader.NormaliseDictionaryComparers after YAML binding.
        if (opts.Providers.TryGetValue(source, out var perProvider))
        {
            return perProvider;
        }
        return opts.Provider;
    }

    /// <summary>
    /// Collects subgenre matches from two sources: the rest of the matching genre candidate
    /// (genre word stripped out) plus every <see cref="LookupResult.SubGenreCandidates"/> entry.
    /// </summary>
    private static List<(string Sub, double Confidence)> CollectSubgenreMatches(
        CompiledTaxonomy compiled,
        string chosenGenre,
        string gcRest,
        IReadOnlyList<GenreCandidate> subGenreCandidates)
    {
        var hits = new List<(string Sub, double Confidence)>();
        // Source A: rest of the matching genre candidate. Confidence inherited from the parent
        // candidate — but we don't have it here; the caller passes via `subGenreCandidates`
        // signature alone. For source A we synthesize confidence 1.0 because the match came
        // from the same string that already won the genre contest.
        foreach (var s in compiled.MatchSubgenres(gcRest, chosenGenre))
        {
            hits.Add((s, 1.0));
        }
        // Source B: separate SubGenreCandidates (e.g. Discogs `style` entries).
        foreach (var sc in subGenreCandidates)
        {
            foreach (var s in compiled.MatchSubgenres(sc.Value, chosenGenre))
            {
                hits.Add((s, sc.Confidence));
            }
        }
        return hits;
    }
}

/// <summary>
/// Pre-computed indexes and compiled regex patterns for one <see cref="Taxonomy"/> instance.
/// Built lazily by <see cref="TaxonomyGenreResolver"/> and cached via
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> so a new Taxonomy reload gets fresh patterns
/// and the old ones are eligible for GC.
/// </summary>
internal sealed class CompiledTaxonomy
{
    private readonly IReadOnlyList<(string Name, Regex Pattern, int Order)> _genres;
    private readonly Dictionary<string, IReadOnlyList<(string Name, Regex Pattern, int Order)>> _subgenresByGenre;
    private readonly HashSet<string> _knownGenres;
    private readonly Dictionary<string, HashSet<string>> _knownSubgenresByGenre;

    private CompiledTaxonomy(
        IReadOnlyList<(string, Regex, int)> genres,
        Dictionary<string, IReadOnlyList<(string, Regex, int)>> subgenresByGenre,
        HashSet<string> knownGenres,
        Dictionary<string, HashSet<string>> knownSubgenresByGenre)
    {
        _genres = genres;
        _subgenresByGenre = subgenresByGenre;
        _knownGenres = knownGenres;
        _knownSubgenresByGenre = knownSubgenresByGenre;
    }

    public static CompiledTaxonomy Build(Taxonomy taxonomy)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);

        var genres = taxonomy.Genres
            .Select((name, idx) => (name, BuildWholeWordPattern(name), idx))
            .ToList();

        var subgenresByGenre = new Dictionary<string, IReadOnlyList<(string, Regex, int)>>(
            StringComparer.OrdinalIgnoreCase);
        var knownSubgenresByGenre = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (genreName, subList) in taxonomy.Subgenres)
        {
            var compiledSubs = subList
                .Select((name, idx) => (name, BuildWholeWordPattern(name), idx))
                .ToList();
            subgenresByGenre[genreName] = compiledSubs;
            knownSubgenresByGenre[genreName] = new HashSet<string>(subList, StringComparer.OrdinalIgnoreCase);
        }

        var knownGenres = new HashSet<string>(taxonomy.Genres, StringComparer.OrdinalIgnoreCase);
        return new CompiledTaxonomy(genres, subgenresByGenre, knownGenres, knownSubgenresByGenre);
    }

    public List<string> MatchGenres(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var hits = new List<string>();
        foreach (var (name, pattern, _) in _genres)
        {
            if (pattern.IsMatch(input)) hits.Add(name);
        }
        return hits;
    }

    public List<string> MatchSubgenres(string input, string genreName)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        if (!_subgenresByGenre.TryGetValue(genreName, out var subs)) return [];
        var hits = new List<string>();
        foreach (var (name, pattern, _) in subs)
        {
            if (pattern.IsMatch(input)) hits.Add(name);
        }
        return hits;
    }

    public string PickLongestThenYamlOrder(IReadOnlyList<string> hits)
    {
        // hits come from MatchGenres which iterates in _genres order, so we can read the order
        // index back from there. But scanning _genres N times is O(N×M); cheaper to look up
        // length first, then disambiguate on order.
        var orderIndex = _genres.ToDictionary(t => t.Name, t => t.Order, StringComparer.Ordinal);
        return hits
            .OrderByDescending(s => s.Length)
            .ThenBy(s => orderIndex[s])
            .First();
    }

    public int SubgenreOrderIndex(string genreName, string subgenre)
    {
        if (!_subgenresByGenre.TryGetValue(genreName, out var subs)) return int.MaxValue;
        foreach (var (name, _, order) in subs)
        {
            if (string.Equals(name, subgenre, StringComparison.Ordinal)) return order;
        }
        return int.MaxValue;
    }

    public static string RemoveGenreWord(string input, string genre)
    {
        var stripped = BuildWholeWordPattern(genre).Replace(input, " ");
        // Collapse whitespace and trim — guards subsequent subgenre matching against
        // double-spaces like "Vocal  " left over from a tail-position strip.
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    public bool IsKnownGenre(string genre) => _knownGenres.Contains(genre);

    public bool IsKnownSubgenre(string genre, string subgenre) =>
        _knownSubgenresByGenre.TryGetValue(genre, out var set) && set.Contains(subgenre);

    private static Regex BuildWholeWordPattern(string name) =>
        // `\b` is Unicode-aware in .NET and works at the boundary of word-chars/non-word-chars.
        // For multi-word genres like "Drum and Bass" or "Hip Hop" the embedded spaces are
        // non-word, so `\bDrum and Bass\b` still matches the run as a whole and rejects
        // "Drum-and-Bassist". For special chars like "R&B" the same logic holds (`&` is non-word).
        new($@"\b{Regex.Escape(name)}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
