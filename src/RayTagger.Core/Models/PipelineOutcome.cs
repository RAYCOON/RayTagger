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
public sealed record PipelineOutcome(
    TrackFile File,
    ResolvedTrackTags Resolved,
    IReadOnlyList<MappingRuleHit> AppliedRules,
    string? DestinationPath,
    PipelineStatus Status,
    IReadOnlyList<StageError> Errors);
