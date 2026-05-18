using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis.Internal;

namespace RayTagger.Analysis;

/// <summary>
/// Probes a native binary by invoking it with a "version" argument. Caches per-process so we
/// don't fork+exec on every scan-startup check. See docs/ARCHITECTURE.md §3.
/// </summary>
public sealed class AnalysisToolProbe : IAnalysisToolProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlyList<string> DefaultVersionArgs = ["--version"];

    private readonly NativeProcessRunner _runner;
    private readonly Dictionary<string, ToolProbeResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public AnalysisToolProbe(NativeProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<ToolProbeResult> ProbeAsync(
        string executable,
        IReadOnlyList<string>? versionArguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        lock (_lock)
        {
            if (_cache.TryGetValue(executable, out var cached))
            {
                return cached;
            }
        }

        var result = await ProbeUncachedAsync(executable, versionArguments ?? DefaultVersionArgs, cancellationToken)
            .ConfigureAwait(false);

        lock (_lock)
        {
            _cache[executable] = result;
        }

        return result;
    }

    private async Task<ToolProbeResult> ProbeUncachedAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(executable, args, ProbeTimeout, cancellationToken).ConfigureAwait(false);

            // Non-zero exit doesn't mean the binary is missing — many extractors (essentia,
            // fpcalc) don't recognise `--version` and exit 1 with a usage banner. The fact that
            // they STARTED is what we care about. Mark as available but capture stderr so the
            // operator has context if something downstream then misbehaves.
            if (!result.Succeeded)
            {
                return new ToolProbeResult(
                    executable,
                    IsAvailable: true,
                    Version: null,
                    ErrorMessage: $"Version probe returned exit code {result.ExitCode} (binary is on PATH, but its --version flag is unrecognised).");
            }

            var version = ExtractFirstLine(result.StandardOutput) ?? ExtractFirstLine(result.StandardError);
            return new ToolProbeResult(executable, IsAvailable: true, Version: version, ErrorMessage: null);
        }
        catch (Win32Exception ex)
        {
            // ENOENT / "file not found" path: this IS the definitive "binary not on PATH" signal.
            return new ToolProbeResult(executable, IsAvailable: false, Version: null, ErrorMessage: ex.Message);
        }
        catch (TimeoutException ex)
        {
            // A 5s probe timeout almost always means the binary is wedged, not missing — but in
            // either case the analyzer can't use it. Report as unavailable so we don't enqueue
            // per-track timeouts on a known-broken install.
            return new ToolProbeResult(executable, IsAvailable: false, Version: null, ErrorMessage: ex.Message);
        }
    }

    private static string? ExtractFirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var newline = text.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
