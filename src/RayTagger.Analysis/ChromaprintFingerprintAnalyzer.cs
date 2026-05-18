using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>
/// Computes a Chromaprint audio fingerprint by shelling out to <c>fpcalc</c>. The fingerprint is
/// the input that AcoustID expects for online lookups. See docs/ARCHITECTURE.md §3.
/// </summary>
public sealed class ChromaprintFingerprintAnalyzer : IFingerprintAnalyzer
{
    public const string ProviderName = "chromaprint";
    public const string Executable = "fpcalc";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly NativeProcessRunner _runner;
    private readonly ILogger<ChromaprintFingerprintAnalyzer> _logger;
    private readonly TimeSpan _timeout;
    private readonly string _executablePath;

    public ChromaprintFingerprintAnalyzer(
        NativeProcessRunner runner,
        ILogger<ChromaprintFingerprintAnalyzer> logger,
        TimeSpan? timeout = null,
        string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? Executable : executablePath;
    }

    public string Name => ProviderName;

    public async Task<FingerprintResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_executablePath, [file.Path], _timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            throw new AnalysisException(
                $"'{_executablePath}' could not be started. Run `tagger setup` to auto-install fpcalc, or install Chromaprint manually (see docs/INSTALL.md).",
                ex, analyzer: ProviderName, filePath: file.Path);
        }

        if (!result.Succeeded)
        {
            throw new AnalysisException(
                $"{_executablePath} exited with code {result.ExitCode}: {result.StandardError.Trim()}",
                analyzer: ProviderName, filePath: file.Path);
        }

        var parsed = ChromaprintOutputParser.Parse(result.StandardOutput);
        if (string.IsNullOrEmpty(parsed.Fingerprint))
        {
            _logger.LogWarning("fpcalc produced no fingerprint for {Path}", file.Path);
            return new FingerprintResult(Chromaprint: null, Confidence: 0);
        }

        return new FingerprintResult(parsed.Fingerprint, Confidence: 1);
    }
}
