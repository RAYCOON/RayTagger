using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Default <see cref="ITempoRangeResolver"/>. Stateless after construction — the relevant slices
/// of <see cref="BpmAnalyzerOptions"/> + taxonomy alias table are captured at build time and
/// applied per track. Resolution is one dictionary lookup per call; no allocation on the hot path.
/// </summary>
/// <remarks>
/// Case-insensitive matching on the configured range keys is guaranteed by
/// <c>TaggerOptionsLoader.NormaliseDictionaryComparers</c>, which rebuilds the map with
/// <see cref="StringComparer.OrdinalIgnoreCase"/> after YamlDotNet has deserialised it (the
/// default deserialiser drops the POCO's pre-set comparer).
/// </remarks>
public sealed class TempoRangeResolver : ITempoRangeResolver
{
    // Concrete Dictionary rather than IReadOnlyDictionary so the JIT can devirtualise TryGetValue
    // on the hot path (one call per track); see CA1859.
    private readonly Dictionary<string, BpmTempoRange> _rangesByGenre;
    private readonly BpmTempoRange? _fallback;
    private readonly IReadOnlyDictionary<string, (string Genre, string Subgenre)> _aliasTable;
    private readonly ILogger<TempoRangeResolver> _logger;

    public TempoRangeResolver(BpmAnalyzerOptions bpmOptions, Taxonomy taxonomy, ILogger<TempoRangeResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(bpmOptions);
        ArgumentNullException.ThrowIfNull(taxonomy);
        ArgumentNullException.ThrowIfNull(logger);
        _rangesByGenre = bpmOptions.TempoRangesByGenre;
        _fallback = bpmOptions.TempoRangeFallback;
        _aliasTable = taxonomy.NormaliseByAlias;
        _logger = logger;
    }

    public BpmTempoRange? Resolve(TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        // Short-circuit when neither per-genre ranges nor a fallback are configured — the resolver
        // is effectively disabled and should never override Essentia's default.
        if (_rangesByGenre.Count == 0 && _fallback is null)
        {
            return null;
        }

        var rawGenre = existing.Genre?.Trim();
        if (string.IsNullOrEmpty(rawGenre))
        {
            return ReturnFallback("no genre tag");
        }

        var normalised = NormaliseGenre(rawGenre);

        if (_rangesByGenre.TryGetValue(normalised, out var range))
        {
            _logger.LogTrace(
                "Tempo range resolved: genre='{Raw}' → normalised='{Normalised}' → range [{Min}, {Max}]",
                rawGenre, normalised, range.Min, range.Max);
            return range;
        }

        return ReturnFallback($"genre '{normalised}' not in configured map");
    }

    private BpmTempoRange? ReturnFallback(string reason)
    {
        if (_fallback is not null)
        {
            _logger.LogTrace(
                "Tempo range fallback [{Min}, {Max}] applied — {Reason}.",
                _fallback.Min, _fallback.Max, reason);
            return _fallback;
        }
        _logger.LogTrace("Tempo range: no rule matched ({Reason}); Essentia uses its default range.", reason);
        return null;
    }

    /// <summary>
    /// Normalises a raw genre string to its canonical form via the taxonomy alias table.
    /// <para>
    /// Examples (with the default taxonomy):
    /// <c>"Tech House"</c> → <c>"House"</c>, <c>"Liquid Drum &amp; Bass"</c> → <c>"Drum and Bass"</c>,
    /// <c>"House (Original Mix)"</c> → <c>"House (Original Mix)"</c> (no match → passthrough).
    /// </para>
    /// <para>
    /// When the taxonomy is empty (user hasn't configured one), the raw string is returned. Only
    /// configured-genre-keys that match exactly will then resolve a range.
    /// </para>
    /// </summary>
    private string NormaliseGenre(string raw)
    {
        if (_aliasTable.TryGetValue(raw, out var pair))
        {
            return pair.Genre;
        }
        return raw;
    }
}
