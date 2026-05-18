using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Pipeline stage 7 — moves or copies a track into the configured library tree based on
/// <see cref="SortOptions.Pattern"/> and conflict policy. See docs/ARCHITECTURE.md §7.
/// </summary>
public interface ISortService
{
    /// <summary>
    /// Computes the destination path and (unless <paramref name="dryRun"/>) performs the move/copy.
    /// Sidecar tag-backups are carried along so <c>tagger restore</c> stays correct.
    /// </summary>
    SortOutcome Execute(
        TrackFile file,
        TrackTags existing,
        ResolvedTrackTags resolved,
        SortOptions options,
        bool dryRun);
}

/// <summary>Pass-through sort that always reports <see cref="SortAction.Disabled"/>.</summary>
public sealed class NoopSortService : ISortService
{
    public static NoopSortService Instance { get; } = new();

    public SortOutcome Execute(TrackFile file, TrackTags existing, ResolvedTrackTags resolved, SortOptions options, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new SortOutcome(file.Path, DestinationPath: null, SortAction.Disabled);
    }
}
