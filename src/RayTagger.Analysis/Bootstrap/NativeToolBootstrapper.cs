using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;

namespace RayTagger.Analysis.Bootstrap;

/// <summary>
/// Downloads, verifies, extracts and caches the native analysis tools declared in a
/// <see cref="NativeToolsManifest"/>. Cache layout:
/// <code>
///   &lt;cacheRoot&gt;/&lt;toolName&gt;/&lt;version&gt;/&lt;rid&gt;/&lt;binary&gt;
/// </code>
/// One <see cref="ConcurrentDictionary{TKey,TValue}"/> de-duplicates parallel <see cref="EnsureAsync"/>
/// calls for the same tool — relevant when the host probes several Essentia-derived dimensions
/// simultaneously and would otherwise race on the same download.
/// </summary>
public sealed class NativeToolBootstrapper : INativeToolBootstrapper
{
    private readonly NativeToolsManifest _manifest;
    private readonly IUserDataDirectoryProvider _dataDirs;
    private readonly HttpClient _http;
    private readonly ILogger<NativeToolBootstrapper> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _rid;

    public NativeToolBootstrapper(
        NativeToolsManifest manifest,
        IUserDataDirectoryProvider dataDirs,
        HttpClient http,
        ILogger<NativeToolBootstrapper> logger,
        string? runtimeIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(dataDirs);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _manifest = manifest;
        _dataDirs = dataDirs;
        _http = http;
        _logger = logger;
        _rid = runtimeIdentifier ?? RuntimeIdentifierResolver.Current;
    }

    public IReadOnlyCollection<string> KnownTools => _manifest.Tools.Keys;

    public string? TryResolveCached(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!_manifest.Tools.TryGetValue(toolName, out var entry))
        {
            return null;
        }

