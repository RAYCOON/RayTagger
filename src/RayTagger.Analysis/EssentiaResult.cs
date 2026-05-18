namespace RayTagger.Analysis;

/// <summary>
/// Subset of <c>essentia_streaming_extractor_music</c>'s JSON output that Tagger consumes. One
/// instance per analysed track, produced by <see cref="IEssentiaAnalysisService"/> and read by
/// the BPM / Key / Energy analyzers. Any field can be <c>null</c> when the underlying descriptor
/// was missing or non-numeric — analyzers must treat that as "no value detected".
/// </summary>
public sealed record EssentiaResult(
    double? Bpm,
    double? BpmConfidence,
    string? KeyKey,
    string? KeyScale,
    double? KeyStrength,
    double? SpectralEnergy);
