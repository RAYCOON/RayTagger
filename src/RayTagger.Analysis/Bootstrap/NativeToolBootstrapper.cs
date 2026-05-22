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
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _modelsInFlight =
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

    public IReadOnlyCollection<string> KnownModels => _manifest.Models.Keys;

    public string? TryResolveCachedModel(string modelKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);

        if (!_manifest.Models.TryGetValue(modelKey, out var entry))
        {
            return null;
        }

        var dir = ModelDirectory(modelKey);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        // Version-sentinel mismatch invalidates the cache.
        var sentinel = Path.Combine(dir, VersionSentinelFileName);
        if (!File.Exists(sentinel))
        {
            return null;
        }
        try
        {
            var cachedVersion = File.ReadAllText(sentinel).Trim();
            if (!string.Equals(cachedVersion, entry.Version, StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        // Every declared file must be present (sentinel alone is insufficient — a partial
        // download might've left the dir half-populated).
        foreach (var file in entry.Files)
        {
            var fileName = ResolveModelFileName(file);
            if (!File.Exists(Path.Combine(dir, fileName)))
            {
                return null;
            }
        }
        return dir;
    }

    public async Task<string> EnsureModelAsync(string modelKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);

        // Same shared-Task pattern as EnsureAsync — concurrent callers for the same model wait
        // on one Lazy; transient failures evict the slot so retries restart from scratch.
        var lazy = _modelsInFlight.GetOrAdd(modelKey,
            key => new Lazy<Task<string>>(
                () => EnsureModelUncachedAsync(key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _modelsInFlight.TryRemove(KeyValuePair.Create(modelKey, lazy));
            throw;
        }
    }

    private async Task<string> EnsureModelUncachedAsync(string modelKey, CancellationToken cancellationToken)
    {
        if (!_manifest.Models.TryGetValue(modelKey, out var entry))
        {
            throw new NativeToolBootstrapException(
                $"Model '{modelKey}' is not declared in the native-tools manifest.");
        }

        var cached = TryResolveCachedModel(modelKey);
        if (cached is not null)
        {
            _logger.LogDebug("Model {Model} already cached at {Path}", modelKey, cached);
            return cached;
        }

        var modelDir = ModelDirectory(modelKey);
        var stageDir = Path.Combine(Path.GetDirectoryName(modelDir)!, $".staging-{modelKey}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDir);

        try
        {
            // Download every file into the staging dir; verify each by SHA-256 before any move.
            foreach (var file in entry.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = ResolveModelFileName(file);
                var targetPath = Path.Combine(stageDir, fileName);
                await DownloadFileAsync(file.Url, targetPath, cancellationToken).ConfigureAwait(false);
                await VerifyHashAsync(targetPath, file.Sha256, cancellationToken).ConfigureAwait(false);
            }

            // Write the version sentinel — last, so partial-staging cleanup doesn't leave a
            // false-positive cache hit.
            await File.WriteAllTextAsync(
                Path.Combine(stageDir, VersionSentinelFileName),
                entry.Version,
                cancellationToken).ConfigureAwait(false);

            // Atomic-ish promote: delete the old dir (if any) and rename staging into place.
            if (Directory.Exists(modelDir))
            {
                Directory.Delete(modelDir, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(modelDir)!);
            Directory.Move(stageDir, modelDir);

            _logger.LogInformation("Model {Model} bootstrapped to {Path}", modelKey, modelDir);
            return modelDir;
        }
        catch (Exception ex) when (ex is not NativeToolBootstrapException and not OperationCanceledException)
        {
            throw new NativeToolBootstrapException(
                $"Failed to bootstrap model '{modelKey}': {ex.Message}", ex);
        }
        finally
        {
            TryCleanup(stageDir);
        }
    }

    private async Task DownloadFileAsync(string url, string targetPath, CancellationToken ct)
    {
        var uri = new Uri(url, UriKind.Absolute);
        _logger.LogInformation("Downloading {Url}", url);

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var http = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(targetPath);
        await http.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Models live next to the tools cache, NOT inside it. The existing <see cref="CacheRoot"/>
    /// resolves to <c>&lt;data-dir&gt;/tools</c> by default (and <c>cache_directory</c> verbatim when
    /// the manifest overrides it) — going through it would put models at
    /// <c>&lt;data-dir&gt;/tools/models/...</c> which doesn't match the published cache layout in
    /// <c>docs/PLAN_GENRE_CLASSIFICATION.md §4.3</c>.
    /// </summary>
    private string ModelsRoot =>
        string.IsNullOrWhiteSpace(_manifest.CacheDirectory)
            ? Path.Combine(_dataDirs.GetDataDirectory(), "models")
            : Path.Combine(_manifest.CacheDirectory, "models");

    private string ModelDirectory(string modelKey) => Path.Combine(ModelsRoot, modelKey);

    private static string ResolveModelFileName(NativeModelFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.RenameTo))
        {
            return file.RenameTo;
        }
        var uri = new Uri(file.Url, UriKind.Absolute);
        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    private const string VersionSentinelFileName = ".version";

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

    public async Task<string> EnsureAsync(string toolName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        // The shared Task must not carry any single caller's cancellation — otherwise the first
        // caller cancelling poisons the result for every other concurrent consumer. We start the
        // work with a token tied to no caller and let each caller WaitAsync its own ct on the
        // returned Task. Failures are evicted so transient errors don't pin a bad result for the
        // lifetime of the process.
        var lazy = _inFlight.GetOrAdd(toolName,
            key => new Lazy<Task<string>>(
                () => EnsureUncachedAsync(key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            // A faulted Task stays pinned in _inFlight forever otherwise — drop it so the next
            // EnsureAsync call retries from scratch instead of replaying the same failure.
            _inFlight.TryRemove(KeyValuePair.Create(toolName, lazy));
            throw;
        }
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
