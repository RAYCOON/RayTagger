using RayTagger.Core.Models;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// One row in the results grid. Captures the diff between the file's existing tags and what the
/// pipeline proposes (or actually wrote in non-dry-run mode), plus enough provenance for the user
/// to understand WHY each field changed.
/// </summary>
/// <remarks>
/// All properties are set in the constructor — no <c>ObservableObject</c> base needed since the
/// row never mutates after the pipeline yields it. Bindings read the values once and never
/// re-subscribe. Add it back if Apply / Revert columns gain mutable state.
/// </remarks>
public sealed class TrackOutcomeViewModel
{
    public string Path { get; }
    public string FileName { get; }
    public PipelineStatus Status { get; }

    /// <summary>
    /// Derived view of the pipeline status that's accurate for dry-run too. The pipeline's
    /// <see cref="PipelineStatus"/> stays <c>Unchanged</c> in dry-run regardless of what the
    /// rule engine produced — for UX we want "Würde ändern" when the resolved tags differ
    /// from existing so the user knows there's something to apply.
    /// </summary>
    public string StatusLabel { get; }

    public string? ExistingGenre { get; }
    public string? ProposedGenre { get; }
    public TagFieldSource GenreSource { get; }

    public string? ExistingSubGenre { get; }
    public string? ProposedSubGenre { get; }

    public double? ExistingBpm { get; }
    public double? ProposedBpm { get; }

    public string? ExistingKey { get; }
    public string? ProposedKey { get; }

    public int? ExistingEnergy { get; }
    public int? ProposedEnergy { get; }

    public string? ExistingMood { get; }
    public string? ProposedMood { get; }

    public string? ExistingSetPosition { get; }
    public string? ProposedSetPosition { get; }

    public string? DestinationPath { get; }
    public IReadOnlyList<string> AppliedRules { get; }
    public IReadOnlyList<string> Errors { get; }

    public TrackOutcomeViewModel(PipelineOutcome outcome, TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(existing);

        Path = outcome.File.Path;
        FileName = System.IO.Path.GetFileName(outcome.File.Path);
        Status = outcome.Status;

        StatusLabel = outcome.Status switch
        {
            PipelineStatus.Failed => "Fehler",
            PipelineStatus.Written => "Geschrieben",
            _ when HasProposedChanges(outcome, existing) => "Würde ändern",
            _ => "Unverändert",
        };

        ExistingGenre = existing.Genre;
        ProposedGenre = outcome.Resolved.Genre.Value;
        GenreSource = outcome.Resolved.Genre.Source;

        ExistingSubGenre = existing.SubGenre;
        ProposedSubGenre = outcome.Resolved.SubGenre.Value;

        ExistingBpm = existing.Bpm;
        ProposedBpm = outcome.Resolved.Bpm.Value;

        ExistingKey = existing.Key?.Standard;
        ProposedKey = outcome.Resolved.Key.Value?.Standard;

        ExistingEnergy = existing.Energy;
        ProposedEnergy = outcome.Resolved.Energy.Value;

        ExistingMood = existing.Mood;
        ProposedMood = outcome.Resolved.Mood.Value;

        ExistingSetPosition = existing.SetPosition;
        ProposedSetPosition = outcome.Resolved.SetPosition.Value;

        DestinationPath = outcome.DestinationPath;
        AppliedRules = [.. outcome.AppliedRules.Select(r => r.RuleName)];
        Errors = [.. outcome.Errors.Select(e => $"[{e.Stage}] {e.Message}")];
    }

    /// <summary>
    /// Detects "the pipeline would change something on disk" without re-running it: any logical
    /// field with a non-<see cref="TagFieldSource.Existing"/> source means we have a proposal,
    /// regardless of whether dry-run gated the actual write.
    /// </summary>
    private static bool HasProposedChanges(PipelineOutcome outcome, TrackTags existing)
    {
        var r = outcome.Resolved;
        if (r.Genre.Source != TagFieldSource.Existing) return true;
        if (r.SubGenre.Source != TagFieldSource.Existing) return true;
        if (r.Bpm.Source != TagFieldSource.Existing) return true;
        if (r.Key.Source != TagFieldSource.Existing) return true;
        if (r.Energy.Source != TagFieldSource.Existing) return true;
        if (r.Mood.Source != TagFieldSource.Existing) return true;
        if (r.SetPosition.Source != TagFieldSource.Existing) return true;
        foreach (var kv in r.Custom)
        {
            if (kv.Value.Source != TagFieldSource.Existing) return true;
        }
        // Sort stage proposing a move counts as a change too.
        return outcome.DestinationPath is not null
            && !string.Equals(outcome.DestinationPath, outcome.File.Path, StringComparison.Ordinal);
    }
}
