using System.Globalization;
using System.Text.RegularExpressions;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Snapshot of every column filter at the moment a predicate runs. Bundling the 17 strings
/// into one struct lets <see cref="FilterPredicates.MatchesAllFilters"/> stay a pure static
/// function — easy to test without instantiating <see cref="ScanViewModel"/> with its DI deps.
/// </summary>
/// <remarks>
/// Reads back exactly the same fields <see cref="ScanViewModel"/> exposes as ObservableProperties.
/// Construct via the convenience ctor below at the start of every <c>Refresh()</c> cycle.
/// </remarks>
public readonly record struct ColumnFilters(
    string? Status,
    string? FileName,
    string? Title,
    string? Artist,
    string? Year,
    string? Genre,
    string? SubGenre,
    string? Bpm,
    string? Key,
    string? CamelotKey,
    string? Energy,
    string? Mood,
    string? SetPosition,
    string? Album,
    string? Length,
    string? Size,
    string? Modified);

/// <summary>
/// Pure-functional filter predicates used by <see cref="ScanViewModel.FilteredOutcomes"/>.
/// Lives outside the VM so unit tests can exercise the matching logic without spinning up
/// the full ScanCoordinator / ITagReader / IMappingRuleEngine dependency chain.
/// </summary>
/// <remarks>
/// Match strategy across all string columns: case-insensitive substring against the same
/// display value the DataGrid cell renders ("filter what you see"). BPM additionally accepts
/// range and comparison syntax — see <see cref="MatchesBpmFilter"/>.
/// </remarks>
public static partial class FilterPredicates
{
    /// <summary>
    /// Returns true iff every non-empty filter in <paramref name="f"/> matches its column on
    /// <paramref name="r"/>. Empty filters short-circuit to "match".
    /// </summary>
    public static bool MatchesAllFilters(TrackOutcomeViewModel r, ColumnFilters f)
    {
        ArgumentNullException.ThrowIfNull(r);
        return ContainsCi(r.StatusBadge, f.Status)
            && ContainsCi(r.FileName, f.FileName)
            && ContainsCi(r.Title, f.Title)
            && ContainsCi(r.Artist, f.Artist)
            && ContainsCi(r.Year, f.Year)
            && ContainsCi(r.GenreDisplay, f.Genre)
            && ContainsCi(r.SubGenreDisplay, f.SubGenre)
            && MatchesBpmFilter(r.EffectiveBpm, f.Bpm, r.BpmDisplay)
            && ContainsCi(r.KeyDisplay, f.Key)
            && ContainsCi(r.CamelotKeyDisplay, f.CamelotKey)
            && ContainsCi(r.EnergyDisplay, f.Energy)
            && ContainsCi(r.MoodDisplay, f.Mood)
            && ContainsCi(r.SetPositionDisplay, f.SetPosition)
            && ContainsCi(r.Album, f.Album)
            && ContainsCi(r.LengthDisplay, f.Length)
            && ContainsCi(r.SizeDisplay, f.Size)
            && ContainsCi(r.ModifiedDisplay, f.Modified);
    }

    /// <summary>Case-insensitive substring match. Empty filter ⇒ always true.</summary>
    public static bool ContainsCi(string? value, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    // Source-generated regexes (.NET 7+): compiled at build time, zero runtime IL emit.
    [GeneratedRegex(
        @"^\s*(-?\d+(?:[.,]\d+)?)\s*-\s*(-?\d+(?:[.,]\d+)?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BpmRangeRegex();

    [GeneratedRegex(
        @"^\s*(<=|>=|<|>)\s*(-?\d+(?:[.,]\d+)?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BpmComparisonRegex();

    /// <summary>
    /// BPM-specific filter. Recognises numeric ranges and comparisons before falling back to a
    /// substring match against the display string.
    /// <list type="bullet">
    ///   <item><c>120-128</c> → BPM lies in [min,max] (endpoints reversible — <c>128-120</c> works too).</item>
    ///   <item><c>&lt;100</c>, <c>&lt;=100</c>, <c>&gt;100</c>, <c>&gt;=100</c> → strict / inclusive comparisons.</item>
    ///   <item>Decimal separator: both <c>,</c> (DE) and <c>.</c> (EN) accepted.</item>
    ///   <item>Anything else (e.g. plain "12") → contains-match against <paramref name="display"/>,
    ///         so the diff-arrow form "124 → 128" still matches "12" or "→".</item>
    /// </list>
    /// Range / comparison matches require a non-null BPM. The substring fallback path leaves the
    /// existing case-insensitive behaviour untouched.
    /// </summary>
    public static bool MatchesBpmFilter(double? bpm, string? filter, string? display)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var trimmed = filter.Trim();

        var rangeMatch = BpmRangeRegex().Match(trimmed);
        if (rangeMatch.Success
            && TryParseBpmNumber(rangeMatch.Groups[1].Value, out var a)
            && TryParseBpmNumber(rangeMatch.Groups[2].Value, out var b))
        {
            if (!bpm.HasValue) return false;
            var (min, max) = a <= b ? (a, b) : (b, a);
            return bpm.Value >= min && bpm.Value <= max;
        }

        var compMatch = BpmComparisonRegex().Match(trimmed);
        if (compMatch.Success && TryParseBpmNumber(compMatch.Groups[2].Value, out var threshold))
        {
            if (!bpm.HasValue) return false;
            return compMatch.Groups[1].Value switch
            {
                "<" => bpm.Value < threshold,
                "<=" => bpm.Value <= threshold,
                ">" => bpm.Value > threshold,
                ">=" => bpm.Value >= threshold,
                _ => false,
            };
        }

        return ContainsCi(display, trimmed);
    }

    // Accept both "," and "." as decimal separator so "124,5" and "124.5" both parse —
    // the BPM display uses CurrentCulture (German "," in this app's locale).
    private static bool TryParseBpmNumber(string text, out double value)
        => double.TryParse(text.Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
