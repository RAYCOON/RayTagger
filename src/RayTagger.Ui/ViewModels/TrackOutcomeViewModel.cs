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
    public PipelineOutcome SourceOutcome { get; }

    public TagFieldSource GenreSource { get; }
    public string? ProposedGenre { get; }
    public string? ProposedSubGenre { get; }
    public double? ProposedBpm { get; }
    public string? ProposedKey { get; }
    public int? ProposedEnergy { get; }
    public string? ProposedMood { get; }
    public string? ProposedSetPosition { get; }
    public string? DestinationPath { get; }
    public IReadOnlyList<string> AppliedRules { get; }
    public IReadOnlyList<string> Errors { get; }

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
    private bool _isApplying;

    [ObservableProperty] private string? _applyError;

    [ObservableProperty] private string? _existingGenre;
    [ObservableProperty] private string? _existingSubGenre;
    [ObservableProperty] private double? _existingBpm;
    [ObservableProperty] private string? _existingKey;
    [ObservableProperty] private int? _existingEnergy;
    [ObservableProperty] private string? _existingMood;
    [ObservableProperty] private string? _existingSetPosition;

    /// <summary>True iff this row has a pending change the user can apply.</summary>
    public bool CanApply => !IsApplying && StatusLabel == "Würde ändern";

    /// <summary>True after a successful apply — drives the green check glyph + suppresses the button.</summary>
    public bool IsApplied => Status == PipelineStatus.Written;

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
