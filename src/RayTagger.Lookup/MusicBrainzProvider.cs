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
    private readonly RateLimiter _rateLimiter = new(TimeSpan.FromMilliseconds(1100));

    public MusicBrainzProvider(HttpClient httpClient, ILogger<MusicBrainzProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
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

        var requestUri = query.RecordingMbid is not null
            ? $"ws/2/recording/{query.RecordingMbid:D}?fmt=json&inc=genres+tags+releases"
            : BuildSearchUri(query);
        if (requestUri is null) return null;

        try
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.GetAsync(new Uri(requestUri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz returned {Status} for {Uri}", response.StatusCode, requestUri);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return query.RecordingMbid is not null
                ? ParseRecordingResponse(doc.RootElement)
                : ParseSearchResponse(doc.RootElement);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz request failed: {Message}", ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz returned malformed JSON: {Message}", ex.Message);
            return null;
        }
    }

    private static string? BuildSearchUri(LookupQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title)) return null;
        var artist = Uri.EscapeDataString(query.Artist);
        var title = Uri.EscapeDataString(query.Title);
        return $"ws/2/recording/?query=artist:\"{artist}\"%20AND%20recording:\"{title}\"&fmt=json&limit=5";
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

    private static LookupResult ParseSearchResponse(JsonElement root)
    {
        if (!root.TryGetProperty("recordings", out var recordings)
            || recordings.ValueKind != JsonValueKind.Array
            || recordings.GetArrayLength() == 0)
        {
            return LookupResult.Empty;
        }
        // Pick the best (first) match — MusicBrainz already sorts by score descending.
        return ParseRecordingResponse(recordings[0]);
    }

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
