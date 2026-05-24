using System.Globalization;

namespace RayTagger.Core.Validation;

/// <summary>
/// Parses the Mixed-In-Key comment-tag format <c>"{BPM} - {Camelot-Key} - {Energy}"</c> into a
/// <see cref="MixedInKeyTruth"/> record. Used by the backtest harness to extract ground-truth
/// BPM/Key/Energy from <c>./music/Tagged/</c> tracks.
/// </summary>
/// <remarks>
/// <para><b>Format example:</b> <c>"120.00 - 9A - 6"</c> — BPM with two decimals, Camelot wheel
/// notation (1A-12A / 1B-12B), integer energy 1-10. Separator is exactly <c>" - "</c>
/// (space-dash-space).</para>
/// <para><b>Why a dedicated parser:</b> the standard TagReader pulls TBPM/BPM and TKEY/INITIALKEY
/// frames, but those carry per-frame data that drifts from the comment (e.g. TBPM integer-rounded
/// to "148" while comment says "147.89"). The comment is Mixed-In-Key's authoritative output and
/// the most reliable truth source. Frame fallback covers the rare cases where comment is missing
/// or malformed.</para>
/// <para><b>Genre-specific BPM correction:</b> Mixed-In-Key tags Drum and Bass half-time
/// (typically 85-87 BPM instead of 170-175) and a subset of Dubstep tracks at full rate
/// (140 BPM instead of 70-95). The project convention is the opposite — DnB at full rate,
/// Dubstep half-time. <see cref="ApplyGenreCorrection"/> inverts MIK's mis-tagging.</para>
/// </remarks>
public static class MixedInKeyCommentParser
{
    private static readonly string[] Separator = [" - "];

    /// <summary>
    /// Tries to parse a Mixed-In-Key comment string. Returns null when the comment doesn't match
    /// the expected three-part shape or any of the three fields fails to parse.
    /// </summary>
    public static MixedInKeyTruth? TryParseComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var parts = comment.Split(Separator, StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
        {
            return null;
        }

        var camelot = parts[1].ToUpperInvariant();
        if (!IsCamelotKey(camelot))
        {
            return null;
        }

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var energy))
        {
            return null;
        }

        if (energy is < 1 or > 10)
        {
            return null;
        }

        return new MixedInKeyTruth(bpm, camelot, energy);
    }

    /// <summary>
    /// Applies the genre-specific BPM correction to a Mixed-In-Key truth value. DnB tracks tagged
    /// at &lt; 100 BPM are doubled; Dubstep tracks tagged at &gt; 100 BPM are halved. All other
    /// genres pass through unchanged. The <paramref name="genreSubfolder"/> is compared
    /// case-insensitive against the canonical genre names.
    /// </summary>
    public static MixedInKeyTruth ApplyGenreCorrection(MixedInKeyTruth truth, string? genreSubfolder)
    {
        ArgumentNullException.ThrowIfNull(truth);

        if (string.IsNullOrWhiteSpace(genreSubfolder))
        {
            return truth;
        }

        var genre = genreSubfolder.Trim();
        if (IsDrumAndBass(genre) && truth.Bpm < 100)
        {
            return truth with { Bpm = truth.Bpm * 2, BpmWasCorrected = true };
        }
        if (IsDubstep(genre) && truth.Bpm > 100)
        {
            return truth with { Bpm = truth.Bpm / 2, BpmWasCorrected = true };
        }
        return truth;
    }

    private static bool IsDrumAndBass(string genre) =>
        genre.Equals("Drum and Bass", StringComparison.OrdinalIgnoreCase)
        || genre.Equals("DnB", StringComparison.OrdinalIgnoreCase)
        || genre.Equals("Drum & Bass", StringComparison.OrdinalIgnoreCase);

    private static bool IsDubstep(string genre) =>
        genre.Equals("Dubstep", StringComparison.OrdinalIgnoreCase)
        || genre.Equals("DubStep", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates a Camelot Wheel code: digit(s) 1-12 followed by A or B.
    /// </summary>
    public static bool IsCamelotKey(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length < 2 || value.Length > 3) return false;
        var letter = value[^1];
        if (letter != 'A' && letter != 'B') return false;
        var digits = value[..^1];
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return false;
        }
        return n is >= 1 and <= 12;
    }
}

/// <summary>
/// Ground-truth values extracted from a Mixed-In-Key-tagged file: BPM (with genre-correction
/// applied), Camelot key, and energy 1-10. <see cref="BpmWasCorrected"/> flags whether the
/// genre-correction kicked in (used by the backtest reporter to surface "MIK was wrong here").
/// </summary>
public sealed record MixedInKeyTruth(double Bpm, string CamelotKey, int Energy)
{
    public bool BpmWasCorrected { get; init; }
}
