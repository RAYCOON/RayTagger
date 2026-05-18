namespace RayTagger.Core.Configuration;

/// <summary>
/// Manifest describing where to download the native analysis tools
/// (<c>essentia_streaming_extractor_music</c>, <c>fpcalc</c>, …) from, what to verify the
/// downloads against, and how to extract them. Loaded from a separate YAML file referenced by
/// <see cref="NativeToolsOptions.ManifestFile"/> in <c>tagger.yaml</c> — kept out of the main
/// config so end users don't accidentally edit URLs/hashes maintained by Tagger.
/// </summary>
/// <remarks>
/// The shape mirrors <c>samples/native-tools.example.yaml</c>; treat that file as the documented
/// contract and update both in lockstep when the schema changes.
/// </remarks>
public sealed class NativeToolsManifest
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Override for the cache root. Empty/null = OS default
    /// (<see cref="IO.IUserDataDirectoryProvider.GetDataDirectory"/> + <c>/tools</c>).
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;

    /// <summary>Map of tool name (binary name on PATH) to its download entry.</summary>
    public Dictionary<string, NativeToolEntry> Tools { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One tool — typically pinned to a single version, with per-RID download sources.</summary>
public sealed class NativeToolEntry
{
    /// <summary>
    /// Logical version label, used as part of the cache subdirectory. Bump this when you swap in
    /// a new binary so existing installs re-download cleanly. Doesn't have to match the binary's
    /// own version string.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Map from .NET RID (e.g. <c>osx-arm64</c>) to that platform's download entry.</summary>
    public Dictionary<string, NativeToolSource> Sources { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One downloadable artifact for a specific platform.</summary>
public sealed class NativeToolSource
{
    /// <summary>Absolute HTTPS URL of the archive (or raw binary if <see cref="ArchiveFormat"/> is <c>None</c>).</summary>
    /// <remarks>
    /// CA1056 (prefer <c>Uri</c>) is suppressed: YamlDotNet binds settable <c>string</c> properties
    /// out-of-the-box. Validation in <see cref="NativeToolsManifestValidator"/> rejects anything
    /// that isn't a parseable HTTPS URL, so the typing weakness stops at the schema layer.
    /// </remarks>
#pragma warning disable CA1056
    public string Url { get; set; } = string.Empty;
#pragma warning restore CA1056

    /// <summary>Lowercase hex SHA-256 of the downloaded file, exactly 64 chars.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>How the download is packaged. See <see cref="NativeToolArchiveFormat"/>.</summary>
    public NativeToolArchiveFormat ArchiveFormat { get; set; } = NativeToolArchiveFormat.None;

    /// <summary>
    /// Relative path of the executable inside the archive. Ignored when <see cref="ArchiveFormat"/>
    /// is <c>None</c>. If empty, the bootstrapper looks for the tool's binary name at the archive root.
    /// </summary>
    public string BinaryPath { get; set; } = string.Empty;
}

public enum NativeToolArchiveFormat
{
    /// <summary>Direct binary download — no extraction, file is the binary.</summary>
    None,

    /// <summary>Tarball with gzip compression. Extracted via <c>System.Formats.Tar</c>.</summary>
    TarGz,

    /// <summary>ZIP archive. Extracted via <c>System.IO.Compression.ZipFile</c>.</summary>
    Zip,
}
