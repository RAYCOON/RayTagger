using Microsoft.Extensions.Logging;

namespace RayTagger.Analysis.Bootstrap;

/// <summary>
/// Combines an <see cref="IAnalysisToolProbe"/> with an optional <see cref="INativeToolBootstrapper"/>
/// into a single "give me a usable path to this binary" entry point. Resolution order:
/// <list type="number">
///   <item>PATH probe — if the binary starts cleanly there, use the bare name.</item>
///   <item>Bootstrapper cache — already-downloaded copy, no network.</item>
///   <item>Bootstrapper download (if not disabled by the caller) — fetches + verifies.</item>
/// </list>
/// Returns <c>null</c> when none of those produce a working binary; callers treat that as "this
/// dimension is unavailable" and continue.
/// </summary>
public sealed class NativeToolResolver
{
    private readonly IAnalysisToolProbe _probe;
    private readonly INativeToolBootstrapper? _bootstrapper;
    private readonly bool _autoBootstrap;
    private readonly ILogger<NativeToolResolver> _logger;

    public NativeToolResolver(
        IAnalysisToolProbe probe,
        INativeToolBootstrapper? bootstrapper,
        bool autoBootstrap,
        ILogger<NativeToolResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(logger);

        _probe = probe;
        _bootstrapper = bootstrapper;
        _autoBootstrap = autoBootstrap;
        _logger = logger;
    }

    /// <summary>
    /// Direct access to the underlying bootstrapper for callers that need to fetch artifacts
    /// other than analysis binaries (e.g. TF model files via
    /// <see cref="INativeToolBootstrapper.EnsureModelAsync"/>). <c>null</c> when no
    /// <c>native-tools.yaml</c> manifest was found — the caller should treat that as
    /// "auto-bootstrap unavailable" and report the dependency as missing.
    /// </summary>
    public INativeToolBootstrapper? Bootstrapper => _bootstrapper;

    /// <summary>
    /// Returns the path Tagger should pass to <c>Process.Start</c>: either the bare binary name
    /// (when PATH works) or an absolute path (when the bootstrapper produced one). <c>null</c>
    /// means the tool is unavailable for this scan.
    /// </summary>
    public async Task<NativeToolResolution?> ResolveAsync(string toolName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var pathProbe = await _probe.ProbeAsync(toolName, cancellationToken: ct).ConfigureAwait(false);
        if (pathProbe.IsAvailable)
        {
            return new NativeToolResolution(toolName, pathProbe, Source: NativeToolResolutionSource.Path);
        }

        if (_bootstrapper is null)
        {
            return null;
        }

        var cached = _bootstrapper.TryResolveCached(toolName);
        if (cached is not null)
        {
            var probe = await _probe.ProbeAsync(cached, cancellationToken: ct).ConfigureAwait(false);
            return probe.IsAvailable
                ? new NativeToolResolution(cached, probe, NativeToolResolutionSource.Cache)
                : null;
        }

        if (!_autoBootstrap)
        {
            _logger.LogDebug("Tool {Tool} not on PATH and auto-bootstrap is disabled.", toolName);
            return null;
        }

        try
        {
            var downloaded = await _bootstrapper.EnsureAsync(toolName, ct).ConfigureAwait(false);
            var probe = await _probe.ProbeAsync(downloaded, cancellationToken: ct).ConfigureAwait(false);
            return probe.IsAvailable
                ? new NativeToolResolution(downloaded, probe, NativeToolResolutionSource.Downloaded)
                : null;
        }
        catch (NativeToolBootstrapException ex)
        {
            _logger.LogWarning(ex, "Bootstrap failed for {Tool}; dimension will be unavailable.", toolName);
            return null;
        }
    }
}

/// <summary>How the binary path was obtained — used for user-facing status banners.</summary>
public enum NativeToolResolutionSource
{
    /// <summary>Binary was found on PATH; no download or cache lookup happened.</summary>
    Path,

    /// <summary>Binary was already present in the bootstrapper cache from a previous run.</summary>
    Cache,

    /// <summary>Binary was downloaded and extracted during this resolution.</summary>
    Downloaded,
}

/// <summary>Outcome of <see cref="NativeToolResolver.ResolveAsync"/>.</summary>
/// <param name="ExecutablePath">Bare binary name (PATH lookup) or absolute path (cached/downloaded).</param>
/// <param name="Probe">Result of the probe that confirmed the binary actually runs.</param>
/// <param name="Source">Where the binary came from.</param>
public sealed record NativeToolResolution(string ExecutablePath, ToolProbeResult Probe, NativeToolResolutionSource Source);
