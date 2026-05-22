using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Merges what we already know (tags on disk) with what analysis and online lookup produced into
/// a <see cref="ResolvedTrackTags"/> whose field sources reflect <c>existing_tags_policy</c> and
/// each analyzer's per-dimension <c>min_confidence</c> threshold. See docs/ARCHITECTURE.md §6.2
/// for the policy matrix.
/// </summary>
/// <remarks>
/// Genre / SubGenre resolution has two modes:
/// <list type="bullet">
///   <item>Legacy (<c>resolver</c> or <c>taxonomy</c> null): top-1 <see cref="GenreCandidate"/>
///   wins blindly, gated only by <c>existing_tags_policy</c>.</item>
///   <item>Taxonomy-aware (both supplied): whole-word match against the taxonomy with
///   longest-match-wins, existing-tag protection, and Subgenre splitting — see
///   <see cref="TaxonomyGenreResolver"/>.</item>
/// </list>
/// Mapping rules run AFTER this merger and can still overwrite the result — rules are
/// <see cref="TagFieldSource.Rules"/> and always win regardless of policy.
/// </remarks>
public static class TagMerger
{
    public static ResolvedTrackTags Merge(
        TrackTags existing,
        AnalysisResult analysis,
        LookupResult? lookup,
        AnalysisOptions analysisConfig,
        ExistingTagsPolicy policy,
        Taxonomy? taxonomy = null,
        TaxonomyGenreResolver? resolver = null,
        IReadOnlyList<ProviderTraceEntry>? providerTrace = null,
        SourcePriorityOptions? sourcePriority = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(analysisConfig);

        var bpm = MergeValue(
            existing.Bpm,
            analysis.Bpm.Bpm,
            analysis.Bpm.Confidence,
            analysisConfig.Bpm.MinConfidence,
            policy);

        var key = MergeReference(
            existing.Key,
            analysis.Key.Key,
            analysis.Key.Confidence,
            analysisConfig.Key.MinConfidence,
            policy);

        var energy = MergeValue(
            existing.Energy,
            analysis.Energy.Energy,
            analysis.Energy.Confidence,
            analysisConfig.Energy.MinConfidence,
            policy);

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
            genre = MergeLookupString(existing.Genre, topGenre, policy);
            subgenre = MergeLookupString(existing.SubGenre, topSubGenre, policy);
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

    private static ResolvedField<string> MergeLookupString(
        string? existing,
        GenreCandidate? lookupCandidate,
        ExistingTagsPolicy policy)
    {
        var lookupUsable = lookupCandidate is not null && !string.IsNullOrWhiteSpace(lookupCandidate.Value);

        if (!lookupUsable)
        {
            return new ResolvedField<string>(existing, TagFieldSource.Existing, existing is null ? 0 : 1);
        }

        if (policy == ExistingTagsPolicy.AlwaysOverwrite || string.IsNullOrEmpty(existing))
        {
            return new ResolvedField<string>(lookupCandidate!.Value, TagFieldSource.Lookup, lookupCandidate.Confidence);
        }

        return new ResolvedField<string>(existing, TagFieldSource.Existing, 1);
    }

    /// <summary>Merge a nullable-struct field (BPM, Energy).</summary>
    private static ResolvedValueField<T> MergeValue<T>(
        T? existing,
        T? analyzed,
        double confidence,
        double minConfidence,
        ExistingTagsPolicy policy) where T : struct
    {
        var analysisUsable = analyzed.HasValue && confidence >= minConfidence;

        if (!analysisUsable)
        {
            return new ResolvedValueField<T>(existing, TagFieldSource.Existing, existing.HasValue ? 1 : 0);
        }

        if (policy == ExistingTagsPolicy.AlwaysOverwrite || !existing.HasValue)
        {
            return new ResolvedValueField<T>(analyzed, TagFieldSource.Analysis, confidence);
        }

        // skip_if_present / fill_only_empty: existing wins when present.
        return new ResolvedValueField<T>(existing, TagFieldSource.Existing, 1);
    }

    /// <summary>Merge a nullable-reference field (Key, Genre, …).</summary>
    private static ResolvedField<T> MergeReference<T>(
        T? existing,
        T? analyzed,
        double confidence,
        double minConfidence,
        ExistingTagsPolicy policy) where T : class
    {
        var analysisUsable = analyzed is not null && confidence >= minConfidence;

        if (!analysisUsable)
        {
            return new ResolvedField<T>(existing, TagFieldSource.Existing, existing is null ? 0 : 1);
        }

        if (policy == ExistingTagsPolicy.AlwaysOverwrite || existing is null)
        {
            return new ResolvedField<T>(analyzed, TagFieldSource.Analysis, confidence);
        }

        return new ResolvedField<T>(existing, TagFieldSource.Existing, 1);
    }
}
