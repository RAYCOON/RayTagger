using System.Globalization;

namespace RayTagger.Analysis.Internal;

/// <summary>
/// Parses <c>fpcalc</c> plain-text output. Format is one <c>KEY=value</c> per line; the keys we
/// care about are <c>DURATION</c> (seconds, integer) and <c>FINGERPRINT</c> (Chromaprint compressed
/// base64). Other lines are ignored — fpcalc occasionally emits <c>FILE=…</c>.
/// </summary>
internal static class ChromaprintOutputParser
{
    public sealed record Parsed(string? Fingerprint, double? DurationSeconds);

    public static Parsed Parse(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);

        string? fingerprint = null;
        double? duration = null;

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = trimmed[..eq];
            var value = trimmed[(eq + 1)..];

            if (key.Equals("FINGERPRINT", StringComparison.OrdinalIgnoreCase))
            {
                fingerprint = value.Length == 0 ? null : value;
            }
            else if (key.Equals("DURATION", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                duration = seconds;
            }
        }

        return new Parsed(fingerprint, duration);
    }
}
