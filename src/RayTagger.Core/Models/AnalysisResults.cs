namespace RayTagger.Core.Models;

/// <summary>
/// BPM analysis output. <see cref="Confidence"/> in [0,1] — the write stage compares against
/// <c>analysis.bpm.min_confidence</c> in tagger.yaml and preserves the existing tag if below.
/// </summary>
/// <param name="Bpm">Final BPM (after the analyzer's fold + snap), or <c>null</c> when detection failed.</param>
/// <param name="Confidence">Essentia's <c>bpm_histogram_first_peak_weight</c>, clamped to [0,1].</param>
/// <param name="WasSnapped">
/// True when the analyzer's snap-to-grid changed the value (Essentia drift like 122.07 → 122.0).
/// Combined with the pipeline-level snap to drive the UI's dark-red highlight.
/// </param>
/// <param name="IsForcedFallback">
/// True when the genre-range fold (raw &lt; Min → ×2, raw &gt; Max → ÷2, then snap) failed to
/// bring the value back into the configured genre range. In that case the analyzer falls back
/// to <c>snap(raw)</c> and signals the UI to highlight the cell dark-blue — the BPM is the user's
/// best raw signal, but the configured genre range disagrees with it.
/// </param>
public sealed record BpmResult(
    double? Bpm,
    double Confidence,
    bool WasSnapped = false,
    bool IsForcedFallback = false);

public sealed record KeyResult(MusicalKey? Key, double Confidence);

public sealed record EnergyResult(int? Energy, double Confidence);

public sealed record FingerprintResult(string? Chromaprint, double Confidence);

/// <summary>
/// Aggregate of the four analyzer outputs for one track plus any MusicBrainz ID returned by an
/// AcoustID lookup that was performed during analysis (kept here so the lookup stage can skip the
/// extra HTTP call when the fingerprint analyzer already resolved an MBID).
/// </summary>
public sealed record AnalysisResult(
    BpmResult Bpm,
    KeyResult Key,
    EnergyResult Energy,
    FingerprintResult Fingerprint,
    Guid? AcoustIdMbid)
{
    public static AnalysisResult Empty { get; } = new(
        new BpmResult(null, 0),
        new KeyResult(null, 0),
        new EnergyResult(null, 0),
        new FingerprintResult(null, 0),
        AcoustIdMbid: null);
}
