using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Analysis;

/// <summary>
/// Runs each configured analyzer for one track. Failures are caught per-analyzer and degrade
/// gracefully — a missing native binary disables that dimension without aborting the pipeline.
/// </summary>
public sealed class AnalysisRunner : IAnalysisRunner
{
    private readonly IBpmAnalyzer? _bpm;
    private readonly IKeyAnalyzer? _key;
    private readonly IEnergyAnalyzer? _energy;
    private readonly IFingerprintAnalyzer? _fingerprint;
    private readonly ILogger<AnalysisRunner> _logger;

    public AnalysisRunner(
        IBpmAnalyzer? bpm,
        IKeyAnalyzer? key,
        IEnergyAnalyzer? energy,
        IFingerprintAnalyzer? fingerprint,
        ILogger<AnalysisRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _bpm = bpm;
        _key = key;
        _energy = energy;
        _fingerprint = fingerprint;
        _logger = logger;
    }

    public async Task<AnalysisResult> RunAsync(TrackFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Each dimension runs in parallel — they're independent and most are I/O bound on native
        // tool startup, so this materially shortens per-file latency.
        var bpmTask = RunAnalyzerAsync(_bpm, a => a.AnalyzeAsync(file, cancellationToken), () => new BpmResult(null, 0));
        var keyTask = RunAnalyzerAsync(_key, a => a.AnalyzeAsync(file, cancellationToken), () => new KeyResult(null, 0));
        var energyTask = RunAnalyzerAsync(_energy, a => a.AnalyzeAsync(file, cancellationToken), () => new EnergyResult(null, 0));
        var fingerprintTask = RunAnalyzerAsync(_fingerprint, a => a.AnalyzeAsync(file, cancellationToken), () => new FingerprintResult(null, 0));

        await Task.WhenAll(bpmTask, keyTask, energyTask, fingerprintTask).ConfigureAwait(false);

        return new AnalysisResult(
            await bpmTask.ConfigureAwait(false),
            await keyTask.ConfigureAwait(false),
            await energyTask.ConfigureAwait(false),
            await fingerprintTask.ConfigureAwait(false),
            AcoustIdMbid: null);
    }

    private async Task<T> RunAnalyzerAsync<TAnalyzer, T>(
        TAnalyzer? analyzer,
        Func<TAnalyzer, Task<T>> run,
        Func<T> empty)
        where TAnalyzer : class
    {
        if (analyzer is null)
        {
            return empty();
        }

        try
        {
            return await run(analyzer).ConfigureAwait(false);
        }
        catch (AnalysisException ex)
        {
            _logger.LogWarning(ex, "Analyzer failed: {Message}", ex.Message);
            return empty();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in analyzer {Analyzer}", typeof(TAnalyzer).Name);
            return empty();
        }
    }
}
