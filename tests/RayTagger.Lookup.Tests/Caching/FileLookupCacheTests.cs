using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Models;
using RayTagger.Lookup.Caching;

namespace RayTagger.Lookup.Tests.Caching;

public sealed class FileLookupCacheTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "tagger-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FileLookupCache _cache;

    public FileLookupCacheTests()
    {
        _cache = new FileLookupCache(_tempDir, NullLogger<FileLookupCache>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Roundtrips_a_lookup_result()
    {
        var result = new LookupResult(
            [new GenreCandidate("House", 0.9, "musicbrainz")],
            [new GenreCandidate("Deep House", 0.95, "discogs")],
            MbRecordingId: Guid.NewGuid());

        await _cache.SetAsync("test_key", result);
        var read = await _cache.GetAsync("test_key", TimeSpan.FromMinutes(1));

        read.Should().NotBeNull();
        read!.GenreCandidates.Should().HaveCount(1);
        read.GenreCandidates[0].Value.Should().Be("House");
        read.SubGenreCandidates[0].Value.Should().Be("Deep House");
        read.MbRecordingId.Should().NotBeNull();
    }

    [Fact]
    public async Task Returns_null_for_missing_key()
    {
        var result = await _cache.GetAsync("does_not_exist", TimeSpan.FromHours(1));
        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_entry_exceeds_ttl()
    {
        await _cache.SetAsync("stale", LookupResult.Empty);
        // File was just written; setting TTL to negative forces "stale" without sleeping.
        var result = await _cache.GetAsync("stale", TimeSpan.FromMilliseconds(-1));
        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_file_is_corrupt_and_does_not_throw()
    {
        var path = Path.Combine(_tempDir, "corrupt.json");
        await File.WriteAllTextAsync(path, "{not valid json");

        var result = await _cache.GetAsync("corrupt", TimeSpan.FromHours(1));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Overwrites_existing_entry_atomically()
    {
        await _cache.SetAsync("k", new LookupResult([new GenreCandidate("First", 0.5, "x")], []));
        await _cache.SetAsync("k", new LookupResult([new GenreCandidate("Second", 0.9, "y")], []));

        var read = await _cache.GetAsync("k", TimeSpan.FromHours(1));
        read!.GenreCandidates[0].Value.Should().Be("Second");
    }
}
