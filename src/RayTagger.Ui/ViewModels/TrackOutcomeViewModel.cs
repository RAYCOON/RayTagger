using CommunityToolkit.Mvvm.ComponentModel;
using RayTagger.Core.Models;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// One row in the results grid. Captures the diff between the file's existing tags and what the
/// pipeline proposes (or actually wrote in non-dry-run mode), plus enough provenance for the user
/// to understand WHY each field changed.
/// </summary>
/// <remarks>
/// Mutable state is intentional: after Apply succeeds we flip the Status / StatusLabel and lift
/// the "Existing" values to match "Proposed" (the file on disk now holds those). That keeps the
/// row visible in the grid with a "Geschrieben" badge instead of forcing a re-scan.
/// </remarks>
public sealed partial class TrackOutcomeViewModel : ObservableObject
{
    public string Path { get; }
    public string FileName { get; }

    /// <summary>The underlying pipeline outcome — used as the input to a deferred write.</summary>
    public PipelineOutcome SourceOutcome { get; private set; }

    /// <summary>The pre-map resolved tags from the scan, kept so live-preview can re-evaluate
    /// the rule engine against a newly-edited mappings.yaml without re-reading the file.</summary>
    public ResolvedTrackTags? PreMapResolved => SourceOutcome.PreMapResolved;

    /// <summary>The existing tags at scan time — feeds rule predicates that look at artist / path.</summary>
    public TrackTags? ExistingAtScan => SourceOutcome.ExistingAtScan;

