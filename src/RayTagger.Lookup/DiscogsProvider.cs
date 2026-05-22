using System.Text.Json;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup.Internal;

namespace RayTagger.Lookup;

/// <summary>
/// Discogs release-search provider. Genres and (especially) styles in Discogs are the best
/// available taxonomy for electronic music — far more granular than MusicBrainz or Last.fm.
/// Confidence ranks "style" higher than "genre" because Discogs styles are the editor-curated,
/// fine-grained labels (e.g. "Deep House"), whereas "genre" is the broad bucket ("Electronic").
/// </summary>
public sealed class DiscogsProvider : IMetadataProvider
{
    public const string ProviderName = "discogs";
    private const double StyleWeight = 0.9;
    private const double GenreWeight = 0.6;

    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly ILogger<DiscogsProvider> _logger;
    private readonly RateLimiter _rateLimiter;

    public DiscogsProvider(
        HttpClient httpClient,
        string token,
        ILogger<DiscogsProvider> logger,
        TimeSpan minRequestInterval)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(minRequestInterval, TimeSpan.Zero);

        _httpClient = httpClient;
        _token = token;
        _logger = logger;
        _rateLimiter = new RateLimiter(minRequestInterval);
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
        var requestUri = $"database/search?type=release&artist={artist}&track={title}&token={_token}";

        try
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.GetAsync(new Uri(requestUri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Discogs returned {Status}", response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Discogs request failed: {Message}", ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discogs returned malformed JSON: {Message}", ex.Message);
            return null;
        }
    }

    private static LookupResult Parse(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return LookupResult.Empty;
        }

        var styles = new List<GenreCandidate>();
        var genres = new List<GenreCandidate>();

        // Aggregate across the first few results — multiple releases of the same track usually
        // share styles, so taking the top hit alone would discard reinforcing evidence.
        var consumed = 0;
        foreach (var hit in results.EnumerateArray())
        {
            if (consumed++ >= 5) break;
            ReadStringArray(hit, "style", StyleWeight, styles);
            ReadStringArray(hit, "genre", GenreWeight, genres);
        }

        var combined = MergeRanked(styles.Concat(genres));
        // Discogs' "style" is the sub-genre slot (e.g. "Deep House"), "genre" the broad bucket
        // ("Electronic") — feed them to the right candidate list so downstream mapping rules can
        // pick by precision.
        return new LookupResult(
            GenreCandidates: combined,
            SubGenreCandidates: MergeRanked(styles));
    }

    private static void ReadStringArray(JsonElement root, string fieldName, double weight, List<GenreCandidate> sink)
    {
        if (!root.TryGetProperty(fieldName, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            sink.Add(new GenreCandidate(value, weight, ProviderName));
        }
    }

    private static List<GenreCandidate> MergeRanked(IEnumerable<GenreCandidate> source)
    {
        var byValue = new Dictionary<string, GenreCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in source)
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
