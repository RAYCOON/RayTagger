using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Metadata;
using RayTagger.Ui.Services;

namespace RayTagger.Ui.Tests.Services;

public class LibraryDiscoveryServiceTests
{
    private static readonly ScanOptions SequentialScan = new()
    {
        Source = "/library",
        Parallelism = 1,
    };

    private static readonly ScanOptions ParallelScan = new()
    {
        Source = "/library",
        Parallelism = 4,
    };

    [Fact]
    public async Task Emits_one_preview_per_file_sequentially_in_discovery_order()
    {
        var files = new[]
        {
            new TrackFile("/library/a.mp3", AudioFormat.Mp3, 100, DateTime.UtcNow),
            new TrackFile("/library/b.flac", AudioFormat.Flac, 200, DateTime.UtcNow),
            new TrackFile("/library/c.aiff", AudioFormat.Aiff, 300, DateTime.UtcNow),
        };
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(files));

        var reader = Substitute.For<ITagReader>();
        reader.Read(files[0].Path).Returns(new TrackTags(Title: "A", Artist: "Alpha"));
        reader.Read(files[1].Path).Returns(new TrackTags(Title: "B", Artist: "Beta"));
        reader.Read(files[2].Path).Returns(new TrackTags(Title: "C", Artist: "Gamma"));

        var service = new LibraryDiscoveryService(discovery, reader, NullLogger<LibraryDiscoveryService>.Instance);

        var previews = await CollectAsync(service.DiscoverAsync(SequentialScan));

        previews.Should().HaveCount(3);
        previews.Select(p => p.File.Path).Should().Equal(files.Select(f => f.Path),
            because: "parallelism=1 preserves the FS-walk order so the user sees a stable grid");
        previews[0].Existing.Title.Should().Be("A");
        previews[1].Existing.Artist.Should().Be("Beta");
        previews.Should().AllSatisfy(p => p.ErrorMessage.Should().BeNull());
    }

    [Fact]
    public async Task Read_failure_surfaces_as_TrackPreview_with_ErrorMessage_and_empty_tags()
    {
        // One good file, one corrupt file. The corrupt one must NOT abort the stream — it just
        // comes through with ErrorMessage populated so the UI can render an ERR row.
        var good = new TrackFile("/library/good.mp3", AudioFormat.Mp3, 100, DateTime.UtcNow);
        var bad = new TrackFile("/library/bad.flac", AudioFormat.Flac, 200, DateTime.UtcNow);
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(good, bad));

        var reader = Substitute.For<ITagReader>();
        reader.Read(good.Path).Returns(new TrackTags(Title: "Good"));
        reader.Read(bad.Path).Returns<TrackTags>(_ =>
            throw new MetadataException("unreadable header", bad.Path));

        var service = new LibraryDiscoveryService(discovery, reader, NullLogger<LibraryDiscoveryService>.Instance);

        var previews = await CollectAsync(service.DiscoverAsync(SequentialScan));

        previews.Should().HaveCount(2);
        previews[0].ErrorMessage.Should().BeNull();
        previews[0].Existing.Title.Should().Be("Good");

        previews[1].ErrorMessage.Should().NotBeNull();
        previews[1].ErrorMessage.Should().Contain("unreadable header");
        previews[1].Existing.Should().Be(TrackTags.Empty,
            because: "callers expect a sentinel Existing so the row's view-model can render with file-only metadata");
    }

    [Fact]
    public async Task Parallel_discovery_emits_all_files_even_if_out_of_order()
    {
        var files = Enumerable.Range(0, 12)
            .Select(i => new TrackFile($"/library/p{i:00}.mp3", AudioFormat.Mp3, 100, DateTime.UtcNow))
            .ToArray();
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(files));

        var reader = Substitute.For<ITagReader>();
        foreach (var f in files) reader.Read(f.Path).Returns(TrackTags.Empty);

        var service = new LibraryDiscoveryService(discovery, reader, NullLogger<LibraryDiscoveryService>.Instance);

        var previews = await CollectAsync(service.DiscoverAsync(ParallelScan));

        previews.Should().HaveCount(12);
        previews.Select(p => p.File.Path).Should().BeEquivalentTo(files.Select(f => f.Path),
            because: "parallel workers race on emission order — every file must still arrive exactly once");
    }

    [Fact]
    public async Task Cancellation_propagates_so_a_re_pick_can_replace_the_stream()
    {
        // Caller-side cancellation (user picks a different folder mid-discovery). The async-enumerator
        // must surface OperationCanceledException rather than silently completing. Pre-cancelled CT
        // means the channel-based fan-out throws on its very first Read/Write call.
        var files = Enumerable.Range(0, 10)
            .Select(i => new TrackFile($"/library/c{i}.mp3", AudioFormat.Mp3, 1, DateTime.UtcNow))
            .ToArray();
        var discovery = Substitute.For<IFileDiscoveryService>();
        discovery.EnumerateAsync(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsAsync(files));

        var reader = Substitute.For<ITagReader>();
        foreach (var f in files) reader.Read(f.Path).Returns(TrackTags.Empty);
        var service = new LibraryDiscoveryService(discovery, reader, NullLogger<LibraryDiscoveryService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () =>
        {
            await foreach (var _ in service.DiscoverAsync(ParallelScan, cts.Token)) { }
        };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async IAsyncEnumerable<TrackFile> AsAsync(params TrackFile[] files)
    {
        foreach (var file in files)
        {
            yield return file;
            await Task.Yield();
        }
    }

    private static async Task<List<TrackPreview>> CollectAsync(IAsyncEnumerable<TrackPreview> source)
    {
        var list = new List<TrackPreview>();
        await foreach (var p in source) list.Add(p);
        return list;
    }
}
