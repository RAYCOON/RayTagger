namespace RayTagger.Core.Models;

/// <summary>
/// Where a resolved tag value originated. The write stage consults this together with
/// per-dimension <c>existing_confidence</c> from tagger.yaml — see docs/ARCHITECTURE.md §6.2.
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
/// write stage can apply per-dimension <c>existing_confidence</c> correctly.
/// </summary>
public sealed record ResolvedTrackTags(
    ResolvedField<string> Genre,
    ResolvedField<string> SubGenre,
    ResolvedValueField<double> Bpm,
    ResolvedField<MusicalKey> Key,
    ResolvedValueField<int> Energy,
    ResolvedField<string> Mood,
    ResolvedField<string> SetPosition,
    IReadOnlyDictionary<string, ResolvedField<string>> Custom)
{
    /// <summary>
    /// Optional audit trail of the taxonomy-aware genre resolver — one entry per inspected API
    /// candidate. Populated by <see cref="Pipeline.TagMerger"/> when the resolver runs (i.e.
    /// <c>lookup.taxonomy_resolution: true</c> AND a non-empty <c>LookupResult</c> is present).
    /// Consumed by the UI's "Regeln"-popup and the CLI scan report; null otherwise.
    /// </summary>
    public IReadOnlyList<Mapping.CandidateTraceEntry>? GenreLookupTrace { get; init; }

    /// <summary>
    /// Optional raw per-provider trace from the lookup runner — one entry per provider that was
    /// inspected during the run (Ok / NoHit / Skipped / Failed). Independent of the resolver's
    /// trace: shows the API's raw responses, not the taxonomy-filtered candidates. Null when
    /// the run used a cached aggregate (cache hit doesn't replay providers) or when lookup was
    /// disabled.
    /// </summary>
    public IReadOnlyList<ProviderTraceEntry>? ProviderTrace { get; init; }

    /// <summary>
    /// Optional per-classifier trace from the audio-based genre classification stage. One entry
    /// per enabled classifier (Ok / NoHit / Disabled / Failed). Null when no classifier is
    /// configured (the no-op runner is in place) or when the stage was skipped entirely.
    /// Diagnostic-only — the merger never reads this.
    /// </summary>
    public IReadOnlyList<Pipeline.ClassifierTraceEntry>? ClassifierTrace { get; init; }

    public static ResolvedTrackTags Empty { get; } = new(
        ResolvedField.Empty<string>(),
        ResolvedField.Empty<string>(),
        ResolvedField.EmptyValue<double>(),
        ResolvedField.Empty<MusicalKey>(),
        ResolvedField.EmptyValue<int>(),
        ResolvedField.Empty<string>(),
        ResolvedField.Empty<string>(),
        new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));
}
