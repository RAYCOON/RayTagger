using System.Diagnostics;

namespace RayTagger.Lookup.Internal;

/// <summary>
/// Per-provider rate limit. Enforces a minimum interval between consecutive requests, serialised
/// through a <see cref="SemaphoreSlim"/> so concurrent callers queue rather than racing.
/// </summary>
/// <remarks>
/// MusicBrainz publishes 1 req/s, AcoustID 3 req/s, Discogs 60/min (≈ 1/s for authenticated tokens),
/// Last.fm has no documented per-IP limit (we self-throttle at 5 req/s). Each provider wires its
/// own instance.
/// </remarks>
// SemaphoreSlim.Dispose() only matters when AvailableWaitHandle has been touched, which we
// never do — the runtime can collect the gate without explicit Dispose, and forcing providers
// to be IDisposable just to satisfy the analyzer would propagate noise upward. Suppress CA1001
// on this class.
#pragma warning disable CA1001
internal sealed class RateLimiter
#pragma warning restore CA1001
{
    private readonly TimeSpan _minInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLastCall = Stopwatch.StartNew();

    public RateLimiter(TimeSpan minInterval)
    {
        _minInterval = minInterval;
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = _sinceLastCall.Elapsed;
            if (elapsed < _minInterval)
            {
                await Task.Delay(_minInterval - elapsed, cancellationToken).ConfigureAwait(false);
            }
            _sinceLastCall.Restart();
        }
        finally
        {
            _gate.Release();
        }
    }
}
