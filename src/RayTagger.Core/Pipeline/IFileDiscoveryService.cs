using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Stage 1 of the pipeline: walks the configured scan root and emits <see cref="TrackFile"/>s for
/// every audio file that passes the extension filter and glob filters. Returns lazily so the
/// caller can start processing as files appear, without waiting for the full walk.
/// </summary>
public interface IFileDiscoveryService
{
    IAsyncEnumerable<TrackFile> EnumerateAsync(ScanOptions scan, CancellationToken cancellationToken = default);
}
