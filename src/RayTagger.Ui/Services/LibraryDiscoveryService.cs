using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Metadata;

namespace RayTagger.Ui.Services;

/// <summary>
/// Fast first-pass over a music folder: enumerates audio files (via <see cref="IFileDiscoveryService"/>)
/// and reads their existing tags (via <see cref="ITagReader"/>) so the UI can populate the results
/// grid with rows the user can already eyeball — well before the analyze/lookup/rules pipeline has
/// done its expensive work.
/// </summary>
/// <remarks>
/// Uses a bounded-channel fan-out identical in shape to <see cref="TagPipeline"/>'s, so the FS-walk
/// producer doesn't run far ahead of the tag-reading workers on huge libraries. Per-file failures
/// (corrupt header, IO error) surface as a <see cref="TrackPreview"/> with <see cref="TrackPreview.ErrorMessage"/>
/// populated rather than aborting the whole stream — the UI shows them as ERR rows.
/// </remarks>
public sealed class LibraryDiscoveryService
{
    private readonly IFileDiscoveryService _discovery;
    private readonly ITagReader _reader;
    private readonly ILogger<LibraryDiscoveryService> _logger;

    public LibraryDiscoveryService(
        IFileDiscoveryService discovery,
        ITagReader reader,
        ILogger<LibraryDiscoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        _discovery = discovery;
        _reader = reader;
        _logger = logger;
    }

    /// <summary>
    /// Streams a <see cref="TrackPreview"/> for every audio file in <paramref name="scan"/>'s
    /// source directory. Parallelism follows <see cref="ScanOptions.EffectiveParallelism"/>.
    /// </summary>
    public async IAsyncEnumerable<TrackPreview> DiscoverAsync(
        ScanOptions scan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var parallelism = Math.Max(1, scan.EffectiveParallelism);

        if (parallelism == 1)
        {
            // Sequential path — deterministic ordering, easier to debug. Tag-reads are cheap
            // (~10-50ms each) so even a 1000-track library here finishes in well under a minute.
            await foreach (var file in _discovery.EnumerateAsync(scan, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ReadPreview(file);
            }
            yield break;
        }

        await foreach (var preview in RunParallelAsync(scan, parallelism, cancellationToken).ConfigureAwait(false))
        {
            yield return preview;
        }
    }

    /// <summary>
    /// Channel-based fan-out: one producer feeds the FS-walk into the work channel; N workers
    /// drain it, read tags, and push <see cref="TrackPreview"/>s into the output channel. Output
    /// order is NOT discovery order — workers race, same trade-off as <see cref="TagPipeline"/>.
    /// </summary>
    private async IAsyncEnumerable<TrackPreview> RunParallelAsync(
        ScanOptions scan,
        int parallelism,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workChannel = Channel.CreateBounded<TrackFile>(new BoundedChannelOptions(parallelism * 4)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var outputChannel = Channel.CreateUnbounded<TrackPreview>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var file in _discovery.EnumerateAsync(scan, cancellationToken).ConfigureAwait(false))
                {
                    await workChannel.Writer.WriteAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                workChannel.Writer.Complete();
            }
        }, cancellationToken);

        var workerTasks = Enumerable.Range(0, parallelism).Select(_ => Task.Run(async () =>
        {
            await foreach (var file in workChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await outputChannel.Writer.WriteAsync(ReadPreview(file), cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken)).ToArray();

        var completionTask = Task.WhenAll(workerTasks).ContinueWith(
            _ => outputChannel.Writer.Complete(),
            TaskScheduler.Default);

        await foreach (var preview in outputChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return preview;
        }

        await producerTask.ConfigureAwait(false);
        await completionTask.ConfigureAwait(false);
        await Task.WhenAll(workerTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <paramref name="file"/>'s tags and wraps the result (or the error message) in a
    /// <see cref="TrackPreview"/>. Never throws — per-file failures isolate so the rest of the
    /// library still streams.
    /// </summary>
    private TrackPreview ReadPreview(TrackFile file)
    {
        try
        {
            var tags = _reader.Read(file.Path);
            return new TrackPreview(file, tags, ErrorMessage: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or MetadataException)
        {
            _logger.LogDebug(ex, "Discovery read failed for {Path}", file.Path);
            return new TrackPreview(file, TrackTags.Empty, ErrorMessage: ex.Message);
        }
    }
}

/// <summary>
/// One row emitted by <see cref="LibraryDiscoveryService"/>. <see cref="ErrorMessage"/> is set
/// when the tag read failed — the UI renders such rows with an ERR status badge and a tooltip.
/// </summary>
public sealed record TrackPreview(TrackFile File, TrackTags Existing, string? ErrorMessage);
