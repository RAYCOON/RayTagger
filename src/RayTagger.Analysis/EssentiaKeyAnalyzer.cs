using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Musical-key detection via Essentia using the EDMA (Electronic Dance Music Algorithm) profile.
/// EDMA is trained on Beatport material and consistently outperforms the Shaath profile (which
/// libKeyFinder / keyfinder-cli use) on DJ-oriented tracks. <c>tonal.key_edma.strength</c> is the
/// confidence in [0,1] — keyfinder-cli had no such signal.
/// </summary>
/// <remarks>
/// The Essentia music extractor always emits all three profiles (Temperley, Krumhansl, EDMA);
/// we only read EDMA. Standard notation is rebuilt as <c>{key}{m if minor, empty if major}</c>
/// and fed through <see cref="KeyNotationConverter"/> so both notations end up populated on the
/// downstream <see cref="MusicalKey"/>.
/// </remarks>
public sealed class EssentiaKeyAnalyzer : IKeyAnalyzer
{
    public const string ProviderName = EssentiaAnalysisService.ProviderName;

    private readonly IEssentiaAnalysisService _service;
    private readonly ILogger<EssentiaKeyAnalyzer> _logger;

    public EssentiaKeyAnalyzer(IEssentiaAnalysisService service, ILogger<EssentiaKeyAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);

        _service = service;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<KeyResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = await _service.RunAsync(file, cancellationToken).ConfigureAwait(false);
        if (result is null || string.IsNullOrWhiteSpace(result.KeyKey) || string.IsNullOrWhiteSpace(result.KeyScale))
        {
            return new KeyResult(Key: null, Confidence: 0);
        }

        var standard = BuildStandardNotation(result.KeyKey, result.KeyScale);
        var key = KeyNotationConverter.FromEither(standard, camelot: null);
        if (key is null)
        {
            _logger.LogWarning(
                "Essentia emitted unrecognised EDMA key '{Key}' scale '{Scale}' for {Path}",
                result.KeyKey, result.KeyScale, file.Path);
            return new KeyResult(Key: null, Confidence: 0);
        }

        var confidence = Math.Clamp(result.KeyStrength ?? 0, 0, 1);
        _logger.LogDebug(
            "Essentia key={Key} strength={Strength:F2} for {Path}",
            key.Standard, confidence, file.Path);

        return new KeyResult(key, confidence);
    }

    private static string BuildStandardNotation(string essentiaKey, string essentiaScale)
    {
        // Essentia emits things like ("A", "minor"). Standard DJ notation is "Am" (minor) or "A"
        // (major). Anything other than "minor" is treated as major — Essentia only ever emits
        // these two values, but be defensive.
        var trimmedKey = essentiaKey.Trim();
        return essentiaScale.Trim().Equals("minor", StringComparison.OrdinalIgnoreCase)
            ? trimmedKey + "m"
            : trimmedKey;
    }
}