    [ObservableProperty] private TagFieldSource _genreSource;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenreDiff))]
    private string? _proposedGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubGenreDiff))]
    private string? _proposedSubGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBpmDiff))]
    private double? _proposedBpm;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyDiff))]
    private string? _proposedKey;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEnergyDiff))]
    private int? _proposedEnergy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoodDiff))]
    private string? _proposedMood;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetPositionDiff))]
    private string? _proposedSetPosition;
    [ObservableProperty] private IReadOnlyList<string> _appliedRules = [];
    [ObservableProperty] private string? _destinationPath;
    public IReadOnlyList<string> Errors { get; }

    // Per-field diff flags drive the gelbes Cell-Highlight in the results grid. True iff the
    // proposed value differs from the existing one (and we actually have a proposed value to
    // highlight — null proposed → no highlight, otherwise every Unverändert row would glow).
    public bool HasGenreDiff => ProposedGenre is not null && !string.Equals(ExistingGenre, ProposedGenre, StringComparison.Ordinal);
    public bool HasSubGenreDiff => ProposedSubGenre is not null && !string.Equals(ExistingSubGenre, ProposedSubGenre, StringComparison.Ordinal);
    public bool HasBpmDiff => ProposedBpm is not null && ProposedBpm != ExistingBpm;
    public bool HasKeyDiff => ProposedKey is not null && !string.Equals(ExistingKey, ProposedKey, StringComparison.Ordinal);
    public bool HasEnergyDiff => ProposedEnergy is not null && ProposedEnergy != ExistingEnergy;
    public bool HasMoodDiff => ProposedMood is not null && !string.Equals(ExistingMood, ProposedMood, StringComparison.Ordinal);
    public bool HasSetPositionDiff => ProposedSetPosition is not null && !string.Equals(ExistingSetPosition, ProposedSetPosition, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsApplied))]
    private PipelineStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsApplied))]
    private string _statusLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _isApplying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _isReverting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _hasSidecar;

    [ObservableProperty] private string? _applyError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenreDiff))]
    private string? _existingGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubGenreDiff))]
    private string? _existingSubGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBpmDiff))]
    private double? _existingBpm;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyDiff))]
    private string? _existingKey;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEnergyDiff))]
    private int? _existingEnergy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoodDiff))]
    private string? _existingMood;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetPositionDiff))]
    private string? _existingSetPosition;

    /// <summary>True iff this row has a pending change the user can apply.</summary>
    public bool CanApply => !IsApplying && !IsReverting && StatusLabel == "Würde ändern";

    /// <summary>True after a successful apply — drives the green check glyph + suppresses the button.</summary>
    public bool IsApplied => Status == PipelineStatus.Written;

    /// <summary>True iff a backup-sidecar exists on disk and no Apply/Revert is in flight.</summary>
    public bool CanRevert => HasSidecar && !IsApplying && !IsReverting;

    public TrackOutcomeViewModel(PipelineOutcome outcome, TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(existing);

        SourceOutcome = outcome;
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
        ExistingSubGenre = existing.SubGenre;
        ExistingBpm = existing.Bpm;
        ExistingKey = existing.Key?.Standard;
        ExistingEnergy = existing.Energy;
        ExistingMood = existing.Mood;
        ExistingSetPosition = existing.SetPosition;

        ApplyResolvedFromOutcome(outcome);
        Errors = [.. outcome.Errors.Select(e => $"[{e.Stage}] {e.Message}")];
    }

    private void ApplyResolvedFromOutcome(PipelineOutcome outcome)
    {
        ProposedGenre = outcome.Resolved.Genre.Value;
        GenreSource = outcome.Resolved.Genre.Source;
        ProposedSubGenre = outcome.Resolved.SubGenre.Value;
        ProposedBpm = outcome.Resolved.Bpm.Value;
        ProposedKey = outcome.Resolved.Key.Value?.Standard;
        ProposedEnergy = outcome.Resolved.Energy.Value;
        ProposedMood = outcome.Resolved.Mood.Value;
        ProposedSetPosition = outcome.Resolved.SetPosition.Value;
        DestinationPath = outcome.DestinationPath;
        AppliedRules = [.. outcome.AppliedRules.Select(r => r.RuleName)];
    }

    /// <summary>
    /// Replaces the cached <see cref="SourceOutcome"/> with the result of re-evaluating the rule
    /// engine on top of the existing pre-map state. Used by Live-Preview so a rule edit shows up
    /// in the grid immediately without re-scanning the file. Recomputes <see cref="StatusLabel"/>
    /// off the new resolved tags so "Würde ändern" / "Unverändert" stay accurate.
    /// </summary>
    public void UpdatePreview(PipelineOutcome refreshedOutcome, TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(refreshedOutcome);
        ArgumentNullException.ThrowIfNull(existing);

        SourceOutcome = refreshedOutcome;
        ApplyResolvedFromOutcome(refreshedOutcome);

        // Keep the row's StatusLabel honest after the preview swap: a previously "Würde ändern"
        // row might collapse to "Unverändert" if the user's new rule chain produces no diff.
        if (Status is PipelineStatus.Written or PipelineStatus.Failed)
        {
            // Don't overwrite terminal labels — the user has already applied / failed this row.
            return;
        }
        StatusLabel = HasProposedChanges(refreshedOutcome, existing) ? "Würde ändern" : "Unverändert";
    }

    /// <summary>Mark the row "in progress" — disables its button + raises CanApply.</summary>
    public void BeginApply()
    {
        ApplyError = null;
        IsApplying = true;
    }

    /// <summary>
    /// Flip the row to <see cref="PipelineStatus.Written"/> after a successful write. Lifts every
    /// Existing-* to match Proposed-* because the file on disk now holds those values; the diff
    /// arrow effectively collapses to "X → X" so the user can still see the new state without
    /// re-scanning.
    /// </summary>
    public void EndApplySuccess()
    {
        ExistingGenre = ProposedGenre;
        ExistingSubGenre = ProposedSubGenre;
        ExistingBpm = ProposedBpm;
        ExistingKey = ProposedKey;
        ExistingEnergy = ProposedEnergy;
        ExistingMood = ProposedMood;
        ExistingSetPosition = ProposedSetPosition;

        IsApplying = false;
        Status = PipelineStatus.Written;
        StatusLabel = "Geschrieben";
    }

    /// <summary>Failure path — flip to red status + surface the message via <see cref="ApplyError"/>.</summary>
    public void EndApplyFailure(string error)
    {
        IsApplying = false;
        Status = PipelineStatus.Failed;
        StatusLabel = "Fehler";
        ApplyError = error;
    }

    /// <summary>Mark the row "in progress" for a Revert operation.</summary>
    public void BeginRevert()
    {
        ApplyError = null;
        IsReverting = true;
    }

    /// <summary>
    /// After a successful Revert: lift every Existing-* to the snapshot values so the diff
    /// column reflects what's actually on disk. The Proposed-* values stay as they were (cached
    /// from the original scan) — that's intentional, it lets the user see "the rules would
    /// re-apply these changes if you click Anwenden again". HasSidecar clears because the
    /// coordinator deleted the file. StatusLabel flips back to "Würde ändern" iff the proposed
    /// state still differs from the restored existing state — otherwise "Unverändert".
    /// </summary>
    public void EndRevertSuccess(TrackTags restored)
    {
        ArgumentNullException.ThrowIfNull(restored);

        ExistingGenre = restored.Genre;
        ExistingSubGenre = restored.SubGenre;
        ExistingBpm = restored.Bpm;
        ExistingKey = restored.Key?.Standard;
        ExistingEnergy = restored.Energy;
        ExistingMood = restored.Mood;
        ExistingSetPosition = restored.SetPosition;

        IsReverting = false;
        HasSidecar = false;
        // Reset Status to neutral. The label below picks the right tone based on whether the
        // proposed state would still change anything against the restored existing state.
        Status = PipelineStatus.Skipped;
        StatusLabel = StillHasProposedChanges(restored) ? "Würde ändern" : "Unverändert";
    }

    private bool StillHasProposedChanges(TrackTags restored) =>
        !string.Equals(restored.Genre, ProposedGenre, StringComparison.Ordinal)
        || !string.Equals(restored.SubGenre, ProposedSubGenre, StringComparison.Ordinal)
        || restored.Bpm != ProposedBpm
        || !string.Equals(restored.Key?.Standard, ProposedKey, StringComparison.Ordinal)
        || restored.Energy != ProposedEnergy
        || !string.Equals(restored.Mood, ProposedMood, StringComparison.Ordinal)
        || !string.Equals(restored.SetPosition, ProposedSetPosition, StringComparison.Ordinal);

    /// <summary>Revert failed — show the error, keep HasSidecar true so the user can retry.</summary>
    public void EndRevertFailure(string error)
    {
        IsReverting = false;
        Status = PipelineStatus.Failed;
        StatusLabel = "Fehler";
        ApplyError = error;
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
