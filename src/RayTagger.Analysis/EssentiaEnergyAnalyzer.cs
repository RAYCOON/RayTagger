using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Energy-level detection via Essentia. Pulls <c>lowlevel.spectral_energy.mean</c> from a single
/// shared Essentia run and quantises it to the DJ-tool-standard 1–10 bucket via a logarithmic
/// mapping over an empirically calibrated range.
/// </summary>
/// <remarks>
/// Why log-scaled: spectral energy is approximately log-perceived (a doubling of measured RMS
/// corresponds to a roughly constant perceived loudness step). A linear mapping would bunch
/// quiet tracks into one bucket and spread loud tracks across many.
/// <para/>
/// The reference range <c>0.001..0.2</c> is empirically calibrated against EDM material and is
/// the primary tuning knob if 1–10 buckets feel off for a given library. Confidence is fixed at
/// 0.8 because frame-aggregated <c>mean</c> over thousands of windows is statistically robust,
/// but the *mapping to a 1–10 perceived energy* remains a heuristic — hence not 1.0.
/// </remarks>
public sealed class EssentiaEnergyAnalyzer : IEnergyAnalyzer
{
    public const string ProviderName = EssentiaAnalysisService.ProviderName;

    // Calibration range — adjust if needed for genres other than EDM. log10(0.001) = -3,
    // log10(0.2) ≈ -0.7. Any spectral_energy outside the range clamps to 1 or 10.
    private const double EnergyFloor = 0.001;
    private const double EnergyCeiling = 0.2;
    private const double Confidence = 0.8;

    private readonly IEssentiaAnalysisService _service;
    private readonly ILogger<EssentiaEnergyAnalyzer> _logger;

    public EssentiaEnergyAnalyzer(IEssentiaAnalysisService service, ILogger<EssentiaEnergyAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);

        _service = service;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<EnergyResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = await _service.RunAsync(file, cancellationToken).ConfigureAwait(false);
        if (result?.SpectralEnergy is null || result.SpectralEnergy <= 0)
        {
            return new EnergyResult(Energy: null, Confidence: 0);
        }

        var bucket = MapToBucket(result.SpectralEnergy.Value);
        _logger.LogDebug(
            "Essentia spectral_energy={Raw:F4} → energy bucket {Bucket} for {Path}",
            result.SpectralEnergy.Value, bucket, file.Path);

        return new EnergyResult(bucket, Confidence);
    }

    internal static int MapToBucket(double spectralEnergy)
    {
        var clamped = Math.Clamp(spectralEnergy, EnergyFloor, EnergyCeiling);
        var logE = Math.Log10(clamped);
        var ratio = (logE - Math.Log10(EnergyFloor)) / (Math.Log10(EnergyCeiling) - Math.Log10(EnergyFloor));
        var bucket = (int)Math.Round(1 + 9 * ratio, MidpointRounding.AwayFromZero);
        return Math.Clamp(bucket, 1, 10);
    }
}
