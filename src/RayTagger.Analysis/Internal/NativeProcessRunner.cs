using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RayTagger.Analysis.Internal;

/// <summary>
/// Abstraction over native-process invocation. The concrete
/// <see cref="NativeProcessRunner"/> implements this against <see cref="Process"/>; tests
/// can substitute a stub returning canned <see cref="ProcessResult"/> values without
/// spawning real subprocesses. Introduced for the TensorFlow genre classifier's unit
/// tests — existing analyzers still depend on the concrete type and need no changes.
/// </summary>
public interface INativeProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thin wrapper around <see cref="Process"/> that:
/// <list type="bullet">
///   <item>Uses <see cref="ProcessStartInfo.ArgumentList"/> so paths with spaces / quotes / shell
///         metacharacters can't escape into the command line.</item>
///   <item>Captures stdout and stderr in parallel.</item>
///   <item>Honours a hard timeout (kills the process tree on expiry).</item>
///   <item>Propagates outer cancellation correctly without confusing it with a timeout.</item>
/// </list>
/// Shared by every CLI-based analyzer in <c>RayTagger.Analysis</c>.
/// </summary>
public sealed class NativeProcessRunner : INativeProcessRunner
{
    private readonly ILogger<NativeProcessRunner> _logger;

    public NativeProcessRunner(ILogger<NativeProcessRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/>. Throws
    /// <see cref="TimeoutException"/> on timeout, <see cref="OperationCanceledException"/> when
    /// the caller cancels, and <see cref="System.ComponentModel.Win32Exception"/> when the
    /// executable cannot be started (typically: not on PATH).
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Default is Console.OutputEncoding, which on Windows is the OEM/ANSI codepage
            // (cp850/cp1252). Essentia/fpcalc emit UTF-8; non-ASCII bytes in error messages
            // (file paths, ffmpeg complaints) would mojibake under cp1252. Force UTF-8 on
            // both streams — harmless on macOS/Linux where it's already the default.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {executable}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout fired (caller didn't cancel). Kill the tree so child processes don't leak —
            // then drain stdout/stderr so we don't orphan the read tasks and risk unobserved
            // exceptions later. Best-effort: bounded wait so a wedged process can't stall us.
            TryKillProcessTree(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new TimeoutException(
                $"{executable} exceeded its {timeout.TotalSeconds:0.#}s timeout.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Ran {Executable} in {Elapsed}ms, exit={ExitCode}",
                executable, stopwatch.ElapsedMilliseconds, process.ExitCode);
        }

        return new ProcessResult(process.ExitCode, stdout, stderr, stopwatch.Elapsed);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort — the process may have exited between HasExited check and Kill.
        }
    }

    /// <summary>
    /// Best-effort completion of the stdout/stderr read tasks after we've already decided to
    /// abort. We swallow exceptions because by this point we're throwing TimeoutException — the
    /// read failures are noise. The bounded wait protects against a process whose stream handles
    /// aren't released even after Kill.
    /// </summary>
    private static async Task DrainAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
            // Ignored — see remarks.
        }
    }
}
