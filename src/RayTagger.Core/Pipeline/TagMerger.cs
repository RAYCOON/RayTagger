using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Merges what we already know (tags on disk) with what analysis and online lookup produced into
/// a <see cref="ResolvedTrackTags"/>. Each output field carries its <see cref="TagFieldSource"/>;
/// the merge picks between existing/analyzer/lookup based on per-dimension <c>min_confidence</c>
/// (gates noisy analyzer output) and per-dimension <c>existing_confidence</c> (controls how
/// readily an existing tag is displaced).
/// </summary>
/// <remarks>
/// <para>
/// Per-dimension <c>existing_confidence</c> (<see cref="AnalyzerOptions.ExistingConfidence"/>
/// for BPM/Key/Energy, <see cref="LookupOptions.ExistingConfidence"/> for legacy genre/subgenre)
/// has replaced the global <c>existing_tags_policy</c>. The legacy enum still works via the
/// loader migration but emits a deprecation warning. See <c>PolicyEquivalenceTests</c> for the
/// equivalence proofs and <c>TaggerOptionsLoader.MigrateLegacyPolicyToExistingConfidence</c>
/// for the mapping rule.
/// </para>
/// <para>
/// Genre / SubGenre resolution has two modes:
/// <list type="bullet">
///   <item>Legacy (<c>resolver</c> or <c>taxonomy</c> null): top-1 <see cref="GenreCandidate"/>
///   wins, gated by <see cref="LegacyLookupMinConfidence"/> (drops noise like LastFm user-tags)
///   AND the <c>lookupExistingConfidence</c> parameter (controls existing-tag protection).</item>
///   <item>Taxonomy-aware (both supplied): whole-word match against the taxonomy with
///   longest-match-wins, existing-tag protection via <see cref="TaxonomyGenreResolver"/>'s
///   own <c>IsKnownGenre</c> rule. The resolver's protection is binary by design — it does
///   not consult <c>lookupExistingConfidence</c>. Set <c>lookup.taxonomy_resolution: false</c>
///   if you need confidence-weighted lookup-vs-existing behaviour for genre.</item>
/// </list>
/// Mapping rules run AFTER this merger and can still overwrite the result — rules are
/// <see cref="TagFieldSource.Rules"/> and always win regardless of policy.
/// </para>
/// </remarks>
public static class TagMerger
{
    public static ResolvedTrackTags Merge(
        TrackTags existing,
        AnalysisResult analysis,
        LookupResult? lookup,
        AnalysisOptions analysisConfig,
        Taxonomy? taxonomy = null,
        TaxonomyGenreResolver? resolver = null,
        IReadOnlyList<ProviderTraceEntry>? providerTrace = null,
        SourcePriorityOptions? sourcePriority = null,
        double lookupExistingConfidence = 1.0)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(analysisConfig);

        var bpm = MergeValue(
            existing.Bpm,
            analysis.Bpm.Bpm,
            analysis.Bpm.Confidence,
            analysisConfig.Bpm.MinConfidence,
            analysisConfig.Bpm.ExistingConfidence);

        var key = MergeReference(
            existing.Key,
            analysis.Key.Key,
            analysis.Key.Confidence,
            analysisConfig.Key.MinConfidence,
            analysisConfig.Key.ExistingConfidence);

        var energy = MergeValue(
            existing.Energy,
            analysis.Energy.Energy,
            analysis.Energy.Confidence,
            analysisConfig.Energy.MinConfidence,
            analysisConfig.Energy.ExistingConfidence);

        ResolvedField<string> genre;
        ResolvedField<string> subgenre;
        IReadOnlyList<CandidateTraceEntry>? trace = null;

        if (lookup is not null && taxonomy is not null && resolver is not null)
        {
            var resolution = resolver.Resolve(
                lookup.GenreCandidates,
                lookup.SubGenreCandidates,
                taxonomy,
                existing.Genre,
                existing.SubGenre,
                sourcePriority);

            genre = BuildResolvedFromResolution(existing.Genre, resolution.ProposedGenre, resolution.MatchedCandidate);
            subgenre = BuildResolvedFromResolution(existing.SubGenre, resolution.ProposedSubgenre, resolution.MatchedCandidate);
            trace = resolution.Trace;
        }
        else
        {
            var topGenre = lookup?.GenreCandidates.Count > 0 ? lookup.GenreCandidates[0] : null;
            var topSubGenre = lookup?.SubGenreCandidates.Count > 0 ? lookup.SubGenreCandidates[0] : null;
            genre = MergeLookupString(existing.Genre, topGenre, lookupExistingConfidence);
            subgenre = MergeLookupString(existing.SubGenre, topSubGenre, lookupExistingConfidence);
        }

        // Mood / SetPosition aren't populated by analyzers or lookup today — they come either
        // from existing tags on disk or from a mapping rule's `set:`. Seed from existing.
        var mood = new ResolvedField<string>(existing.Mood, TagFieldSource.Existing,
            string.IsNullOrEmpty(existing.Mood) ? 0 : 1);
        var setPosition = new ResolvedField<string>(existing.SetPosition, TagFieldSource.Existing,
            string.IsNullOrEmpty(existing.SetPosition) ? 0 : 1);

        var custom = existing.Custom.ToDictionary(
            kv => kv.Key,
            kv => new ResolvedField<string>(kv.Value, TagFieldSource.Existing, 1),
            StringComparer.OrdinalIgnoreCase);

