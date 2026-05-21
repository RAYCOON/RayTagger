namespace RayTagger.Core.Models;

/// <summary>
/// Bidirectional map between musical-key standard notation (e.g. "Am", "F#m", "G") and the
/// Camelot Wheel (e.g. "8A", "11A", "9B"). Used wherever a partial key needs filling-in so
/// downstream consumers always see a complete <see cref="MusicalKey"/>: the Metadata layer's tag
/// reader fills in whichever notation the file lacked, and audio analyzers fill in whichever
/// notation the native tool didn't emit.
/// </summary>
/// <remarks>
/// Lives in Core because it's pure music theory — no IO, no Metadata or Analysis dependency.
/// Multiple assemblies need it, so the visibility is public and the assembly placement is the
/// natural one (Core has no upstream references).
/// </remarks>
public static class KeyNotationConverter
{
    // 12 major + 12 minor = 24 entries. Each is canonical Camelot ↔ standard.
    // Major scale uses "B" suffix in Camelot; minor uses "A" suffix.
    private static readonly (string Standard, string Camelot)[] Mapping =
    [
        // Minor keys — Camelot "A" wheel
        ("Abm",  "1A"),  ("G#m",  "1A"),
        ("Ebm",  "2A"),  ("D#m",  "2A"),
        ("Bbm",  "3A"),  ("A#m",  "3A"),
        ("Fm",   "4A"),
        ("Cm",   "5A"),
        ("Gm",   "6A"),
        ("Dm",   "7A"),
        ("Am",   "8A"),
        ("Em",   "9A"),
        ("Bm",  "10A"),
        ("F#m", "11A"),  ("Gbm", "11A"),
        ("C#m", "12A"),  ("Dbm", "12A"),

        // Major keys — Camelot "B" wheel
        ("B",    "1B"),  ("Cb",   "1B"),
        ("F#",   "2B"),  ("Gb",   "2B"),
        ("Db",   "3B"),  ("C#",   "3B"),
        ("Ab",   "4B"),
        ("Eb",   "5B"),  ("D#",   "5B"),
        ("Bb",   "6B"),  ("A#",   "6B"),
        ("F",    "7B"),
        ("C",    "8B"),
        ("G",    "9B"),
        ("D",   "10B"),
        ("A",   "11B"),
        ("E",   "12B"),
    ];

    private static readonly Dictionary<string, string> StandardToCamelot = BuildStandardToCamelot();
    private static readonly Dictionary<string, string> CamelotToStandard = BuildCamelotToStandard();

    private static Dictionary<string, string> BuildStandardToCamelot()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (std, cam) in Mapping)
        {
            dict.TryAdd(std, cam);
        }
        return dict;
    }

    private static Dictionary<string, string> BuildCamelotToStandard()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Prefer the first appearance (canonical form) when a Camelot code has multiple aliases.
        foreach (var (std, cam) in Mapping)
        {
            dict.TryAdd(cam, std);
        }
        return dict;
    }

    /// <summary>
    /// Tries to build a fully-populated <see cref="MusicalKey"/> from whichever notation was read.
    /// Returns null when neither input is recognised.
    /// </summary>
    /// <remarks>
    /// **Real-world tolerance:** the ID3v2.4 spec says <c>TKEY</c> (and Vorbis <c>INITIALKEY</c>)
    /// carry standard notation — but Mixed In Key and several other DJ tools ignore the spec and
    /// write Camelot (<c>8A</c>) into those frames. We try both interpretations of each input so
    /// neither convention silently drops the key. When one input is valid and the other is
    /// garbage, the valid one wins and its counterpart is derived from the map.
    /// </remarks>
    public static MusicalKey? FromEither(string? standard, string? camelot)
    {
        var stdSlot = ClassifyValue(standard);
        var camSlot = ClassifyValue(camelot);

        // Pull a recognised standard + Camelot pair from whichever inputs actually parse.
        // Both inputs are tried as both notations so a Camelot-in-TKEY ("8A") still resolves.
        var resolvedStd = stdSlot.Standard ?? camSlot.Standard;
        var resolvedCam = camSlot.Camelot ?? stdSlot.Camelot;

        if (resolvedStd is not null && resolvedCam is not null)
        {
            return new MusicalKey(resolvedStd, resolvedCam);
        }
        return null;
    }

    /// <summary>
    /// Classifies a raw key string by trying it as standard notation first, then Camelot. Returns
    /// whichever direction(s) recognise it together with the derived counterpart.
    /// </summary>
    private static (string? Standard, string? Camelot) ClassifyValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var asStd = NormaliseStandard(value);
        if (asStd is not null && StandardToCamelot.TryGetValue(asStd, out var camFromStd))
        {
            return (asStd, camFromStd);
        }

        var asCam = NormaliseCamelot(value);
        if (asCam is not null && CamelotToStandard.TryGetValue(asCam, out var stdFromCam))
        {
            return (stdFromCam, asCam);
        }

        return (null, null);
    }

    private static string? NormaliseStandard(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        // Trim and titlecase the first letter: " am " → "Am", "F#M" → "F#m".
        // CA1308 prefers uppercase normalisation, but musical-key notation deliberately uses
        // lowercase 'm' for minor (the de-facto standard from Mixed In Key, Traktor, etc.).
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
#pragma warning disable CA1308 // Musical-key notation uses lowercase 'm' as the minor-mode marker.
        var tail = trimmed[1..].ToLowerInvariant();
#pragma warning restore CA1308
        return char.ToUpperInvariant(trimmed[0]) + tail;
    }

    private static string? NormaliseCamelot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        // Format: digit(s) + A/B, e.g. "8A", "11B". Strip whitespace, uppercase.
        return value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Zero-pad the leading number of a Camelot code so lexicographic sort yields Wheel order
    /// (1A, 1B, 2A, 2B, …, 12A, 12B) instead of "10A" landing before "2A". Pure transform on
    /// the textual representation — null/whitespace passes through as null, codes that don't
    /// end in A/B fall through unchanged so degenerate inputs don't silently produce
    /// misleading sort keys.
    /// </summary>
    public static string? CamelotSortKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var letterIndex = value.Length - 1;
        var letter = char.ToUpperInvariant(value[letterIndex]);
        if (letter != 'A' && letter != 'B') return value;
        var number = value[..letterIndex];
        return number.Length == 1 ? $"0{number}{letter}" : $"{number}{letter}";
    }
}
