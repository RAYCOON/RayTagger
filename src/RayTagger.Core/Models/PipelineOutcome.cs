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
/// <param name="BpmIsForcedFallback">
/// True when the BPM analyzer's genre-range fold (raw × 2 / raw ÷ 2 then snap) failed to land
/// the value back inside the configured genre interval. The cell is then rendered dark-blue so
/// the user sees that the configured range and the detected tempo couldn't be reconciled — the
/// written value is <c>snap(raw)</c>, the best raw signal available.
/// </param>
/// <param name="BpmCrossCheckDelta">
/// When both an existing BPM tag and a usable analyzer BPM are present, the percent delta
/// (<c>|existing - analyzer| / existing</c>) — set on every track regardless of which value
/// won the merge. Null when one of the two is missing. The UI surfaces values &gt; 2 % as a
/// drift warning (dark-red border) so the user can spot Mixed-In-Key vs Essentia disagreements
/// before they end up in a mix. See <c>TagMerger</c>'s cross-check logic.
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
    bool BpmWasSnapped = false,
    bool BpmIsForcedFallback = false,
    double? BpmCrossCheckDelta = null);
