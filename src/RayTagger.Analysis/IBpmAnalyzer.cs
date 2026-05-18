using RayTagger.Core.Models;

namespace RayTagger.Analysis;

/// <summary>Detects beats-per-minute for an audio file. See docs/ARCHITECTURE.md §3.</summary>
public interface IBpmAnalyzer
{
    /// <summary>Stable, lowercase identifier used in config (<c>analysis.bpm.provider</c>) and logs.</summary>
    string Name { get; }

    Task<BpmResult> AnalyzeAsync(TrackFile file, CancellationToken cancellationToken = default);
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
