using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Default <see cref="ITagPipeline"/> implementation. Per file:
/// <list type="number">
///   <item>Read existing tags via <see cref="ITagReaderAdapter"/>.</item>
///   <item>Run configured analyzers via <see cref="IAnalysisRunner"/>.</item>
///   <item>Merge existing + analysis into <see cref="ResolvedTrackTags"/> per
///         <c>existing_tags_policy</c> and per-dimension <c>min_confidence</c>.</item>
///   <item>(Phase 3) apply mapping rules.</item>
///   <item>(Phase 4) overlay online lookup results.</item>
///   <item>Optionally write — delegated to <see cref="ITagWriterAdapter"/> when not in dry-run.</item>
/// </list>
/// Per-file errors are isolated: an exception in one file logs and continues with the next, so a
/// corrupt MP3 doesn't abort a 10 000-file scan.
/// </summary>
public sealed class TagPipeline : ITagPipeline
{
    private readonly IFileDiscoveryService _discovery;
    private readonly ITagReaderAdapter _reader;
    private readonly ITagWriterAdapter _writer;
    private readonly IAnalysisRunner _analysisRunner;
    private readonly ILookupRunner _lookupRunner;
    private readonly IMappingRuleEngine _ruleEngine;
    private readonly ISortService _sortService;
    private readonly ILogger<TagPipeline> _logger;

    public TagPipeline(
        IFileDiscoveryService discovery,
        ITagReaderAdapter reader,
        ITagWriterAdapter writer,
        IAnalysisRunner analysisRunner,
        ILookupRunner lookupRunner,
        IMappingRuleEngine ruleEngine,
        ISortService sortService,
        ILogger<TagPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(analysisRunner);
        ArgumentNullException.ThrowIfNull(lookupRunner);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(sortService);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _reader = reader;
        _writer = writer;
        _analysisRunner = analysisRunner;
        _lookupRunner = lookupRunner;
        _ruleEngine = ruleEngine;
        _sortService = sortService;
        _logger = logger;
    }

    private static LookupQuery BuildLookupQuery(TrackTags existing, AnalysisResult analysis) =>
        new()
        {
            Artist = existing.Artist,
            Title = existing.Title,
            Album = existing.Album,
            Fingerprint = analysis.Fingerprint.Chromaprint,
            // Duration isn't tracked on TrackTags yet — AcoustID skips when null, other providers
            // don't need it. Surfacing duration is a small TagLib# addition; deferred until the
            // user has Essentia + fpcalc installed and we can validate AcoustID end-to-end.
            DurationSeconds = null,
        };

    public async IAsyncEnumerable<PipelineOutcome> RunAsync(
        TaggerOptions options,
        MappingRuleSet rules,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rules);

        await foreach (var file in _discovery.EnumerateAsync(options.Scan, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await ProcessFileAsync(file, options, rules, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PipelineOutcome> ProcessFileAsync(
        TrackFile file,
        TaggerOptions options,
        MappingRuleSet rules,
        CancellationToken cancellationToken)
    {
        var errors = new List<StageError>();
        TrackTags existing;

        try
        {
            existing = _reader.Read(file.Path);
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Read failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Read", ex.Message));
            return new PipelineOutcome(
                file,
                ResolvedTrackTags.Empty,
                AppliedRules: [],
                DestinationPath: null,
                Status: PipelineStatus.Failed,
                Errors: errors);
        }

        AnalysisResult analysis = AnalysisResult.Empty;
        try
        {
            analysis = await _analysisRunner.RunAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Analysis failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Analyze", ex.Message));
            // Continue with whatever existing tags we have — analysis is best-effort.
        }

        LookupResult? lookup = null;
        try
        {
            var query = BuildLookupQuery(existing, analysis);
            lookup = await _lookupRunner.RunAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Lookup failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Lookup", ex.Message));
            // Fail open — pipeline continues with whatever analysis produced.
        }

        var resolved = TagMerger.Merge(existing, analysis, lookup, options.Analysis, options.Read.ExistingTagsPolicy);

        IReadOnlyList<MappingRuleHit> appliedRules = [];
        try
        {
            var ruleResult = _ruleEngine.Evaluate(resolved, existing, file, rules);
            resolved = ruleResult.Tags;
            appliedRules = ruleResult.Applied;
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Mapping rules failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Map", ex.Message));
            // Continue without rule-derived overrides — the resolved tags are still valid.
        }

        var status = PipelineStatus.Unchanged;
        if (options.Write.DryRun || !HasAnyNonExistingField(resolved))
        {
            // Dry-run never writes; nothing to do when every field is Existing-sourced.
        }
        else
        {
            try
            {
                var writeResult = _writer.Write(file.Path, resolved, options);
                status = writeResult.WrittenFields.Count > 0 ? PipelineStatus.Written : PipelineStatus.Unchanged;
            }
            catch (Exception ex) when (ShouldIsolate(ex))
            {
                _logger.LogWarning(ex, "Write failed for {Path}: {Message}", file.Path, ex.Message);
                errors.Add(new StageError("Write", ex.Message));
                status = PipelineStatus.Failed;
            }
        }

        string? destinationPath = null;
        try
        {
            var sortOutcome = _sortService.Execute(file, existing, resolved, options.Sort, options.Write.DryRun);
            destinationPath = sortOutcome.DestinationPath;
            if (sortOutcome.Action is SortAction.Moved or SortAction.Copied or SortAction.Planned
                && status == PipelineStatus.Unchanged)
            {
                // A pure relocation without tag changes still counts as work done — flip the
                // overall status so the renderer's "Unchanged" bucket doesn't hide sorted files.
                status = PipelineStatus.Written;
            }
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Sort failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Sort", ex.Message));
        }

        return new PipelineOutcome(
            file,
            resolved,
            appliedRules,
            DestinationPath: destinationPath,
            Status: status,
            Errors: errors);
    }

    private static bool HasAnyNonExistingField(ResolvedTrackTags resolved) =>
        resolved.Genre.Source != TagFieldSource.Existing
        || resolved.SubGenre.Source != TagFieldSource.Existing
        || resolved.Bpm.Source != TagFieldSource.Existing
        || resolved.Key.Source != TagFieldSource.Existing
        || resolved.Energy.Source != TagFieldSource.Existing;

    /// <summary>
    /// Per-file failure isolation: catch everything except cancellation/OOM/StackOverflow.
    /// </summary>
    private static bool ShouldIsolate(Exception ex) =>
        ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException;
}
