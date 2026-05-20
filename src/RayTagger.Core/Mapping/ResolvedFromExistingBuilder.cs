using RayTagger.Core.Models;

namespace RayTagger.Core.Mapping;

/// <summary>
/// Lifts a <see cref="TrackTags"/> (raw disk values) into a <see cref="ResolvedTrackTags"/> with
/// every populated field marked as <see cref="TagFieldSource.Existing"/>. Used by rule-only
/// debugging entry points (CLI <c>explain</c> verb, UI "Gegen Datei testen…") so the rule engine
/// sees the same shape it would during a normal scan, minus the Analysis/Lookup contributions.
/// </summary>
/// <remarks>
/// Null disk values come back as <see cref="ResolvedField.Empty{T}"/> / <see cref="ResolvedField.EmptyValue{T}"/>
/// — same convention the merger uses, so downstream code doesn't have to special-case "no value".
/// </remarks>
public static class ResolvedFromExistingBuilder
{
    public static ResolvedTrackTags Build(TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return new ResolvedTrackTags(
            Genre: existing.Genre is null
                ? ResolvedField.Empty<string>()
                : new ResolvedField<string>(existing.Genre, TagFieldSource.Existing, 1.0),
            SubGenre: existing.SubGenre is null
                ? ResolvedField.Empty<string>()
                : new ResolvedField<string>(existing.SubGenre, TagFieldSource.Existing, 1.0),
            Bpm: existing.Bpm is null
                ? ResolvedField.EmptyValue<double>()
                : new ResolvedValueField<double>(existing.Bpm, TagFieldSource.Existing, 1.0),
            Key: existing.Key is null
                ? ResolvedField.Empty<MusicalKey>()
                : new ResolvedField<MusicalKey>(existing.Key, TagFieldSource.Existing, 1.0),
            Energy: existing.Energy is null
                ? ResolvedField.EmptyValue<int>()
                : new ResolvedValueField<int>(existing.Energy, TagFieldSource.Existing, 1.0),
            Mood: existing.Mood is null
                ? ResolvedField.Empty<string>()
                : new ResolvedField<string>(existing.Mood, TagFieldSource.Existing, 1.0),
            SetPosition: existing.SetPosition is null
                ? ResolvedField.Empty<string>()
                : new ResolvedField<string>(existing.SetPosition, TagFieldSource.Existing, 1.0),
            Custom: new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));
    }
}
