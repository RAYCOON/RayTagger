namespace RayTagger.Analysis;

/// <summary>
/// Discovers whether a native analysis binary is available on PATH. Used at host startup to log
/// a friendly availability report and to graceful-degrade — a missing tool disables its analyzer
/// rather than crashing the scan. See docs/ARCHITECTURE.md §3.
/// </summary>
public interface IAnalysisToolProbe
{
    /// <summary>Checks whether <paramref name="executable"/> is on PATH and runs successfully.</summary>
    /// <param name="executable">Binary name (e.g. <c>fpcalc</c>) or absolute path.</param>
    /// <param name="versionArguments">
    /// Arguments that print version info quickly and exit 0 on a healthy install. Defaults to
    /// <c>--version</c>; some tools need <c>-version</c> or <c>--help</c>.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    Task<ToolProbeResult> ProbeAsync(string executable, IReadOnlyList<string>? versionArguments = null, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a single <see cref="IAnalysisToolProbe.ProbeAsync"/> call.</summary>
public sealed record ToolProbeResult(
    string Executable,
    bool IsAvailable,
    string? Version,
    string? ErrorMessage);
