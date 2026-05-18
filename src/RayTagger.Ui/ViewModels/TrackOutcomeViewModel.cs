using CommunityToolkit.Mvvm.ComponentModel;
using RayTagger.Core.Models;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// One row in the results grid. Captures the diff between the file's existing tags and what the
/// pipeline proposes (or actually wrote in non-dry-run mode), plus enough provenance for the user
/// to understand WHY each field changed.
/// </summary>
public sealed partial class TrackOutcomeViewModel : ObservableObject
{
    public string Path { get; }
    public string FileName { get; }
    public PipelineStatus Status { get; }

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

        DestinationPath = outcome.DestinationPath;
        AppliedRules = [.. outcome.AppliedRules.Select(r => r.RuleName)];
        Errors = [.. outcome.Errors.Select(e => $"[{e.Stage}] {e.Message}")];
    }
}
