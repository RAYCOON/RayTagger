using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>Detects beats-per-minute for an audio file. See docs/ARCHITECTURE.md §3.</summary>
/// <remarks>
/// The <see cref="BpmTempoRange"/> parameter is the per-track genre-resolved tempo hint. Essentia
/// itself always runs with its built-in default range; the analyzer applies the hint AFTER
/// detection — fold (×2/÷2) for out-of-range values, snap-to-grid for in-range, with a dark-blue
/// "forced fallback" highlight when even the folded result lies outside the configured interval.
/// When the hint is <c>null</c> (no genre tag / no rule), the analyzer returns the raw Essentia
/// value and lets the pipeline-level snap handle final cleanup.
/// </remarks>
public interface IBpmAnalyzer
{
    /// <summary>Stable, lowercase identifier used in config (<c>analysis.bpm.provider</c>) and logs.</summary>
    string Name { get; }

    Task<BpmResult> AnalyzeAsync(
        TrackFile file,
        BpmTempoRange? tempoRange = null,
        CancellationToken cancellationToken = default);
}

public interface IKeyAnalyzer
{
    string Name { get; }
    Task<KeyResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default);
}

public interface IEnergyAnalyzer
{
    string Name { get; }
    Task<EnergyResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default);
}

public interface IFingerprintAnalyzer
{
    string Name { get; }
    Task<FingerprintResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default);
}
