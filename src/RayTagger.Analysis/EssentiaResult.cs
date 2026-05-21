namespace RayTagger.Analysis;

/// <summary>
/// Subset of <c>essentia_streaming_extractor_music</c>'s JSON output that Tagger consumes. One
/// instance per analysed track, produced by <see cref="IEssentiaAnalysisService"/> and read by
/// the BPM / Key / Energy analyzers. Any field can be <c>null</c> when the underlying descriptor
/// was missing or non-numeric — analyzers must treat that as "no value detected".
/// </summary>
/// <remarks>
/// The energy fields (<see cref="AverageLoudness"/>, <see cref="SpectralFlux"/>,
/// <see cref="OnsetRate"/>, <see cref="Danceability"/>, <see cref="BeatsLoudness"/>) feed the
/// multi-feature composite in <see cref="EssentiaEnergyAnalyzer"/>. <see cref="SpectralEnergy"/>
/// is retained for diagnostic logging — it is no longer the primary energy signal because raw
/// spectral RMS does not differentiate well across a DJ library (replay-gain dependent, rhythm
/// blind). See the analyzer for the rationale.
/// </remarks>
public sealed record EssentiaResult(
    double? Bpm,
    double? BpmConfidence,
    string? KeyKey,
    string? KeyScale,
    double? KeyStrength,
    double? SpectralEnergy,
    double? AverageLoudness,
    double? SpectralFlux,
    double? OnsetRate,
    double? Danceability,
    double? BeatsLoudness);