        var result = new ResolvedTrackTags(genre, subgenre, bpm, key, energy, mood, setPosition, custom);
        if (trace is not null) result = result with { GenreLookupTrace = trace };
        if (providerTrace is not null) result = result with { ProviderTrace = providerTrace };
        return result;
    }

    /// <summary>
    /// Wraps a resolver-produced proposed value into a <see cref="ResolvedField{T}"/>. When the
    /// resolver returned null (= existing is protected and wins), we re-emit the existing value
    /// with <see cref="TagFieldSource.Existing"/>; otherwise <see cref="TagFieldSource.Lookup"/>
    /// with the source candidate's confidence.
    /// </summary>
    private static ResolvedField<string> BuildResolvedFromResolution(
        string? existing,
        string? proposed,
        GenreCandidate? sourceCandidate)
    {
        if (proposed is null)
        {
            return new ResolvedField<string>(existing, TagFieldSource.Existing, existing is null ? 0 : 1);
        }
        var confidence = sourceCandidate?.Confidence ?? 0.5;
        return new ResolvedField<string>(proposed, TagFieldSource.Lookup, confidence);
    }

    /// <summary>
    /// Minimum confidence a lookup candidate must clear in the legacy "top-1 wins" path before it
    /// can overwrite an existing tag or fill an empty slot. The taxonomy-aware path doesn't need
    /// this gate because the resolver already filters via whole-word taxonomy matching, but the
    /// legacy path would otherwise write e.g. a LastFm "favourite"/"seen-live" user-tag with
    /// confidence ~0.05 over a curated value. 0.30 reflects "any meaningful signal" without
    /// blocking moderate-confidence provider hits.
    /// </summary>
    public const double LegacyLookupMinConfidence = 0.30;

    private static ResolvedField<string> MergeLookupString(
        string? existing,
        GenreCandidate? lookupCandidate,
        double existingConfidence)
    {
        var lookupUsable = lookupCandidate is not null
            && !string.IsNullOrWhiteSpace(lookupCandidate.Value)
            && lookupCandidate.Confidence >= LegacyLookupMinConfidence;

        if (!lookupUsable)
        {
            return new ResolvedField<string>(existing, TagFieldSource.Existing, existing is null ? 0 : existingConfidence);
        }

        if (string.IsNullOrEmpty(existing))
        {
            // Empty slot: fill regardless of existing_confidence. The user can't have "wanted to
            // protect" a value that wasn't there.
            return new ResolvedField<string>(lookupCandidate!.Value, TagFieldSource.Lookup, lookupCandidate.Confidence);
        }

        // Confidence-weighted choice: lookup candidate has to beat existing_confidence to
        // overwrite. Default existing_confidence=1.0 reproduces the classic "existing always
        // wins" behaviour (lookup confidences typically sit in [0,1] and rarely reach exactly
        // 1.0). Setting it to 0 lets every usable candidate overwrite.
        return lookupCandidate!.Confidence > existingConfidence
            ? new ResolvedField<string>(lookupCandidate.Value, TagFieldSource.Lookup, lookupCandidate.Confidence)
            : new ResolvedField<string>(existing, TagFieldSource.Existing, existingConfidence);
    }

    /// <summary>Merge a nullable-struct field (BPM, Energy).</summary>
    private static ResolvedValueField<T> MergeValue<T>(
        T? existing,
        T? analyzed,
        double confidence,
        double minConfidence,
        double existingConfidence) where T : struct
    {
        var analysisUsable = analyzed.HasValue && confidence >= minConfidence;

        if (!analysisUsable)
        {
            return new ResolvedValueField<T>(existing, TagFieldSource.Existing, existing.HasValue ? existingConfidence : 0);
        }

        if (!existing.HasValue)
        {
            return new ResolvedValueField<T>(analyzed, TagFieldSource.Analysis, confidence);
        }

        // Confidence-weighted choice: analyzer wins iff its confidence strictly beats the
        // existing_confidence floor. Default existing_confidence=1.0 keeps existing in place
        // (analyzer confidences rarely hit 1.0); setting it to 0 lets every usable analyzer
        // hit overwrite (per-dimension always-overwrite).
        return confidence > existingConfidence
            ? new ResolvedValueField<T>(analyzed, TagFieldSource.Analysis, confidence)
            : new ResolvedValueField<T>(existing, TagFieldSource.Existing, existingConfidence);
    }

    /// <summary>Merge a nullable-reference field (Key, Genre, …).</summary>
    private static ResolvedField<T> MergeReference<T>(
        T? existing,
        T? analyzed,
        double confidence,
        double minConfidence,
        double existingConfidence) where T : class
    {
        var analysisUsable = analyzed is not null && confidence >= minConfidence;

        if (!analysisUsable)
        {
            return new ResolvedField<T>(existing, TagFieldSource.Existing, existing is null ? 0 : existingConfidence);
        }

        if (existing is null)
        {
            return new ResolvedField<T>(analyzed, TagFieldSource.Analysis, confidence);
        }

        return confidence > existingConfidence
            ? new ResolvedField<T>(analyzed, TagFieldSource.Analysis, confidence)
            : new ResolvedField<T>(existing, TagFieldSource.Existing, existingConfidence);
    }
}
