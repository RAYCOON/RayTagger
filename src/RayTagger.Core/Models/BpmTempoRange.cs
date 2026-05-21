namespace RayTagger.Core.Models;

/// <summary>
/// Per-genre BPM hint: the plausible tempo interval for a genre. Resolved from
/// <c>analysis.bpm.tempo_ranges_by_genre</c> via the track's normalised genre.
/// </summary>
/// <remarks>
/// Both <see cref="Min"/> and <see cref="Max"/> must be set together — a range without bounds
/// would carry no useful signal. Essentia is always invoked with its default range; the BPM
/// analyzer consults this hint AFTER detection to fold half/double-time errors back into the
/// genre's plausible interval (raw &lt; Min → raw × 2; raw &gt; Max → raw / 2) and snap the result.
/// <para/>
/// <b>Class with settable properties</b> rather than a positional record so YamlDotNet can
/// deserialise it from the inline mapping form (<c>{ min: 110, max: 140 }</c>) and equality
/// works structurally for cache-key and resolver-test purposes.
/// </remarks>
public sealed class BpmTempoRange : IEquatable<BpmTempoRange>
{
    public double? Min { get; set; }
    public double? Max { get; set; }

    public BpmTempoRange() { }
    public BpmTempoRange(double min, double max) { Min = min; Max = max; }

    /// <summary>True when both <see cref="Min"/> and <see cref="Max"/> carry values.</summary>
    public bool HasRange => Min.HasValue && Max.HasValue;

    /// <summary>
    /// Inclusive bounds check used by the BPM fold algorithm. Returns false when the range itself
    /// is not fully specified — a half-configured range can't say whether a value belongs.
    /// </summary>
    public bool Contains(double bpm) => HasRange && bpm >= Min!.Value && bpm <= Max!.Value;

    /// <summary>
    /// Stable signature, locale-invariant. Kept for diagnostic logging — Essentia no longer
    /// receives a per-range profile, so this no longer feeds into the analysis cache key.
    /// </summary>
    public string Signature => HasRange
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Min!.Value:0.##}-{Max!.Value:0.##}")
        : "default";

    public bool Equals(BpmTempoRange? other) =>
        other is not null
        && Nullable.Equals(Min, other.Min)
        && Nullable.Equals(Max, other.Max);
    public override bool Equals(object? obj) => Equals(obj as BpmTempoRange);
    public override int GetHashCode() => HashCode.Combine(Min, Max);
    public override string ToString() => HasRange ? $"[{Min}, {Max}]" : "no-range";
}
