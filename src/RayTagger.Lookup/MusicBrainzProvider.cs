using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup.Internal;

namespace RayTagger.Lookup;

/// <summary>
/// MusicBrainz REST provider. Anchors on the recording MBID when one is available (typically
/// supplied upstream by AcoustID) — that's the high-precision path; falls back to free-text
/// search by artist+title when no MBID is set. Always self-throttled to 1 req/s per the
/// MusicBrainz fair-use policy.
/// </summary>
/// <remarks>
/// Requires a descriptive <c>User-Agent</c> per
/// <a href="https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting">MusicBrainz Rate Limiting</a>.
/// We set it once on the configured <see cref="HttpClient"/>.
/// </remarks>
public sealed class MusicBrainzProvider : IMetadataProvider
{
    public const string ProviderName = "musicbrainz";

    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzProvider> _logger;
    private readonly RateLimiter _rateLimiter;

    public MusicBrainzProvider(
        HttpClient httpClient,
        ILogger<MusicBrainzProvider> logger,
        TimeSpan minRequestInterval)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(minRequestInterval, TimeSpan.Zero);

        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = new RateLimiter(minRequestInterval);
    }

    public string Name => ProviderName;

    public bool CanHandle(LookupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.RecordingMbid is not null
            || (!string.IsNullOrWhiteSpace(query.Artist) && !string.IsNullOrWhiteSpace(query.Title));
    }

    public async Task<LookupResult?> LookupAsync(LookupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Fast path: caller already has the recording MBID (e.g. AcoustID handshake supplied
        // it). One request, all genres/tags inline via inc=.
        if (query.RecordingMbid is not null)
        {
            return await FetchRecordingDetailsAsync(query.RecordingMbid.Value, cancellationToken).ConfigureAwait(false);
        }

        // Slow path: artist+title search. MB's /search endpoint **does not** return tags or
        // genres regardless of an `inc=` parameter — it's strictly a discovery step. So we
        // walk up to MaxRecordingsToProbe of the top search hits, follow each one up with a
        // /recording/{mbid}?inc=genres+tags+releases detail lookup, and stop at the first hit
        // that actually carries genre/tag annotations. That way an untagged remix at rank 1
        // doesn't shadow the tagged base recording at rank 2.
        var searchUri = BuildSearchUri(query);
        if (searchUri is null) return null;

        List<Guid> topMbids;
        try
        {
            topMbids = await SearchForTopMbidsAsync(searchUri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz search failed: {Message}", ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz search returned malformed JSON: {Message}", ex.Message);
            return null;
        }

        if (topMbids.Count == 0) return LookupResult.Empty;

        LookupResult? firstHitWithMbidOnly = null;
        for (var i = 0; i < topMbids.Count; i++)
        {
            var detail = await FetchRecordingDetailsAsync(topMbids[i], cancellationToken).ConfigureAwait(false);
            if (detail is null) continue;
            firstHitWithMbidOnly ??= detail;
            if (detail.GenreCandidates.Count > 0)
            {
                return detail;
            }
        }

        // No recording in the top-N had any tags. Return the first hit anyway so the caller
        // gets at least the MBID for downstream lookup chaining (and so the empty-result
        // cache filter in LookupRunner sees a useful signal worth caching).
        return firstHitWithMbidOnly ?? LookupResult.Empty;
    }

    private async Task<LookupResult?> FetchRecordingDetailsAsync(Guid recordingMbid, CancellationToken cancellationToken)
    {
        var uri = $"ws/2/recording/{recordingMbid:D}?fmt=json&inc=genres+tags+releases";
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.GetAsync(new Uri(uri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz detail returned {Status} for {Uri}", response.StatusCode, uri);
                return null;
            }
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseRecordingResponse(doc.RootElement);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz detail request failed: {Message}", ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz detail returned malformed JSON: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<List<Guid>> SearchForTopMbidsAsync(string searchUri, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.GetAsync(new Uri(searchUri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MusicBrainz search returned {Status} for {Uri}", response.StatusCode, searchUri);
            return [];
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var take = Math.Min(MaxRecordingsToProbe, recordings.GetArrayLength());
        var mbids = new List<Guid>(take);
        for (var i = 0; i < take; i++)
        {
            if (recordings[i].TryGetProperty("id", out var idElem)
                && Guid.TryParse(idElem.GetString(), out var parsed))
            {
                mbids.Add(parsed);
            }
        }
        return mbids;
    }

    private static string? BuildSearchUri(LookupQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title)) return null;
        var artist = Uri.EscapeDataString(EscapeLucene(query.Artist));
        var title = Uri.EscapeDataString(EscapeLucene(query.Title));
        return $"ws/2/recording/?query=artist:\"{artist}\"%20AND%20recording:\"{title}\"&fmt=json&limit=5";
    }

    /// <summary>
    /// MusicBrainz wraps Lucene; the embedded values run inside double-quoted phrases but the
    /// special chars listed below still need a leading backslash, otherwise an artist named
    /// e.g. <c>"AC/DC"</c>, <c>"Wu-Tang"</c>, or anything with parens / colons / wildcards
    /// produces a 400 or an empty result set. URL-escaping alone doesn't help — Lucene operates
    /// on the decoded query.
    /// </summary>
    private static string EscapeLucene(string raw)
    {
        // Lucene specials per the Apache Lucene query-syntax reference:
        //   + - && || ! ( ) { } [ ] ^ " ~ * ? : \ /
        // Backslash is escaped first to keep us from double-escaping the escapes we add.
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\\':
                case '+': case '-': case '!': case '(': case ')':
                case '{': case '}': case '[': case ']':
                case '^': case '"': case '~': case '*': case '?':
                case ':': case '/':
                    sb.Append('\\').Append(c);
                    break;
                case '&':
                case '|':
                    // && and || only carry meaning when doubled, but escaping a lone bare one
                    // is safe and avoids a stateful look-ahead.
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static LookupResult ParseRecordingResponse(JsonElement root)
    {
        var genres = ReadCandidates(root, "genres", weight: 1.0);
        var tags = ReadCandidates(root, "tags", weight: 0.7);
        var combined = MergeRanked(genres, tags);

        Guid? releaseMbid = null;
        if (root.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0)
        {
            if (releases[0].TryGetProperty("id", out var idElem) && Guid.TryParse(idElem.GetString(), out var parsed))
            {
                releaseMbid = parsed;
            }
        }
        Guid? recordingMbid = null;
        if (root.TryGetProperty("id", out var recIdElem) && Guid.TryParse(recIdElem.GetString(), out var recParsed))
        {
            recordingMbid = recParsed;
        }

        return new LookupResult(combined, SubGenreCandidates: [], releaseMbid, recordingMbid);
    }

    /// <summary>
    /// How many of the top search hits to follow up with a detail lookup before giving up.
    /// Each probe costs one extra MB request (gated by the 1 req/s rate limit), so this trades
    /// latency for the chance to find genre tags on a non-#1 hit. 3 strikes a balance — the
    /// untagged-remix-then-tagged-base case usually resolves at rank 2.
    /// </summary>
    private const int MaxRecordingsToProbe = 3;

    private static List<GenreCandidate> ReadCandidates(JsonElement root, string fieldName, double weight)
    {
        var result = new List<GenreCandidate>();
        if (!root.TryGetProperty(fieldName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var maxCount = 1;
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("count", out var countElem) && countElem.TryGetInt32(out var c) && c > maxCount)
            {
                maxCount = c;
            }
        }
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameElem)) continue;
            var name = nameElem.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var count = item.TryGetProperty("count", out var countElem) && countElem.TryGetInt32(out var c) ? c : 1;
            var confidence = Math.Clamp((double)count / maxCount * weight, 0, 1);
            result.Add(new GenreCandidate(name, confidence, ProviderName));
        }
        return result;
    }

    private static List<GenreCandidate> MergeRanked(
        IEnumerable<GenreCandidate> primary, IEnumerable<GenreCandidate> secondary)
    {
        var byValue = new Dictionary<string, GenreCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in primary.Concat(secondary))
        {
            if (byValue.TryGetValue(c.Value, out var existing))
            {
                if (c.Confidence > existing.Confidence) byValue[c.Value] = c;
            }
            else
            {
                byValue[c.Value] = c;
            }
        }
        return byValue.Values.OrderByDescending(c => c.Confidence).ToList();
    }
}
