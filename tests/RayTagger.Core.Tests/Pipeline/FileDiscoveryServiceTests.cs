using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Core.Tests.Pipeline;

public sealed class FileDiscoveryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FileDiscoveryService _sut = new();

    public FileDiscoveryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tagger-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Yields_files_with_supported_extensions_only()
    {
        TouchFiles("a.mp3", "b.flac", "c.aiff", "d.wav", "e.txt", "noext");

        var paths = await CollectPathsAsync(new ScanOptions { Source = _root });

        paths.Should().HaveCount(3);
        paths.Select(Path.GetFileName).Should().BeEquivalentTo(["a.mp3", "b.flac", "c.aiff"]);
    }

    [Fact]
    public async Task Recursive_walk_descends_into_subdirectories()
    {
        TouchFiles("a.mp3", Path.Combine("sub", "b.mp3"), Path.Combine("sub", "deep", "c.mp3"));

        var paths = await CollectPathsAsync(new ScanOptions { Source = _root, Recursive = true });

        paths.Should().HaveCount(3);
    }

    [Fact]
    public async Task Non_recursive_walk_stays_in_root()
    {
        TouchFiles("a.mp3", Path.Combine("sub", "b.mp3"));

        var paths = await CollectPathsAsync(new ScanOptions { Source = _root, Recursive = false });

        paths.Should().HaveCount(1);
        Path.GetFileName(paths[0]).Should().Be("a.mp3");
    }

    [Fact]
    public async Task Respects_formats_filter()
    {
        TouchFiles("a.mp3", "b.flac", "c.aiff");

        var paths = await CollectPathsAsync(new ScanOptions
        {
            Source = _root,
            Formats = ["mp3"],
        });

        paths.Should().HaveCount(1);
        Path.GetExtension(paths[0]).Should().Be(".mp3");
    }

    [Fact]
    public async Task Exclude_globs_skip_matching_paths()
    {
        TouchFiles(
            "a.mp3",
            Path.Combine("Promo", "b.mp3"),
            Path.Combine("Library", "c.mp3"));

        var paths = await CollectPathsAsync(new ScanOptions
        {
            Source = _root,
            ExcludeGlobs = ["Promo/**"],
        });

        paths.Should().HaveCount(2);
        paths.Select(Path.GetFileName).Should().NotContain("b.mp3");
    }

    [Fact]
    public async Task Include_globs_act_as_allowlist_when_present()
    {
        TouchFiles(
            "a.mp3",
            Path.Combine("Promo", "b.mp3"),
            Path.Combine("Library", "c.mp3"));

        var paths = await CollectPathsAsync(new ScanOptions
        {
            Source = _root,
            IncludeGlobs = ["Promo/**"],
        });

        paths.Should().HaveCount(1);
        Path.GetFileName(paths[0]).Should().Be("b.mp3");
    }

    [Fact]
    public async Task Returns_empty_when_source_directory_does_not_exist()
    {
        var paths = await CollectPathsAsync(new ScanOptions { Source = Path.Combine(_root, "missing") });

        paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Track_file_carries_size_and_mtime()
    {
        var path = Path.Combine(_root, "song.mp3");
        File.WriteAllText(path, "12345");

        var files = await CollectAsync(new ScanOptions { Source = _root });

        files.Should().HaveCount(1);
        files[0].SizeBytes.Should().Be(5);
        files[0].LastModifiedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        files[0].Format.Should().Be(AudioFormat.Mp3);
    }

    // --- helpers -----------------------------------------------------------------------

    private void TouchFiles(params string[] relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var full = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    private async Task<List<string>> CollectPathsAsync(ScanOptions options)
    {
        var files = await CollectAsync(options);
        return files.Select(f => f.Path).ToList();
    }

    private async Task<List<TrackFile>> CollectAsync(ScanOptions options)
    {
        var list = new List<TrackFile>();
        await foreach (var f in _sut.EnumerateAsync(options))
        {
            list.Add(f);
        }
        return list;
    }
}
