using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Musical-key detection via Essentia. By default reads the EDMA (Electronic Dance Music
/// Algorithm) profile, which is trained on Beatport material and consistently outperforms
/// Shaath/libKeyFinder on DJ-oriented tracks. The 1795-track backtest from 2026-05-23 showed
/// EDMA-only managing 37 % exact key match across a mixed library — better selection strategies
/// are available via <see cref="KeyAnalyzerOptions.Profile"/>: ensemble-voting picks the key two
/// of {EDMA, Temperley, Krumhansl} agree on, and falls back to the strongest single profile
/// when no two agree.
/// </summary>
/// <remarks>
/// Essentia's music extractor always emits all three key profiles in parallel for free, so the
/// per-profile reads cost nothing at scan time — the strategy lives entirely in this analyzer.
/// Standard notation is rebuilt as <c>{key}{m if minor, empty if major}</c> and fed through
/// <see cref="KeyNotationConverter"/> so both notations end up populated on the downstream
/// <see cref="MusicalKey"/>.
/// </remarks>
public sealed class EssentiaKeyAnalyzer : IKeyAnalyzer
{
    public const string ProviderName = EssentiaAnalysisService.ProviderName;

    private readonly IEssentiaAnalysisService _service;
    private readonly KeyAnalyzerOptions _options;
    private readonly ILogger<EssentiaKeyAnalyzer> _logger;

    public EssentiaKeyAnalyzer(
        IEssentiaAnalysisService service,
        KeyAnalyzerOptions options,
        ILogger<EssentiaKeyAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _service = service;
        _options = options;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<KeyResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = await _service.RunAsync(file, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new KeyResult(Key: null, Confidence: 0);
        }

        var picked = PickKey(result);
        if (picked is null) return new KeyResult(Key: null, Confidence: 0);

        var standard = BuildStandardNotation(picked.Value.Key, picked.Value.Scale);
        var key = KeyNotationConverter.FromEither(standard, camelot: null);
        if (key is null)
        {
            _logger.LogWarning(
                "Essentia emitted unrecognised key '{Key}' scale '{Scale}' (profile={Profile}) for {Path}",
                picked.Value.Key, picked.Value.Scale, _options.Profile, file.Path);
            return new KeyResult(Key: null, Confidence: 0);
        }

        var confidence = Math.Clamp(picked.Value.Strength, 0, 1);
        _logger.LogDebug(
            "Essentia key={Key} strength={Strength:F2} (selection={Profile}) for {Path}",
            key.Standard, confidence, _options.Profile, file.Path);

        return new KeyResult(key, confidence);
    }

    /// <summary>
    /// Picks one (key, scale, strength) triple from the up-to-three profiles Essentia reported,
    /// honouring <see cref="KeyAnalyzerOptions.Profile"/>. Returns <c>null</c> only when every
    /// applicable profile is missing — the caller treats that as "no key detected".
    /// </summary>
    private (string Key, string Scale, double Strength)? PickKey(EssentiaResult r)
    {
        var edma = TryReadProfile(r.KeyKey, r.KeyScale, r.KeyStrength);
        var temp = TryReadProfile(r.KeyTemperleyKey, r.KeyTemperleyScale, r.KeyTemperleyStrength);
        var krum = TryReadProfile(r.KeyKrumhanslKey, r.KeyKrumhanslScale, r.KeyKrumhanslStrength);

        return _options.Profile switch
        {
            KeyProfileSelection.Edma => edma,
            KeyProfileSelection.Temperley => temp,
            KeyProfileSelection.Krumhansl => krum,
            KeyProfileSelection.EdmaWithFallback => EdmaWithFallback(edma, temp, krum),
            KeyProfileSelection.EnsembleVoting => EnsembleVote(edma, temp, krum),
            _ => edma,
        };
    }

    private (string Key, string Scale, double Strength)? EdmaWithFallback(
        (string Key, string Scale, double Strength)? edma,
        (string Key, string Scale, double Strength)? temp,
        (string Key, string Scale, double Strength)? krum)
    {
        // EDMA stays in charge while its strength clears the floor — most of the EDM library
        // is in that regime. Below the floor, hand off to whichever of the other two reports
        // the highest strength (typically Temperley on rock/hip-hop, Krumhansl on classical).
        if (edma is { } e && e.Strength >= _options.EdmaWithFallbackThreshold)
        {
            return e;
        }
        var fallback = HighestStrength(temp, krum);
        return fallback ?? edma;
    }

    private static (string Key, string Scale, double Strength)? EnsembleVote(
        (string Key, string Scale, double Strength)? edma,
        (string Key, string Scale, double Strength)? temp,
        (string Key, string Scale, double Strength)? krum)
    {
        // Vote by canonical-form equality across the three profiles. When two agree, the third
        // is treated as an outlier and the agreeing pair wins; confidence is the average of the
        // agreeing strengths. No agreement → fall through to the single highest-strength
        // profile so the analyzer still emits something useful.
        var votes = new List<(string Key, string Scale, double Strength)>(3);
        if (edma is { } e) votes.Add(e);
        if (temp is { } t) votes.Add(t);
        if (krum is { } k) votes.Add(k);
        if (votes.Count == 0) return null;
        if (votes.Count == 1) return votes[0];

        // Look for any pair (or triple) with matching (Key, Scale, IgnoreCase).
        for (var i = 0; i < votes.Count; i++)
        {
            var matches = new List<(string Key, string Scale, double Strength)> { votes[i] };
            for (var j = i + 1; j < votes.Count; j++)
            {
                if (KeyMatches(votes[i], votes[j]))
                {
                    matches.Add(votes[j]);
                }
            }
            if (matches.Count >= 2)
            {
                var avgStrength = matches.Average(m => m.Strength);
                return (votes[i].Key, votes[i].Scale, avgStrength);
            }
        }

        // No pair agreed — fall back to the single profile with the highest reported strength.
        // That keys the output to "the most self-confident interpretation we have".
        return votes.OrderByDescending(v => v.Strength).First();
    }

    private static bool KeyMatches(
        (string Key, string Scale, double Strength) a,
        (string Key, string Scale, double Strength) b) =>
        string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Scale, b.Scale, StringComparison.OrdinalIgnoreCase);

    private static (string Key, string Scale, double Strength)? HighestStrength(
        (string Key, string Scale, double Strength)? a,
        (string Key, string Scale, double Strength)? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value.Strength >= b.Value.Strength ? a : b;
    }

    private static (string Key, string Scale, double Strength)? TryReadProfile(string? key, string? scale, double? strength)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(scale))
        {
            return null;
        }
        return (key, scale, strength ?? 0);
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
