using System.Text.Json;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup.Internal;

namespace RayTagger.Lookup;

/// <summary>
/// AcoustID fingerprint-to-MBID lookup. Doesn't return genres itself — its job is to enrich the
/// <see cref="LookupQuery"/> with a <see cref="LookupResult.MbRecordingId"/> so the next provider
/// in the chain (MusicBrainz) can do an MBID-anchored lookup instead of free-text search.
/// </summary>
/// <remarks>
/// Free API key from <a href="https://acoustid.org/api-key">acoustid.org</a>. Quota is 3 req/s per
/// key. Provider does nothing when the query has no fingerprint — falls open to downstream
/// providers running their free-text path.
/// </remarks>
public sealed class AcoustIdProvider : IMetadataProvider
{
    public const string ProviderName = "acoustid";

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<AcoustIdProvider> _logger;
    private readonly RateLimiter _rateLimiter;

    public AcoustIdProvider(
        HttpClient httpClient,
        string apiKey,
        ILogger<AcoustIdProvider> logger,
        TimeSpan minRequestInterval)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(minRequestInterval, TimeSpan.Zero);

        _httpClient = httpClient;
        _apiKey = apiKey;
        _logger = logger;
        _rateLimiter = new RateLimiter(minRequestInterval);
    }

    public string Name => ProviderName;

    public bool CanHandle(LookupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return !string.IsNullOrWhiteSpace(query.Fingerprint) && query.DurationSeconds is > 0;
    }

    public async Task<LookupResult?> LookupAsync(LookupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanHandle(query)) return null;

        // POST is preferred over GET because fingerprints are 1-2 KB — too long for a URL on some
        // proxies. The form-encoded body is exactly what the AcoustID docs recommend.
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client", _apiKey),
            new KeyValuePair<string, string>("duration", query.DurationSeconds!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("fingerprint", query.Fingerprint!),
            new KeyValuePair<string, string>("meta", "recordings"),
        });

        try
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.PostAsync(new Uri("v2/lookup", UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AcoustID returned {Status}", response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "AcoustID request failed: {Message}", ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "AcoustID returned malformed JSON: {Message}", ex.Message);
            return null;
        }
    }

    private static LookupResult? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return null;
        }

        // Take the highest-scoring result that has at least one recording.
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("recordings", out var recordings)
                || recordings.ValueKind != JsonValueKind.Array
                || recordings.GetArrayLength() == 0)
            {
                continue;
            }
            if (!recordings[0].TryGetProperty("id", out var idElem)
                || !Guid.TryParse(idElem.GetString(), out var mbid))
            {
                continue;
            }
            return new LookupResult([], [], MbReleaseId: null, MbRecordingId: mbid);
        }
        return null;
    }
}
