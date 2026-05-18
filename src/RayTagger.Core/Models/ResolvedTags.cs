namespace RayTagger.Core.Models;

/// <summary>
/// Where a resolved tag value originated. The write stage consults this together with
/// <c>existing_tags_policy</c> from tagger.yaml — see docs/ARCHITECTURE.md §6.2.
/// </summary>
/// <remarks>
/// Invariant: <see cref="Rules"/>-sourced values always overwrite, regardless of policy. Rules are
/// the user's explicit declarative intent.
/// </remarks>
public enum TagFieldSource
{
    /// <summary>The value was already on the file when the scan started.</summary>
    Existing,

    /// <summary>The value came from an audio-analysis adapter (BPM/Key/Energy/Fingerprint).</summary>
    Analysis,

    /// <summary>The value came from an online metadata provider.</summary>
    Lookup,

    /// <summary>The value was set by a mapping rule's <c>set:</c> block.</summary>
    Rules,
}

/// <summary>
/// A resolved field of reference type (e.g. genre name, musical key). <see cref="Value"/> being
/// <c>null</c> means "absent". For value-type fields (BPM, energy) use <see cref="ResolvedValueField{T}"/>
/// — separate types are required because C#'s unconstrained-generic nullability doesn't carry the
/// <c>Nullable&lt;T&gt;</c> semantics that the pipeline needs for structs.
/// </summary>
public sealed record ResolvedField<T>(T? Value, TagFieldSource Source, double Confidence)
    where T : class
{
    public bool HasValue => Value is not null && !(Value is string s && s.Length == 0);
}

/// <summary>Resolved field for value types — wraps <c>T?</c> as <see cref="Nullable{T}"/>.</summary>
public sealed record ResolvedValueField<T>(T? Value, TagFieldSource Source, double Confidence)
    where T : struct
{
    public bool HasValue => Value.HasValue;
}

/// <summary>Non-generic factory helpers for both ResolvedField variants.</summary>
public static class ResolvedField
{
    public static ResolvedField<T> Empty<T>(TagFieldSource source = TagFieldSource.Existing)
        where T : class => new(default, source, 0);

    public static ResolvedValueField<T> EmptyValue<T>(TagFieldSource source = TagFieldSource.Existing)
        where T : struct => new(default, source, 0);
}

/// <summary>
/// The in-flight tag state during a pipeline run. Each logical field carries its source so the
/// write stage can apply <c>existing_tags_policy</c> correctly.
/// </summary>
public sealed record ResolvedTrackTags(
    ResolvedField<string> Genre,
    ResolvedField<string> SubGenre,
    ResolvedValueField<double> Bpm,
    ResolvedField<MusicalKey> Key,
    ResolvedValueField<int> Energy,
    IReadOnlyDictionary<string, ResolvedField<string>> Custom)
{
    public static ResolvedTrackTags Empty { get; } = new(
        ResolvedField.Empty<string>(),
        ResolvedField.Empty<string>(),
        ResolvedField.EmptyValue<double>(),
        ResolvedField.Empty<MusicalKey>(),
        ResolvedField.EmptyValue<int>(),
        new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));
}
