using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// BPM detection via Essentia. Pulls <c>rhythm.bpm</c> from a single shared Essentia run and uses
/// <c>rhythm.bpm_histogram_first_peak_weight</c> as the confidence signal — Essentia's documented
/// proxy for tempo reliability (the proportion of beats locked to the dominant tempo).
/// </summary>
/// <remarks>
/// Replaces the earlier aubio integration. Essentia's tempo tracker is more robust against the
/// half/double-time errors aubio is known for on EDM material, and crucially exposes a real
/// confidence value where aubio gives none. The actual fork+exec happens in
/// <see cref="IEssentiaAnalysisService"/> — this analyzer just reads its field.
/// </remarks>
public sealed class EssentiaBpmAnalyzer : IBpmAnalyzer
{
    public const string ProviderName = EssentiaAnalysisService.ProviderName;

    private readonly IEssentiaAnalysisService _service;
    private readonly ILogger<EssentiaBpmAnalyzer> _logger;

    public EssentiaBpmAnalyzer(IEssentiaAnalysisService service, ILogger<EssentiaBpmAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);

        _service = service;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<BpmResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = await _service.RunAsync(file, cancellationToken).ConfigureAwait(false);
        if (result?.Bpm is null)
        {
            return new BpmResult(Bpm: null, Confidence: 0);
        }

        // bpm_histogram_first_peak_weight is in [0,1]. Missing → 0 (caller's min_confidence
        // gate will then drop the value if the threshold is non-zero).
        var confidence = Math.Clamp(result.BpmConfidence ?? 0, 0, 1);
        _logger.LogDebug(
            "Essentia BPM={Bpm} confidence={Confidence:F2} for {Path}",
            result.Bpm, confidence, file.Path);

        return new BpmResult(result.Bpm, confidence);
    }
}
