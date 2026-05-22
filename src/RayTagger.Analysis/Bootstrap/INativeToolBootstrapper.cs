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

    /// <summary>
    /// Returns the absolute path of the directory that contains all files for the TF model
    /// <paramref name="modelKey"/>, downloading missing files on first use. The directory layout
    /// matches what the Python bridge expects: every file sits flat under
    /// <c>&lt;cacheRoot&gt;/models/&lt;modelKey&gt;/</c>. A <c>.version</c> sentinel file is written so a
    /// later manifest version bump triggers re-download.
    /// </summary>
    /// <exception cref="NativeToolBootstrapException">
    /// Thrown when the model isn't in the manifest, a download fails, or a SHA-256 doesn't match.
    /// </exception>
    Task<string> EnsureModelAsync(string modelKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the cached model directory if every declared file exists AND the <c>.version</c>
    /// sentinel matches the manifest version. Otherwise <c>null</c> — no network I/O.
    /// </summary>
    string? TryResolveCachedModel(string modelKey);

    /// <summary>The set of TF model keys declared in the manifest.</summary>
    IReadOnlyCollection<string> KnownModels { get; }
}
