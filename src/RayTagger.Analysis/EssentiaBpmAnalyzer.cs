using Microsoft.Extensions.Logging;
using RayTagger.Core.Analysis;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// BPM detection via Essentia. Reads <c>rhythm.bpm</c> from a single shared Essentia run with
/// <c>rhythm.bpm_histogram_first_peak_weight</c> as the confidence signal (Essentia's documented
/// proxy for tempo reliability), then applies a per-genre fold algorithm to bring half/double-time
/// detections back into the configured range.
/// </summary>
/// <remarks>
/// Essentia itself always runs with its built-in default range — the per-genre hint is consulted
/// AFTER detection. The fold algorithm:
/// <list type="number">
///   <item>No range configured → return <c>snap(raw)</c>? No: leave snapping to the pipeline-level
///         pass that uniformly cleans every source (existing/lookup/rules). Return raw verbatim.</item>
///   <item>Raw ∈ [Min, Max] → return <c>snap(raw)</c>; <see cref="BpmResult.WasSnapped"/> reflects
///         whether the snap actually changed the value.</item>
///   <item>Raw &lt; Min → fold via <c>raw × 2</c>; raw &gt; Max → fold via <c>raw / 2</c>;
///         apply snap; if the result is now in range, return it (no forced-fallback marker).</item>
///   <item>Folded-and-snapped value still out of range → return <c>snap(raw)</c> and set
///         <see cref="BpmResult.IsForcedFallback"/>. The UI renders the cell dark-blue so the
///         user sees that the genre-range disagreement was unresolvable.</item>
/// </list>
/// </remarks>
public sealed class EssentiaBpmAnalyzer : IBpmAnalyzer
{
    public const string ProviderName = EssentiaAnalysisService.ProviderName;

    private readonly IEssentiaAnalysisService _service;
    private readonly ILogger<EssentiaBpmAnalyzer> _logger;
    private readonly double _snapTolerancePercent;
    private readonly double _snapStep;

    public EssentiaBpmAnalyzer(
        IEssentiaAnalysisService service,
        ILogger<EssentiaBpmAnalyzer> logger,
        BpmAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);

        _service = service;
        _logger = logger;
        // Snap params come from BpmAnalyzerOptions when available; tests that construct the
        // analyzer directly can omit them, in which case the defaults match the option POCO's
        // defaults so the analyzer behaves identically to a fully-wired production run.
        _snapTolerancePercent = options?.SnapTolerancePercent ?? 0.12;
        _snapStep = options?.SnapStep ?? 0.5;
    }

    public string Name => ProviderName;

    public async Task<BpmResult> AnalyzeAsync(
        TrackFile file,
        BpmTempoRange? tempoRange = null,
        CancellationToken cancellationToken = default)
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
        var raw = result.Bpm.Value;

        // Delegate the fold + snap to BpmFolder so TagPipeline can apply the same rules with a
        // post-lookup genre range (Sprint 5 / #15). The folder returns raw+confidence verbatim
        // when no range is configured — matches the original "leave the pipeline-level snap to
        // tidy up" behaviour.
        var folded = BpmFolder.Apply(raw, confidence, tempoRange, _snapTolerancePercent, _snapStep);
        _logger.LogDebug(
            "Essentia BPM={Raw:F2} confidence={Confidence:F2} range={Range} → {Resolved:F2} " +
            "(snapped={Snapped} forced-fallback={Fallback}) for {Path}",
            raw, confidence, tempoRange, folded.Bpm, folded.WasSnapped, folded.IsForcedFallback, file.Path);
        return folded;
    }
}
