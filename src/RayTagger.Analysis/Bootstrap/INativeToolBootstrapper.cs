namespace RayTagger.Analysis.Bootstrap;

/// <summary>
/// Provides absolute paths to native analysis binaries, downloading + extracting them on first
/// use according to a YAML manifest. The contract is: if <see cref="EnsureAsync"/> returns a path,
/// callers can pass it straight to <c>Process.Start</c> without further PATH probing.
/// </summary>
public interface INativeToolBootstrapper
{
    /// <summary>
    /// Returns the absolute path of <paramref name="toolName"/> after making sure it's available
    /// locally — either by finding a cached copy or by downloading + verifying + extracting it.
    /// </summary>
    /// <exception cref="NativeToolBootstrapException">
    /// Thrown when the tool is not in the manifest, no source matches the current runtime, the
    /// download fails, the SHA-256 doesn't match, or extraction can't locate the binary.
    /// </exception>
    Task<string> EnsureAsync(string toolName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the cached binary path if it exists, otherwise <c>null</c> — without performing any
    /// network I/O. Used by the probe layer to fall through to bootstrap only when nothing is
    /// already on disk.
    /// </summary>
    string? TryResolveCached(string toolName);

    /// <summary>The set of tool names declared in the manifest (used by <c>tagger setup</c>).</summary>
    IReadOnlyCollection<string> KnownTools { get; }
}
