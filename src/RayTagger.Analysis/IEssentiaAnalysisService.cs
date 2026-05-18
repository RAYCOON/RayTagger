using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Single Essentia run per track, results shared across the BPM / Key / Energy analyzers.
/// Forking <c>essentia_streaming_extractor_music</c> three times per file would triple the
/// I/O and CPU cost for no benefit — the JSON output already contains all three dimensions,
/// so we run it once and cache.
/// </summary>
public interface IEssentiaAnalysisService
{
    /// <summary>
    /// Runs Essentia for <paramref name="file"/> (if not already cached) and returns the parsed
    /// JSON output, or <c>null</c> when the binary is missing or the run failed unrecoverably.
    /// Per-file failures must not abort the scan — callers receive a <c>null</c> result.
    /// </summary>
    Task<EssentiaResult?> RunAsync(TrackFile file, CancellationToken cancellationToken = default);
}
