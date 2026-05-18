using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Models;
using FsFile = System.IO.File;

namespace RayTagger.Analysis;

/// <summary>
/// Default <see cref="IEssentiaAnalysisService"/>. Runs Essentia exactly once per
/// (path, last-write-time) tuple and shares the parsed result with every caller. Per-track
/// failures are captured in the cached task so they propagate consistently to all consumers.
/// </summary>
/// <remarks>
/// The cache is unbounded for the lifetime of the service — fine because the service is scoped
/// to a single scan invocation, which processes hundreds of tracks at most, each storing ~6
/// scalars. If scans ever grow to 10k+ tracks, swap this for an LRU.
/// </remarks>
public sealed class EssentiaAnalysisService : IEssentiaAnalysisService
{
    public const string ProviderName = "essentia";
    public const string Executable = "essentia_streaming_extractor_music";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly NativeProcessRunner _runner;
    private readonly ILogger<EssentiaAnalysisService> _logger;
    private readonly TimeSpan _timeout;
    private readonly string _executablePath;

    private readonly ConcurrentDictionary<string, Lazy<Task<EssentiaResult?>>> _cache
        = new(StringComparer.Ordinal);

    public EssentiaAnalysisService(
        NativeProcessRunner runner,
        ILogger<EssentiaAnalysisService> logger,
        TimeSpan? timeout = null,
        string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;
        // Absolute path from the bootstrapper takes precedence; otherwise we fall back to PATH
        // resolution by passing the bare binary name to Process.Start.
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? Executable : executablePath;
    }

    public async Task<EssentiaResult?> RunAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        // mtime tick is part of the key so a re-tagged file gets re-analysed within the same
        // process, instead of returning a stale cached result.
        var key = $"{file.Path}|{file.LastModifiedUtc.Ticks}";

        // The shared Task runs with no caller's cancellation — each caller WaitAsyncs its own ct
        // on the returned Task so one caller's cancel doesn't propagate to siblings waiting for
        // the same result. Failures are evicted so transient errors retry on the next call.
        var lazy = _cache.GetOrAdd(
            key,
            cacheKey => new Lazy<Task<EssentiaResult?>>(
                () => RunEssentiaAsync(file, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _cache.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }
    }

    private async Task<EssentiaResult?> RunEssentiaAsync(
        TrackFile file, CancellationToken cancellationToken)
    {
        // Essentia writes the analysis JSON to a path we pass as an argument. Use a unique temp
        // file so parallel scans of the same library don't collide.
        var tempJson = Path.Combine(Path.GetTempPath(), $"tagger-essentia-{Guid.NewGuid():N}.json");

        try
        {
            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(
                    _executablePath,
                    [file.Path, tempJson],
                    _timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                throw new AnalysisException(
                    $"'{_executablePath}' could not be started. Run `tagger setup` to auto-install Essentia, or place it on PATH (see docs/INSTALL.md).",
                    ex, analyzer: ProviderName, filePath: file.Path);
            }

            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "{Executable} exited with {ExitCode} for {Path}: {Stderr}",
                    Executable, result.ExitCode, file.Path, result.StandardError);
                return null;
            }

            if (!FsFile.Exists(tempJson))
            {
                _logger.LogWarning("Essentia ran successfully but produced no output file for {Path}", file.Path);
                return null;
            }

            using var stream = FsFile.OpenRead(tempJson);
            return EssentiaJsonParser.Parse(stream);
        }
        finally
        {
            try
            {
                if (FsFile.Exists(tempJson))
                {
                    FsFile.Delete(tempJson);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; OS reclaims /tmp eventually.
            }
        }
    }
}
