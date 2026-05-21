using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Energy-level detection via Essentia. Combines five perceptual / rhythmic descriptors from a
/// single Essentia run into one composite score, then quantises that score to the DJ-tool-standard
/// 1–10 bucket.
/// </summary>
/// <remarks>
/// <para><b>Why not just <c>spectral_energy.mean</c>:</b> raw spectral RMS is a poor proxy for
/// perceived energy. It is replay-gain dependent, rhythm-blind, and on a typical DJ library
/// (deep house → DnB → techno → gqom) the values cluster so tightly that every track falls into
/// buckets 6–9. A chillout DnB track and a peak-time gqom banger end up indistinguishable.</para>
///
/// <para><b>The five-feature composite:</b></para>
/// <list type="table">
///   <listheader><term>Feature</term><description>What it captures · weight</description></listheader>
///   <item><term><c>spectral_flux</c></term><description>Frame-to-frame spectral change.
///     Direct correlate of arousal — Eerola/Schubert MIR work attributes ~65% of arousal variance
///     to flux + entropy combined. · <b>0.35</b></description></item>
///   <item><term><c>beats_loudness</c></term><description>Spectral energy at detected beat
///     positions. Captures kick-drum punch — the single most predictive feature for
///     dancefloor-energy specifically (as opposed to general arousal). · <b>0.25</b></description></item>
///   <item><term><c>onset_rate</c></term><description>Onsets per second. Event density;
///     distinguishes a sparse atmospheric track from a busy peak-time tool at the same BPM.
///     · <b>0.15</b></description></item>
///   <item><term><c>danceability</c></term><description>Essentia's own holistic [0..3] estimator.
///     Hedges against pathological cases that score high on the spectral features but feel
///     "off the floor" (e.g. dense ambient drones). · <b>0.15</b></description></item>
///   <item><term><c>average_loudness</c></term><description>Replay-gain-normalised perceptual
///     loudness in [0,1]. Lowest weight because it saturates on modern mastered material —
///     useful mainly for distinguishing well-mastered tracks from rough demos. · <b>0.10</b></description></item>
/// </list>
///
/// <para><b>Per-feature normalisation</b> uses (floor, ceiling) anchors. The built-in defaults
/// were tuned against a 35-track reference library spanning ambient → DnB → techno → gqom.
/// A library-specific profile (<see cref="EnergyCalibrationProfile"/>, written by
/// <c>tagger calibrate-energy</c>) overrides those defaults to maximise bucket spread on
/// non-generic libraries (classical, jazz, podcast, …).</para>
///
/// <para><b>Graceful degradation:</b> when some Essentia features are missing (older extractor
/// build, malformed JSON), the composite renormalises over the available subset. If too few
/// features are present (weight coverage &lt; 40%), the analyzer returns no value rather than
/// emit a misleading bucket. Confidence scales linearly with feature coverage.</para>
/// </remarks>
public sealed class EssentiaEnergyAnalyzer : IEnergyAnalyzer
{
    public const string ProviderName = EssentiaAnalysisService.ProviderName;

    // Built-in default anchors — see class remarks. Used when no calibration profile is provided.
    internal static readonly FeatureRange DefaultFlux = new(0.050, 0.150);
    internal static readonly FeatureRange DefaultBeatsLoudness = new(0.020, 0.250);
    internal static readonly FeatureRange DefaultOnsetRate = new(3.0, 7.5);
    internal static readonly FeatureRange DefaultDanceability = new(1.0, 2.5);
    internal static readonly FeatureRange DefaultAverageLoudness = new(0.50, 1.00);

    // Component weights — must sum to 1.0. See class remarks for the rationale per feature.
    private const double WFlux = 0.35;
    private const double WBeats = 0.25;
    private const double WOnset = 0.15;
    private const double WDance = 0.15;
    private const double WLoudness = 0.10;

    // Drop the result rather than emit a low-confidence bucket below this weight coverage.
    private const double MinFeatureCoverage = 0.40;

    // Confidence at full feature coverage. Scales linearly with coverage; the underlying
    // descriptors are statistically robust (mean-aggregated over thousands of frames). The
    // mapping to a perceptual 1–10 scale remains heuristic, which is why it isn't 1.0.
    private const double MaxConfidence = 0.80;

    private readonly IEssentiaAnalysisService _service;
    private readonly ILogger<EssentiaEnergyAnalyzer> _logger;
    private readonly CompositeRanges _ranges;

    public EssentiaEnergyAnalyzer(
        IEssentiaAnalysisService service,
        ILogger<EssentiaEnergyAnalyzer> logger,
        EnergyCalibrationProfile? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);

        _service = service;
        _logger = logger;
        _ranges = CompositeRanges.From(calibration);

