using System.Text;
using System.Text.Json;

namespace RayTagger.Analysis.Internal;

// EssentiaResult lives one level up so it can be the public return type of IEssentiaAnalysisService.

/// <summary>
/// Parses the <c>essentia_streaming_extractor_music</c> JSON output. The full document carries
/// hundreds of descriptors; we extract only the BPM / Key / Energy fields Tagger actually needs.
/// </summary>
/// <remarks>
/// Implemented with <see cref="JsonDocument"/> rather than POCO deserialisation because:
/// <list type="bullet">
///   <item>The schema is large and we'd hand-write a 30-class hierarchy for ~6 fields.</item>
///   <item>Frame-aggregated descriptors are objects with <c>mean</c>/<c>var</c>/<c>min</c>/<c>max</c>,
///         single-value descriptors are bare numbers — POCO binding can't handle both shapes.</item>
///   <item>Older Essentia versions wrap scalars in single-element arrays.</item>
/// </list>
/// </remarks>
internal static class EssentiaJsonParser
{
    public static EssentiaResult Parse(Stream jsonStream)
    {
        ArgumentNullException.ThrowIfNull(jsonStream);

        // Essentia occasionally leaks raw control bytes (0x00-0x1F) into JSON strings when a
        // track's tags contain them — System.Text.Json is strict and refuses these per RFC 8259.
        // Sanitise the stream before parsing: only newline/CR/tab survive, everything else in
        // that range gets replaced with a space. We only read a handful of well-known fields
        // (BPM, key, energy), none of which can legitimately contain control characters.
        using var reader = new StreamReader(jsonStream, Encoding.UTF8);
        var raw = reader.ReadToEnd();
        var sanitised = SanitiseControlChars(raw);

        using var doc = JsonDocument.Parse(sanitised);
        var root = doc.RootElement;

        var bpm = TryReadDouble(root, "rhythm", "bpm");
        // Essentia doesn't expose a top-level bpm_confidence; the first BPM-histogram peak's
        // weight is the documented proxy (0..1, mass of beats matching the dominant tempo).
        var bpmConfidence = TryReadDouble(root, "rhythm", "bpm_histogram_first_peak_weight");

        var (keyKey, keyScale, keyStrength) = ReadEdmaKey(root);

        // Energy descriptors. Frame-level outputs are aggregated by Essentia into
        // {mean, var, min, max, ...} — we read the mean. Track-level scalars (average_loudness,
        // onset_rate, danceability) are bare numbers.
        var spectralEnergy = TryReadAggregateMean(root, "lowlevel", "spectral_energy");
        var spectralFlux = TryReadAggregateMean(root, "lowlevel", "spectral_flux");
        var averageLoudness = TryReadDouble(root, "lowlevel", "average_loudness");
        var onsetRate = TryReadDouble(root, "rhythm", "onset_rate");
        var danceability = TryReadDouble(root, "rhythm", "danceability");
        var beatsLoudness = TryReadAggregateMean(root, "rhythm", "beats_loudness");

        // Descriptors used by the heuristic genre classifier (DSP-only, present in every
        // default Essentia run). See docs/PLAN_GENRE_CLASSIFICATION.md §3.5.
        var spectralCentroidMean = TryReadAggregateMean(root, "lowlevel", "spectral_centroid");
        var spectralComplexityMean = TryReadAggregateMean(root, "lowlevel", "spectral_complexity");
        var dynamicComplexity = TryReadDouble(root, "lowlevel", "dynamic_complexity");
        var chordsChangesRate = TryReadDouble(root, "tonal", "chords_changes_rate");
        var chordsStrengthMean = TryReadAggregateMean(root, "tonal", "chords_strength");

        return new EssentiaResult(
            bpm, bpmConfidence,
            keyKey, keyScale, keyStrength,
            spectralEnergy,
            averageLoudness, spectralFlux, onsetRate, danceability, beatsLoudness,
            spectralCentroidMean, spectralComplexityMean, dynamicComplexity,
            chordsChangesRate, chordsStrengthMean);
    }

    public static EssentiaResult ParseString(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream(bytes);
        return Parse(ms);
    }

    private static string SanitiseControlChars(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c < 0x20 && c != '\n' && c != '\r' && c != '\t')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static (string? Key, string? Scale, double? Strength) ReadEdmaKey(JsonElement root)
    {
        if (!root.TryGetProperty("tonal", out var tonal) || tonal.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }
        if (!tonal.TryGetProperty("key_edma", out var edma) || edma.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }
        return (
            TryReadString(edma, "key"),
            TryReadString(edma, "scale"),
            TryReadScalarNumber(edma, "strength"));
    }

    private static double? TryReadDouble(JsonElement root, string objectKey, string fieldKey)
    {
        if (!root.TryGetProperty(objectKey, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return TryReadScalarNumber(obj, fieldKey);
    }

    private static double? TryReadAggregateMean(JsonElement root, string objectKey, string fieldKey)
    {
        if (!root.TryGetProperty(objectKey, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!obj.TryGetProperty(fieldKey, out var field))
        {
            return null;
        }
        // Frame-aggregated shape: { "mean": x, "var": y, ... }
        if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("mean", out var mean))
        {
            return ReadNumberOrFirstArrayElement(mean);
        }
        // Single-value fallback for non-aggregated descriptors.
        return ReadNumberOrFirstArrayElement(field);
    }

    private static double? TryReadScalarNumber(JsonElement parent, string fieldKey)
    {
        if (!parent.TryGetProperty(fieldKey, out var field))
        {
            return null;
        }
        return ReadNumberOrFirstArrayElement(field);
    }

    private static string? TryReadString(JsonElement parent, string fieldKey)
    {
        if (!parent.TryGetProperty(fieldKey, out var field))
        {
            return null;
        }
        if (field.ValueKind == JsonValueKind.Array && field.GetArrayLength() > 0)
        {
            field = field[0];
        }
        return field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    }

    private static double? ReadNumberOrFirstArrayElement(JsonElement field)
    {
        // Some older Essentia versions wrap scalars in single-element arrays.
        if (field.ValueKind == JsonValueKind.Array && field.GetArrayLength() > 0)
        {
            field = field[0];
        }
        return field.ValueKind == JsonValueKind.Number ? field.GetDouble() : null;
    }
}
