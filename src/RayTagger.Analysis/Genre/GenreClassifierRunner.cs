using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Analysis.Genre;

/// <summary>
/// Default <see cref="IGenreClassifierRunner"/> — fans out to every enabled
/// <see cref="IGenreClassifier"/> in parallel, aggregates their candidates into a single ranked
/// stream and records a per-classifier <see cref="ClassifierTraceEntry"/>. See
/// <c>docs/PLAN_GENRE_CLASSIFICATION.md §3.7.2</c>.
/// </summary>
public sealed class GenreClassifierRunner : IGenreClassifierRunner
{
    private readonly List<IGenreClassifier> _classifiers;
    private readonly ILogger<GenreClassifierRunner> _logger;

    public GenreClassifierRunner(
        IEnumerable<IGenreClassifier> classifiers,
        ILogger<GenreClassifierRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(classifiers);
        ArgumentNullException.ThrowIfNull(logger);
        _classifiers = [.. classifiers];
        _logger = logger;
    }

    public async Task<GenreClassifierRunResult> RunAsync(
        TrackFile file, AnalysisResult analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_classifiers.Count == 0)
        {
            return GenreClassifierRunResult.Empty;
        }

        var tasks = _classifiers
            .Select(c => RunOneAsync(c, file, cancellationToken))
            .ToArray();
        var entries = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Sort within classifier hits only — they get appended AFTER provider hits in TagPipeline,
        // so providers retain top spots.
        var allCandidates = entries
            .Where(e => e.Status == ClassifierTraceStatus.Ok)
            .SelectMany(e => e.Candidates)
            .OrderByDescending(c => c.Confidence)
            .ToList();

        return new GenreClassifierRunResult(allCandidates, entries);
    }

    private async Task<ClassifierTraceEntry> RunOneAsync(
        IGenreClassifier classifier, TrackFile file, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await classifier.ClassifyAsync(file, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var status = result.Candidates.Count > 0
                ? ClassifierTraceStatus.Ok
                : ClassifierTraceStatus.NoHit;
            return new ClassifierTraceEntry(
                classifier.Name, status, result.Candidates, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(
                ex,
                "Klassifikator {Name} hat einen Fehler geworfen für {Path}: {Message}",
                classifier.Name, file.Path, ex.Message);
            return new ClassifierTraceEntry(
                classifier.Name,
                ClassifierTraceStatus.Failed,
                [],
                ex.Message,
                sw.Elapsed.TotalMilliseconds);
        }
    }
}
