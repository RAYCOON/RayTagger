using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Maps a track's existing genre tag to a per-genre BPM range configured under
/// <c>analysis.bpm.tempo_ranges_by_genre</c>. The resolved range is passed to Essentia as a
/// per-track profile so the tempo histogram only considers BPM values that genre-typed material
/// can plausibly take — which eliminates the half/double-time errors Essentia is otherwise prone
/// to on extreme tempos (e.g. a 86 BPM DnB intro detected as 172).
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item>Read <see cref="TrackTags.Genre"/>. Empty → null (Essentia default).</item>
///   <item>Normalise via <see cref="RayTagger.Core.Mapping.Taxonomy.NormaliseByAlias"/> so
///         "Tech House" maps to "House" before lookup. If no taxonomy is configured the raw
///         string is used (only exact-name matches will hit).</item>
///   <item>Lookup in the configured genre→range map (case-insensitive).</item>
///   <item>If no match, return <c>TempoRangeFallback</c>, which may also be null.</item>
/// </list>
/// Returning <c>null</c> means "let Essentia use its default range" — semantically distinct from
/// returning a specific range, which always overrides the default.
/// </remarks>
public interface ITempoRangeResolver
{
    BpmTempoRange? Resolve(TrackTags existing);
}