        var path = ExpectedBinaryPath(toolName, entry);
        return File.Exists(path) ? path : null;
    }

    public Task<string> EnsureAsync(string toolName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var lazy = _inFlight.GetOrAdd(toolName,
            key => new Lazy<Task<string>>(() => EnsureUncachedAsync(key, cancellationToken)));
        return lazy.Value;
    }

    private async Task<string> EnsureUncachedAsync(string toolName, CancellationToken cancellationToken)
    {
        if (!_manifest.Tools.TryGetValue(toolName, out var entry))
        {
            throw new NativeToolBootstrapException(
                $"Tool '{toolName}' is not declared in the native-tools manifest.");
        }

        if (!entry.Sources.TryGetValue(_rid, out var source))
        {
            var available = string.Join(", ", entry.Sources.Keys);
            throw new NativeToolBootstrapException(
                $"No download source for tool '{toolName}' on runtime '{_rid}'. Manifest declares: {available}.");
        }

        var binaryPath = ExpectedBinaryPath(toolName, entry);
        if (File.Exists(binaryPath))
        {
            _logger.LogDebug("Tool {Tool} already cached at {Path}", toolName, binaryPath);
            return binaryPath;
        }

        var stageDir = Path.Combine(VersionDirectory(toolName, entry), $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDir);

        try
        {
            var downloadedFile = await DownloadAsync(source, stageDir, cancellationToken).ConfigureAwait(false);
            await VerifyHashAsync(downloadedFile, source.Sha256, cancellationToken).ConfigureAwait(false);

            var extractedBinary = source.ArchiveFormat switch
            {
                NativeToolArchiveFormat.None => PromoteRawBinary(downloadedFile, toolName),
                NativeToolArchiveFormat.Zip => ExtractZip(downloadedFile, stageDir, source.BinaryPath, toolName),
                NativeToolArchiveFormat.TarGz => await ExtractTarGzAsync(downloadedFile, stageDir, source.BinaryPath, toolName, cancellationToken).ConfigureAwait(false),
                _ => throw new NativeToolBootstrapException(
                    $"Unsupported archive_format '{source.ArchiveFormat}' for tool '{toolName}'."),
            };

            var finalDir = Path.GetDirectoryName(binaryPath)!;
            Directory.CreateDirectory(finalDir);

            if (File.Exists(binaryPath))
            {
                File.Delete(binaryPath);
            }
            File.Move(extractedBinary, binaryPath);
            MakeExecutable(binaryPath);

            _logger.LogInformation("Tool {Tool} bootstrapped to {Path}", toolName, binaryPath);
            return binaryPath;
        }
        catch (Exception ex) when (ex is not NativeToolBootstrapException and not OperationCanceledException)
        {
            throw new NativeToolBootstrapException(
                $"Failed to bootstrap tool '{toolName}' for {_rid}: {ex.Message}", ex);
        }
        finally
        {
            TryCleanup(stageDir);
        }
    }

    private string CacheRoot =>
        string.IsNullOrWhiteSpace(_manifest.CacheDirectory)
            ? Path.Combine(_dataDirs.GetDataDirectory(), "tools")
            : _manifest.CacheDirectory;

    private string VersionDirectory(string toolName, NativeToolEntry entry) =>
        Path.Combine(CacheRoot, toolName, entry.Version, _rid);

    private string ExpectedBinaryPath(string toolName, NativeToolEntry entry)
    {
        var fileName = ResolveBinaryFileName(toolName);
        return Path.Combine(VersionDirectory(toolName, entry), fileName);
    }

    private static string ResolveBinaryFileName(string toolName) =>
        OperatingSystem.IsWindows() && !toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? toolName + ".exe"
            : toolName;

    private async Task<string> DownloadAsync(NativeToolSource source, string stageDir, CancellationToken ct)
    {
        var uri = new Uri(source.Url, UriKind.Absolute);
        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download.bin";
        }
        var targetPath = Path.Combine(stageDir, fileName);

        _logger.LogInformation("Downloading {Url}", source.Url);

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var http = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(targetPath);
        await http.CopyToAsync(file, ct).ConfigureAwait(false);

        return targetPath;
    }

    private static async Task VerifyHashAsync(string filePath, string expectedSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var actualBytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actualHex = Convert.ToHexStringLower(actualBytes);

        if (!string.Equals(actualHex, expectedSha256, StringComparison.Ordinal))
        {
            throw new NativeToolBootstrapException(
                $"SHA-256 mismatch for {Path.GetFileName(filePath)}. Expected {expectedSha256}, got {actualHex}.");
        }
    }

    private static string PromoteRawBinary(string downloadedFile, string toolName)
    {
        // Raw binary: rename the download to the expected file name so the staging dir contains
        // exactly the file we'll move into the final cache slot.
        var renamed = Path.Combine(Path.GetDirectoryName(downloadedFile)!, ResolveBinaryFileName(toolName));
        if (!string.Equals(renamed, downloadedFile, StringComparison.Ordinal))
        {
            File.Move(downloadedFile, renamed, overwrite: true);
        }
        return renamed;
    }

    private static string ExtractZip(string archivePath, string stageDir, string binaryPathInArchive, string toolName)
    {
        var extractDir = Path.Combine(stageDir, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
        return LocateExtractedBinary(extractDir, binaryPathInArchive, toolName);
    }

    private static async Task<string> ExtractTarGzAsync(
        string archivePath,
        string stageDir,
        string binaryPathInArchive,
        string toolName,
        CancellationToken ct)
    {
        var extractDir = Path.Combine(stageDir, "extracted");
        Directory.CreateDirectory(extractDir);

        await using var file = File.OpenRead(archivePath);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, extractDir, overwriteFiles: true, ct).ConfigureAwait(false);

        return LocateExtractedBinary(extractDir, binaryPathInArchive, toolName);
    }

    private static string LocateExtractedBinary(string extractDir, string binaryPathInArchive, string toolName)
    {
        // Defence-in-depth: .NET 7+'s TarFile/ZipFile already reject entries that resolve outside
        // the extraction root, but `binary_path` is a manifest field — confused-deputy risk if the
        // manifest is ever loaded from an untrusted source. Resolve both paths and require the
        // binary to live under extractDir before we trust it.
        var canonicalExtractDir = Path.GetFullPath(extractDir);

        if (!string.IsNullOrWhiteSpace(binaryPathInArchive))
        {
            var explicitPath = Path.GetFullPath(Path.Combine(extractDir, binaryPathInArchive));
            if (!IsUnderRoot(explicitPath, canonicalExtractDir))
            {
                throw new NativeToolBootstrapException(
                    $"binary_path '{binaryPathInArchive}' resolves outside the extraction directory — refusing.");
            }
            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }
            throw new NativeToolBootstrapException(
                $"binary_path '{binaryPathInArchive}' was not found in the extracted archive for tool '{toolName}'.");
        }

        var expectedName = ResolveBinaryFileName(toolName);
        var match = Directory.EnumerateFiles(extractDir, expectedName, SearchOption.AllDirectories)
            .Where(p => IsUnderRoot(Path.GetFullPath(p), canonicalExtractDir))
            .FirstOrDefault();
        if (match is null)
        {
            throw new NativeToolBootstrapException(
                $"Archive for tool '{toolName}' did not contain a file named '{expectedName}'. Set binary_path explicitly in the manifest.");
        }
        return match;
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.StartsWith(rootWithSep, comparer)
            || string.Equals(candidate, root, comparer);
    }

    private static void MakeExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // 0755 — owner rwx, group/other rx. Standard for native CLIs.
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                 | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, mode);
    }

    private void TryCleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not clean up staging directory {Dir}", dir);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Could not clean up staging directory {Dir}", dir);
        }
    }
}
