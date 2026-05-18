namespace RayTagger.Core.Models;

/// <summary>
/// BPM analysis output. <see cref="Confidence"/> in [0,1] — the write stage compares against
/// <c>analysis.bpm.min_confidence</c> in tagger.yaml and preserves the existing tag if below.
/// </summary>
public sealed record BpmResult(double? Bpm, double Confidence);

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
