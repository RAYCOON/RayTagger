using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests;

/// <summary>
/// Pins the cache-key contract that lets BPM / Key / Energy analyzers share one Essentia fork
/// per track. Essentia is always invoked with its default range, so the key is just
/// (path, mtime) — the per-genre tempo hint is applied AFTER detection by
/// <see cref="EssentiaBpmAnalyzer"/> and doesn't enter the cache identity.
/// </summary>
public class EssentiaAnalysisServiceCacheKeyTests
{
    private static TrackFile File(string path = "/fake/song.mp3", DateTime? mtime = null) =>
        new(path, AudioFormat.Mp3, 1024, mtime ?? new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Same_file_yields_same_key()
    {
        var f = File();

        EssentiaAnalysisService.BuildCacheKey(f)
            .Should().Be(EssentiaAnalysisService.BuildCacheKey(f));
    }

    [Fact]
    public void Different_mtime_yields_different_key()
    {
        // A re-tagged file (new mtime) must re-analyse; the cache must not cling to stale results.
        var k1 = EssentiaAnalysisService.BuildCacheKey(File(mtime: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var k2 = EssentiaAnalysisService.BuildCacheKey(File(mtime: new DateTime(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc)));

        k1.Should().NotBe(k2);
    }

    [Fact]
    public void Different_files_yield_different_keys()
    {
        var k1 = EssentiaAnalysisService.BuildCacheKey(File("/a/track1.mp3"));
        var k2 = EssentiaAnalysisService.BuildCacheKey(File("/a/track2.mp3"));

        k1.Should().NotBe(k2);
    }
}
