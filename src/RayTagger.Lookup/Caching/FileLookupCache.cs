using System.Text.Json;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;
using FsFile = System.IO.File;

namespace RayTagger.Lookup.Caching;

/// <summary>
/// Default <see cref="ILookupCache"/>. Stores one JSON document per cache key under the configured
/// directory. TTL is enforced on read by comparing the file's <c>LastWriteTimeUtc</c> against
/// <see cref="DateTime.UtcNow"/>. Writes are atomic: serialise to a temp file in the same
/// directory, then rename — so a crash mid-write can never leave a half-written cache entry.
/// </summary>
public sealed class FileLookupCache : ILookupCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _directory;
    private readonly ILogger<FileLookupCache> _logger;

    public FileLookupCache(string directory, ILogger<FileLookupCache> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);

        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    public async Task<LookupResult?> GetAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var path = Path.Combine(_directory, key + ".json");
        if (!FsFile.Exists(path))
        {
            return null;
        }
        var age = DateTime.UtcNow - FsFile.GetLastWriteTimeUtc(path);
        if (age > ttl)
        {
            _logger.LogDebug("Lookup cache stale for {Key} (age {Age})", key, age);
            return null;
        }

        try
        {
            await using var stream = FsFile.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<LookupResult>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lookup cache entry {Path} is corrupt, ignoring", path);
            return null;
        }
    }

    public async Task SetAsync(string key, LookupResult result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(result);

        var finalPath = Path.Combine(_directory, key + ".json");
        var tempPath = finalPath + ".tmp";

        try
        {
            await using (var stream = FsFile.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            // Atomic on macOS/Linux; on Windows File.Move with overwrite=true is the safe equivalent.
            FsFile.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            // Serialization or rename failed — clean the partial temp file rather than leaving
            // .tmp turds piling up in the cache directory across runs. Original exception bubbles.
            try { if (FsFile.Exists(tempPath)) FsFile.Delete(tempPath); } catch (IOException) { }
            throw;
        }
    }
}
