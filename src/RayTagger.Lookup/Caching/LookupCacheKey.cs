using System.Security.Cryptography;
using System.Text;
using RayTagger.Core.Pipeline;

namespace RayTagger.Lookup.Caching;

/// <summary>
/// Builds a stable, filename-safe cache key from a <see cref="LookupQuery"/>. Fingerprint wins
/// when present (different artist/title rendering shouldn't fork the cache); otherwise we hash the
/// normalised artist+title+album triple.
/// </summary>
/// <remarks>
/// Normalisation: lowercase, all non-alphanumeric stripped. Two tracks tagged "The Beatles" vs.
/// "the beatles!" share a cache slot — the goal is "did we already ask the API about THIS song?".
/// </remarks>
public static class LookupCacheKey
{
    public static string From(LookupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!string.IsNullOrWhiteSpace(query.Fingerprint))
        {
            return "fp_" + Hash(query.Fingerprint!);
        }
        var artist = Normalise(query.Artist);
        var title = Normalise(query.Title);
        var album = Normalise(query.Album);
        return "ata_" + Hash($"{artist}|{title}|{album}");
    }

    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        // 16 hex chars (8 bytes) is plenty for a cache namespace — collision risk is negligible
        // even for libraries with millions of tracks, and shorter filenames keep the cache dir tidy.
        // Convert.ToHexString already returns uppercase, which is fine for filenames on every
        // case-sensitive and case-insensitive filesystem.
        return Convert.ToHexString(hash, 0, 8);
    }
}
