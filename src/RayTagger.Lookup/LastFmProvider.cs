using System.Text.Json;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup.Internal;

namespace RayTagger.Lookup;

/// <summary>
/// Last.fm top-tags provider. Returns broad crowd-sourced tags — noisy compared to MusicBrainz,
/// but valuable for genres MusicBrainz hasn't curated yet. Tags are weighted to 0.6 confidence
/// because they're user-generated; mapping rules can still prefer them if a regex demands it.
/// </summary>
public sealed class LastFmProvider : IMetadataProvider
{
    public const string ProviderName = "lastfm";
    private const double TagWeight = 0.6;

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<LastFmProvider> _logger;
    private readonly RateLimiter _rateLimiter = new(TimeSpan.FromMilliseconds(200));

    public LastFmProvider(HttpClient httpClient, string apiKey, ILogger<LastFmProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _apiKey = apiKey;
        _logger = logger;
    }

    public string Name => ProviderName;

    public bool CanHandle(LookupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return !string.IsNullOrWhiteSpace(query.Artist) && !string.IsNullOrWhiteSpace(query.Title);
    }

    public async Task<LookupResult?> LookupAsync(LookupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanHandle(query)) return null;

        var artist = Uri.EscapeDataString(query.Artist!);
        var title = Uri.EscapeDataString(query.Title!);
        var requestUri = $"2.0/?method=track.getTopTags&artist={artist}&track={title}&api_key={_apiKey}&format=json&autocorrect=1";

        try
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.GetAsync(new Uri(requestUri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Last.fm returned {Status}", response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Last.fm request failed: {Message}", ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Last.fm returned malformed JSON: {Message}", ex.Message);
            return null;
        }
    }

    private static LookupResult Parse(JsonElement root)
    {
        if (!root.TryGetProperty("toptags", out var topTags)
            || !topTags.TryGetProperty("tag", out var tagArray)
            || tagArray.ValueKind != JsonValueKind.Array
            || tagArray.GetArrayLength() == 0)
        {
            return LookupResult.Empty;
        }

        // Last.fm "count" is 0..100. Normalise relative to the highest value in the response —
        // gives a sensible confidence even for tracks where the top tag is at count 50.
        var maxCount = 1;
        foreach (var t in tagArray.EnumerateArray())
        {
            if (t.TryGetProperty("count", out var countElem) && countElem.TryGetInt32(out var c) && c > maxCount)
            {
                maxCount = c;
            }
        }

        var candidates = new List<GenreCandidate>();
        foreach (var t in tagArray.EnumerateArray())
        {
            if (!t.TryGetProperty("name", out var nameElem)) continue;
            var name = nameElem.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var count = t.TryGetProperty("count", out var countElem) && countElem.TryGetInt32(out var c) ? c : 1;
            candidates.Add(new GenreCandidate(name, Math.Clamp((double)count / maxCount * TagWeight, 0, 1), ProviderName));
        }
        return new LookupResult(candidates.OrderByDescending(c => c.Confidence).ToList(), []);
    }
}
