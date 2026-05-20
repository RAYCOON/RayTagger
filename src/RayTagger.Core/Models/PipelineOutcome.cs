namespace RayTagger.Core.Models;

/// <summary>Terminal status of a single file's run through the pipeline.</summary>
public enum PipelineStatus
{
    /// <summary>File was processed and tag write succeeded (or would-have-succeeded in dry-run).</summary>
    Written,

    /// <summary>File was processed but no field needed writing (all sources matched existing tags).</summary>
    Unchanged,

    /// <summary>File was skipped because of a filter / glob / existing-tags policy.</summary>
    Skipped,

    /// <summary>One or more stages failed for this file; see <see cref="PipelineOutcome.Errors"/>.</summary>
    Failed,
}

/// <summary>A single error produced by a pipeline stage for one file.</summary>
public sealed record StageError(string Stage, string Message, string? StackTrace = null);

/// <summary>A mapping-rule match (name + which fields it altered) for diagnostics / "tagger explain".</summary>
public sealed record MappingRuleHit(string RuleName, IReadOnlyList<string> ChangedFields);

/// <summary>End-to-end result for one file. Returned in batches at the end of a scan run.</summary>
/// <param name="File">The track file the pipeline processed.</param>
/// <param name="Resolved">Resolved tags after every stage — analysis, lookup, AND mapping rules.</param>
/// <param name="AppliedRules">Mapping rules that matched, in evaluation order.</param>
/// <param name="DestinationPath">Where the file would (or did) end up after the optional sort stage.</param>
/// <param name="Status">Terminal pipeline status.</param>
/// <param name="Errors">Errors from individual stages — file is still in the result.</param>
/// <param name="PreMapResolved">
/// Resolved tags BEFORE the mapping-rule stage ran. Captured so the UI's Live-Preview can re-run
/// the rule engine against a freshly-edited <c>mappings.yaml</c> without paying for another read +
/// analyze + lookup pass. Null when the pipeline didn't reach the map stage (e.g. read failed).
/// </param>
/// <param name="ExistingAtScan">
/// Existing tags as read from disk before any merging. Needed by the mapping-rule engine for
/// predicates that look at the original artist / path (the resolved view loses provenance). Null
/// when the read failed.
/// </param>
/// <param name="BpmWasSnapped">
/// True when the BPM analyzer produced a non-integer value that fell within the configured
/// <c>analysis.bpm.snap_tolerance_percent</c> tolerance and got rounded to an integer. Drives the
/// UI's dark-red highlight so the user can see which BPMs are "corrected" rather than verbatim.
/// </param>
public sealed record PipelineOutcome(
    TrackFile File,
    ResolvedTrackTags Resolved,
    IReadOnlyList<MappingRuleHit> AppliedRules,
    string? DestinationPath,
    PipelineStatus Status,
    IReadOnlyList<StageError> Errors,
    ResolvedTrackTags? PreMapResolved = null,
    TrackTags? ExistingAtScan = null,
    bool BpmWasSnapped = false);