        if (calibration is not null)
        {
            _logger.LogInformation(
                "Energy calibration profile loaded ({SampleCount} tracks, {GeneratedAt:yyyy-MM-dd}): " +
                "flux=[{FluxFloor:F3},{FluxCeiling:F3}] beats=[{BeatsFloor:F3},{BeatsCeiling:F3}] " +
                "onset=[{OnsetFloor:F2},{OnsetCeiling:F2}] dance=[{DanceFloor:F2},{DanceCeiling:F2}] " +
                "aloud=[{LoudFloor:F2},{LoudCeiling:F2}]",
                calibration.SampleCount, calibration.GeneratedAt,
                _ranges.Flux.Floor, _ranges.Flux.Ceiling,
                _ranges.Beats.Floor, _ranges.Beats.Ceiling,
                _ranges.Onset.Floor, _ranges.Onset.Ceiling,
                _ranges.Dance.Floor, _ranges.Dance.Ceiling,
                _ranges.Loudness.Floor, _ranges.Loudness.Ceiling);
        }
    }

    public string Name => ProviderName;

    public async Task<EnergyResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = await _service.RunAsync(file, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new EnergyResult(Energy: null, Confidence: 0);
        }

        var (bucket, confidence) = Compose(result, _ranges);
        if (bucket is null)
        {
            return new EnergyResult(Energy: null, Confidence: 0);
        }

        _logger.LogDebug(
            "Essentia energy composite for {Path}: flux={Flux:F3} beats={Beats:F3} onset={Onset:F2} dance={Dance:F2} aloud={Loud:F3} → bucket {Bucket} (conf {Conf:F2})",
            file.Path,
            result.SpectralFlux ?? double.NaN,
            result.BeatsLoudness ?? double.NaN,
            result.OnsetRate ?? double.NaN,
            result.Danceability ?? double.NaN,
            result.AverageLoudness ?? double.NaN,
            bucket.Value,
            confidence);

        return new EnergyResult(bucket, confidence);
    }

    /// <summary>
    /// Computes the composite bucket (1–10) and its confidence from an Essentia result using
    /// the built-in default ranges. Internal entry point for unit testing the math.
    /// </summary>
    internal static (int? Bucket, double Confidence) Compose(EssentiaResult r) =>
        Compose(r, CompositeRanges.Defaults);

    internal static (int? Bucket, double Confidence) Compose(EssentiaResult r, CompositeRanges ranges)
    {
        ArgumentNullException.ThrowIfNull(r);

        ReadOnlySpan<(double? Value, double Weight)> terms =
        [
            (ranges.Flux.Normalize(r.SpectralFlux),       WFlux),
            (ranges.Beats.Normalize(r.BeatsLoudness),     WBeats),
            (ranges.Onset.Normalize(r.OnsetRate),         WOnset),
            (ranges.Dance.Normalize(r.Danceability),      WDance),
            (ranges.Loudness.Normalize(r.AverageLoudness),WLoudness),
        ];

        double weightedSum = 0;
        double availableWeight = 0;
        foreach (var (value, weight) in terms)
        {
            if (value is null) continue;
            weightedSum += value.Value * weight;
            availableWeight += weight;
        }

        if (availableWeight < MinFeatureCoverage)
        {
            return (null, 0);
        }

        var composite = weightedSum / availableWeight;
        var bucket = Math.Clamp(
            (int)Math.Round(1 + 9 * composite, MidpointRounding.AwayFromZero),
            1, 10);
        var confidence = MaxConfidence * availableWeight;
        return (bucket, confidence);
    }

    internal readonly record struct FeatureRange(double Floor, double Ceiling)
    {
        public double? Normalize(double? raw)
        {
            if (raw is null) return null;
            // Degenerate range (Floor >= Ceiling) → treat all values as 0.5 (midpoint) rather
            // than divide-by-zero. Defensive against malformed profiles in tagger.yaml.
            if (Ceiling - Floor <= 1e-9) return 0.5;
            var clamped = Math.Clamp(raw.Value, Floor, Ceiling);
            return (clamped - Floor) / (Ceiling - Floor);
        }

        public static FeatureRange FromAnchor(FeatureAnchor? anchor, FeatureRange fallback) =>
            anchor is null ? fallback : new(anchor.Floor, anchor.Ceiling);
    }

    internal sealed record CompositeRanges(
        FeatureRange Flux,
        FeatureRange Beats,
        FeatureRange Onset,
        FeatureRange Dance,
        FeatureRange Loudness)
    {
        public static CompositeRanges Defaults { get; } = new(
            DefaultFlux, DefaultBeatsLoudness, DefaultOnsetRate, DefaultDanceability, DefaultAverageLoudness);

        public static CompositeRanges From(EnergyCalibrationProfile? profile)
        {
            if (profile is null) return Defaults;
            return new CompositeRanges(
                FeatureRange.FromAnchor(profile.SpectralFlux,     DefaultFlux),
                FeatureRange.FromAnchor(profile.BeatsLoudness,    DefaultBeatsLoudness),
                FeatureRange.FromAnchor(profile.OnsetRate,        DefaultOnsetRate),
                FeatureRange.FromAnchor(profile.Danceability,     DefaultDanceability),
                FeatureRange.FromAnchor(profile.AverageLoudness,  DefaultAverageLoudness));
        }
    }
}
