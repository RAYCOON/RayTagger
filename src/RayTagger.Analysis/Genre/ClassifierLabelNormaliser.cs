using System.Globalization;
using System.Text;

namespace RayTagger.Analysis.Genre;

/// <summary>
/// Normalises raw model labels so the taxonomy resolver's whole-word regex
/// (<c>\b…\b</c>, case-insensitive) actually matches them. Underscore is a word
/// character in .NET regex, so <c>\bdeep_house\b</c> won't match <c>\bDeep House\b</c>
/// in the taxonomy — every classifier funnels its labels through this helper before
/// emitting candidates. See <c>docs/PLAN_GENRE_CLASSIFICATION.md §5.1a</c>.
/// </summary>
public static class ClassifierLabelNormaliser
{
    /// <summary>
    /// Pipeline: lowercase → replace <c>_</c>/<c>-</c>/<c>/</c> with single space → collapse
    /// runs of whitespace → rough English depluralisation (strip trailing <c>s</c> when length &gt; 4
    /// and the result is still ASCII letters/spaces) → trim. Returns empty string for null/blank
    /// input — caller decides whether to emit a candidate.
    /// </summary>
    public static string Normalise(string? rawLabel)
    {
        if (string.IsNullOrWhiteSpace(rawLabel))
        {
            return string.Empty;
        }

        // Taxonomy match regex is case-insensitive but the normalised form is canonical
        // lowercase — keeps tests/traces readable.
#pragma warning disable CA1308
        var lowered = rawLabel.ToLowerInvariant();
#pragma warning restore CA1308
        var replaced = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            replaced.Append(ch is '_' or '-' or '/' ? ' ' : ch);
        }

        var collapsed = CollapseWhitespace(replaced.ToString()).Trim();
        if (collapsed.Length == 0)
        {
            return string.Empty;
        }

        var depluralised = TryDepluralise(collapsed);
        return depluralised;
    }

    private static string CollapseWhitespace(string input)
    {
        var sb = new StringBuilder(input.Length);
        var lastWasSpace = false;
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }

    private static string TryDepluralise(string input)
    {
        // "trances" → "trance"; leave short words alone ("bass" / "jazz" stay intact).
        if (input.Length <= 4 || !input.EndsWith('s'))
        {
            return input;
        }

        var trimmed = input[..^1];
        foreach (var ch in trimmed)
        {
            if (!(char.IsLetter(ch) && ch < 128) && ch != ' ')
            {
                return input;
            }
        }

        // Avoid double-stripping ("ss" endings like "bass" / "drum n bass" never reach here
        // because of the length floor, but guard anyway).
        if (trimmed.EndsWith('s'))
        {
            return input;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToLower(trimmed);
    }
}
