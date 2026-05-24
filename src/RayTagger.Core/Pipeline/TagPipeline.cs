using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
///   <item>Merge existing + analysis into <see cref="ResolvedTrackTags"/> per per-dimension
///         <c>min_confidence</c> and <c>existing_confidence</c>.</item>
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
    private readonly IGenreClassifierRunner _classifierRunner;
    private readonly IMappingRuleEngine _ruleEngine;
    private readonly ISortService _sortService;
    private readonly Mapping.TaxonomyGenreResolver _genreResolver;
    private readonly ILogger<TagPipeline> _logger;

    public TagPipeline(
        IFileDiscoveryService discovery,
        ITagReaderAdapter reader,
        ITagWriterAdapter writer,
        IAnalysisRunner analysisRunner,
        ILookupRunner lookupRunner,
        IGenreClassifierRunner classifierRunner,
        IMappingRuleEngine ruleEngine,
        ISortService sortService,
        Mapping.TaxonomyGenreResolver genreResolver,
        ILogger<TagPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(analysisRunner);
        ArgumentNullException.ThrowIfNull(lookupRunner);
        ArgumentNullException.ThrowIfNull(classifierRunner);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(sortService);
        ArgumentNullException.ThrowIfNull(genreResolver);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _reader = reader;
        _writer = writer;
        _analysisRunner = analysisRunner;
        _lookupRunner = lookupRunner;
        _classifierRunner = classifierRunner;
        _ruleEngine = ruleEngine;
        _sortService = sortService;
        _genreResolver = genreResolver;
        _logger = logger;
    }

    private static LookupQuery BuildLookupQuery(TrackTags existing, AnalysisResult analysis) =>
        new()
        {
            Artist = existing.Artist,
            Title = existing.Title,
            Album = existing.Album,
            Fingerprint = analysis.Fingerprint.Chromaprint,
            // Duration comes from TagLib# Properties.Duration (read by TagLibTagReader). AcoustID
            // requires it; the other providers ignore the field when null.
            DurationSeconds = existing.DurationSeconds,
        };

    public async IAsyncEnumerable<PipelineOutcome> RunAsync(
        TaggerOptions options,
        MappingRuleSet rules,
        Func<TrackFile, ValueTask>? onFileStarted = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rules);

        var parallelism = Math.Max(1, options.Scan.EffectiveParallelism);

        // Parallelism = 1 keeps the simple sequential code-path for users who want deterministic
        // ordering or who debug with breakpoints. Above 1 we kick the channel-based fan-out so
        // the Essentia-subprocess and HTTP-lookup latency overlaps with CPU work on other tracks.
        if (parallelism == 1)
        {
            await foreach (var file in _discovery.EnumerateAsync(options.Scan, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await InvokeFileStartedSafelyAsync(onFileStarted, file).ConfigureAwait(false);
                yield return await ProcessFileAsync(file, options, rules, cancellationToken).ConfigureAwait(false);
            }
            yield break;
        }

        await foreach (var outcome in RunParallelAsync(options, rules, parallelism, onFileStarted, cancellationToken).ConfigureAwait(false))
        {
            yield return outcome;
        }
    }

    /// <summary>
    /// Bounded-channel fan-out. One producer task pulls discovered <see cref="TrackFile"/>s into
    /// the work channel; N worker tasks pull from the work channel, process the file end-to-end,
    /// and push the resulting <see cref="PipelineOutcome"/> into the output channel. The caller's
    /// async-enumerator drains the output channel in completion order — *not* discovery order,
    /// which is the cost users pay for the throughput win.
    /// </summary>
    /// <remarks>
    /// We bound the work channel at 4 × parallelism so the producer doesn't run far ahead of the
    /// workers (avoids holding a million <see cref="TrackFile"/> records in memory on huge
    /// libraries). The output channel is unbounded — back-pressure on the consumer would stall
    /// workers and re-introduce the sequential bottleneck.
    /// </remarks>
    private async IAsyncEnumerable<PipelineOutcome> RunParallelAsync(
        TaggerOptions options,
        MappingRuleSet rules,
        int parallelism,
        Func<TrackFile, ValueTask>? onFileStarted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workChannel = Channel.CreateBounded<TrackFile>(new BoundedChannelOptions(parallelism * 4)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var outputChannel = Channel.CreateUnbounded<PipelineOutcome>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var file in _discovery.EnumerateAsync(options.Scan, cancellationToken).ConfigureAwait(false))
                {
                    await workChannel.Writer.WriteAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                workChannel.Writer.Complete();
            }
        }, cancellationToken);

        var workerTasks = Enumerable.Range(0, parallelism).Select(_ => Task.Run(async () =>
        {
            await foreach (var file in workChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // OnFileStarted fires exactly once per file, right before ProcessFileAsync begins.
                // Lets the UI flip the row to "Scannen" / SCN-badge so the user can see which
                // tracks the worker pool is actively chewing on.
                await InvokeFileStartedSafelyAsync(onFileStarted, file).ConfigureAwait(false);
                var outcome = await ProcessFileAsync(file, options, rules, cancellationToken).ConfigureAwait(false);
                await outputChannel.Writer.WriteAsync(outcome, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken)).ToArray();

        var completionTask = Task.WhenAll(workerTasks).ContinueWith(
            _ => outputChannel.Writer.Complete(),
            TaskScheduler.Default);

        await foreach (var outcome in outputChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return outcome;
        }

        // Surface any worker / producer exception once the consumer has drained — keeps the
        // failure mode the same as the sequential path (one bad file doesn't kill the scan, but
        // a fatal error like a bug in TagMerger does propagate).
        await producerTask.ConfigureAwait(false);
        await completionTask.ConfigureAwait(false);
        await Task.WhenAll(workerTasks).ConfigureAwait(false);
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
            // existing is passed through so the runner can resolve per-track hints (e.g.
            // tempo-range from the genre tag) before kicking off the native analyzers.
            analysis = await _analysisRunner.RunAsync(file, existing, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Analysis failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Analyze", ex.Message));
            // Continue with whatever existing tags we have — analysis is best-effort.
        }


        LookupResult? lookup = null;
        IReadOnlyList<ProviderTraceEntry>? providerTrace = null;
        try
        {
            var query = BuildLookupQuery(existing, analysis);
            var runResult = await _lookupRunner.RunAsync(query, cancellationToken).ConfigureAwait(false);
            lookup = runResult.Result;
            providerTrace = runResult.Trace;
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Lookup failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Lookup", ex.Message));
            // Fail open — pipeline continues with whatever analysis produced.
        }

        // Audio-based genre classification stage. Appends classifier candidates to the lookup
        // result so the taxonomy resolver applies the same whole-word + longest-match logic to
        // classifier hits as it does to API hits. Providers retain top spots in the candidate
        // list — classifiers only "rescue" the resolver when providers produced nothing
        // taxonomy-shaped. See docs/PLAN_GENRE_CLASSIFICATION.md §5.1.
        IReadOnlyList<ClassifierTraceEntry>? classifierTrace = null;
        try
        {
            var classifierRun = await _classifierRunner
                .RunAsync(file, analysis, cancellationToken)
                .ConfigureAwait(false);
            // Only surface a trace when at least one classifier actually ran. The Noop runner
            // returns an empty list — distinguishing "stage skipped" from "stage ran with no
            // hits" keeps the UI diagnostic surface honest.
            if (classifierRun.Trace.Count > 0)
            {
                classifierTrace = classifierRun.Trace;
            }
            if (classifierRun.Candidates.Count > 0)
            {
                var existingGenres = lookup?.GenreCandidates ?? [];
                var merged = existingGenres.Concat(classifierRun.Candidates).ToList();
                lookup = (lookup ?? LookupResult.Empty) with { GenreCandidates = merged };
            }
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Classifier stage failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Classify", ex.Message));
            // Fail open — the merger still has the (unmodified) lookup result to consume.
        }

        // Pass the taxonomy + resolver only when the flag is on AND a taxonomy is loaded;
        // otherwise TagMerger falls back to the legacy "top-1 stur" Lookup-Merge.
        var resolverActive = options.Lookup.TaxonomyResolution && options.Taxonomy.Loaded.Genres.Count > 0;
        var resolved = TagMerger.Merge(
            existing,
            analysis,
            lookup,
            options.Analysis,
            resolverActive ? options.Taxonomy.Loaded : null,
            resolverActive ? _genreResolver : null,
            providerTrace,
            options.Mapping.SourcePriority,
            options.Lookup.ExistingConfidence);
        if (classifierTrace is not null)
        {
            resolved = resolved with { ClassifierTrace = classifierTrace };
        }
        // Snapshot the pre-map state so the UI's live-preview can re-evaluate the rule chain
        // against a freshly-edited mappings.yaml without paying for a re-read / re-analyze.
        var preMapResolved = resolved;

        IReadOnlyList<MappingRuleHit> appliedRules = [];
        try
        {
            var ruleResult = _ruleEngine.Evaluate(resolved, existing, file, rules, options.Taxonomy.Loaded);
            resolved = ruleResult.Tags;
            appliedRules = ruleResult.Applied;
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "Mapping rules failed for {Path}: {Message}", file.Path, ex.Message);
            errors.Add(new StageError("Map", ex.Message));
            // Continue without rule-derived overrides — the resolved tags are still valid.
        }

        // #15 BPM-Re-Fold: when the initial analyzer pass ran without a genre hint (because the
        // file had no existing Genre tag), the resulting BPM is the raw Essentia value with no
        // half/double-time correction. Once lookup AND mapping rules have settled the genre,
        // retry the fold against that genre's tempo range — recovers the typical "DnB at 86 BPM
        // raw → 172 corrected" case for tracks that arrived without a genre tag. Runs AFTER
        // mapping rules so a rule-supplied genre can drive the fold too (Source = Rules path).
        // Only fires when:
        //   - existing.Genre was empty (analyzer used no range, so analysis.Bpm.Bpm is raw)
        //   - the resolved Genre comes from Lookup / Rules (NOT Existing — that path is already
        //     handled by the analyzer's initial fold)
        //   - there's an actual range configured for the resolved genre
        if (string.IsNullOrWhiteSpace(existing.Genre)
            && !string.IsNullOrWhiteSpace(resolved.Genre.Value)
            && resolved.Genre.Source != TagFieldSource.Existing
            && resolved.Bpm.Value is double rawBpm
            && resolved.Bpm.Source == TagFieldSource.Analysis
            && options.Analysis.Bpm.TempoRangesByGenre.TryGetValue(resolved.Genre.Value, out var lookupRange))
        {
            var refolded = RayTagger.Core.Analysis.BpmFolder.Apply(
                rawBpm,
                resolved.Bpm.Confidence,
                lookupRange,
                options.Analysis.Bpm.SnapTolerancePercent,
                options.Analysis.Bpm.SnapStep);
            if (refolded.Bpm is double refoldedValue && !refoldedValue.Equals(rawBpm))
            {
                _logger.LogDebug(
                    "BPM re-fold for {Path}: raw {Raw:F2} → {Folded:F2} via resolved-genre {Genre} range {Range}",
                    file.Path, rawBpm, refoldedValue, resolved.Genre.Value, lookupRange);
                resolved = resolved with
                {
                    Bpm = new ResolvedValueField<double>(refoldedValue, TagFieldSource.Analysis, refolded.Confidence),
                };
            }
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

        // BPM snap: clean up near-integer BPM values uniformly across all sources (existing tags
        // like "126.01", analyzer noise like "122.07", rule-doubled results). Runs LAST so it
        // sees the final value the user/writer would see. Snapped values promote to
        // TagFieldSource.Rules so the writer treats them as user-declared intent and overwrites
        // the existing tag — otherwise skip_if_present would silently preserve "126.01" on disk
        // while the UI showed the rounded "126".
        //
        // Capture the pre-snap source so we can attribute the analyzer's own snap/forced-fallback
        // flags only when the analyzer's value actually made it through the merge. (Existing-tag
        // values that skip_if_present preserved should not inherit analyzer-side highlights.)
        var preSnapSource = resolved.Bpm.Source;
        var pipelineSnapFired = false;
        if (resolved.Bpm.Value is double finalBpm)
        {
            // Per-genre snap-step override: when the resolved genre has an entry in
            // SnapStepByGenre, use it instead of the global SnapStep. Same normalisation as
            // TempoRangesByGenre — the resolved Genre value is what we look up here.
            var snapStep = options.Analysis.Bpm.SnapStep;
            if (!string.IsNullOrWhiteSpace(resolved.Genre.Value)
                && options.Analysis.Bpm.SnapStepByGenre.TryGetValue(resolved.Genre.Value, out var perGenreStep)
                && perGenreStep > 0)
            {
                snapStep = perGenreStep;
            }

            var snapped = RayTagger.Core.Analysis.BpmSnapper.Snap(
                finalBpm,
                options.Analysis.Bpm.SnapTolerancePercent,
                snapStep,
                out pipelineSnapFired);
            if (pipelineSnapFired)
            {
                resolved = resolved with
                {
                    Bpm = new ResolvedValueField<double>(snapped, TagFieldSource.Rules, resolved.Bpm.Confidence),
                };
            }
        }
        var analyzerProducedFinal = preSnapSource == TagFieldSource.Analysis;
        var bpmWasSnapped = pipelineSnapFired || (analyzerProducedFinal && analysis.Bpm.WasSnapped);
        var bpmIsForcedFallback = analyzerProducedFinal && analysis.Bpm.IsForcedFallback;

        // BPM-Cross-Check: when both an existing tag and a min-confidence-cleared analyzer BPM
        // are available, report the relative drift between them. Pure diagnostic — doesn't
        // influence which value wins (that's existing_confidence's job). Surfaces silent drift
        // between Mixed-In-Key's tag and Essentia's detection: 2 % is the rule-of-thumb where
        // a track starts beating against a mix.
        double? bpmCrossCheckDelta = null;
        if (existing.Bpm is double existingBpm
            && analysis.Bpm.Bpm is double analyzerBpm
            && analysis.Bpm.Confidence >= options.Analysis.Bpm.MinConfidence
            && existingBpm > 0)
        {
            bpmCrossCheckDelta = Math.Abs(existingBpm - analyzerBpm) / existingBpm;
            if (bpmCrossCheckDelta > 0.02)
            {
                _logger.LogWarning(
                    "BPM drift {DriftPercent:P1} for {Path}: existing={Existing} analyzer={Analyzer}",
                    bpmCrossCheckDelta, file.Path, existingBpm, analyzerBpm);
            }
        }

        return new PipelineOutcome(
            file,
            resolved,
            appliedRules,
            DestinationPath: destinationPath,
            Status: status,
            Errors: errors,
            PreMapResolved: preMapResolved,
            ExistingAtScan: existing,
            BpmWasSnapped: bpmWasSnapped,
            BpmIsForcedFallback: bpmIsForcedFallback,
            BpmCrossCheckDelta: bpmCrossCheckDelta);
    }

    private static bool HasAnyNonExistingField(ResolvedTrackTags resolved) =>
        resolved.Genre.Source != TagFieldSource.Existing
        || resolved.SubGenre.Source != TagFieldSource.Existing
        || resolved.Bpm.Source != TagFieldSource.Existing
        || resolved.Key.Source != TagFieldSource.Existing
        || resolved.Energy.Source != TagFieldSource.Existing
        || resolved.Mood.Source != TagFieldSource.Existing
        || resolved.SetPosition.Source != TagFieldSource.Existing
        // Custom fields set by mapping rules count too — without this, a rule that ONLY changes
        // a custom tag (no logical slot touched) gets bucketed as Unchanged and the writer is
        // skipped, silently dropping the user's declarative intent on the floor.
        || resolved.Custom.Any(kv => kv.Value.Source != TagFieldSource.Existing);

    /// <summary>
    /// Per-file failure isolation: catch everything except cancellation/OOM/StackOverflow.
    /// </summary>
    private static bool ShouldIsolate(Exception ex) =>
        ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException;

    /// <summary>
    /// Invokes the optional <paramref name="onFileStarted"/> callback with the same isolation we
    /// apply to <see cref="ProcessFileAsync"/>: a buggy UI callback must not bring a worker down
    /// and starve the rest of the channel. Cancellation propagates so a Cancel-button click still
    /// stops the pipeline cleanly.
    /// </summary>
    private async ValueTask InvokeFileStartedSafelyAsync(Func<TrackFile, ValueTask>? onFileStarted, TrackFile file)
    {
        if (onFileStarted is null) return;
        try
        {
            await onFileStarted(file).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldIsolate(ex))
        {
            _logger.LogWarning(ex, "OnFileStarted callback failed for {Path}", file.Path);
        }
    }
}
