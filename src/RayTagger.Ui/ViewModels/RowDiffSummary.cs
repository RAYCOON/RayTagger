using System.Collections.ObjectModel;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// One line in the Rule Editor's right-hand diff panel — a single track that has at least one
/// field whose proposed value differs from disk. Holds a back-reference to the row VM so the
/// "Springen" button can scroll the results grid back to it without a path / filename lookup.
/// </summary>
/// <remarks>
/// Plain class (not a record) because the binding system reuses item containers and the
/// back-reference makes value-equality semantics misleading: two summaries for "the same file"
/// can legitimately carry different <see cref="Diffs"/> snapshots after a Live-Preview edit.
/// </remarks>
public sealed class RowDiffSummary
{
    public TrackOutcomeViewModel Row { get; }
    public string FileName => Row.FileName;
    public IReadOnlyList<FieldDiff> Diffs { get; }

    public RowDiffSummary(TrackOutcomeViewModel row, IReadOnlyList<FieldDiff> diffs)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(diffs);
        Row = row;
        Diffs = diffs;
    }
}

/// <summary>One field-level change on a row. Empty values render as "—" in the side panel.</summary>
public sealed record FieldDiff(string Field, string? OldValue, string? NewValue);

/// <summary>
/// Helper that lifts <see cref="TrackOutcomeViewModel"/>'s seven boolean HasXxxDiff flags into a
/// flat <see cref="FieldDiff"/> list. Kept here (next to the data shape) so the rebuild loop in
/// <see cref="ScanViewModel"/> stays one-liner-readable.
/// </summary>
public static class RowDiffCollector
{
    public static IReadOnlyList<FieldDiff> Collect(TrackOutcomeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var diffs = new List<FieldDiff>();
        if (row.HasGenreDiff) diffs.Add(new FieldDiff("Genre", row.ExistingGenre, row.ProposedGenre));
        if (row.HasSubGenreDiff) diffs.Add(new FieldDiff("Sub-Genre", row.ExistingSubGenre, row.ProposedSubGenre));
        if (row.HasBpmDiff) diffs.Add(new FieldDiff("BPM", row.ExistingBpm?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), row.ProposedBpm?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        if (row.HasKeyDiff) diffs.Add(new FieldDiff("Key", row.ExistingKey, row.ProposedKey));
        if (row.HasEnergyDiff) diffs.Add(new FieldDiff("Energy", row.ExistingEnergy?.ToString(System.Globalization.CultureInfo.InvariantCulture), row.ProposedEnergy?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (row.HasMoodDiff) diffs.Add(new FieldDiff("Mood", row.ExistingMood, row.ProposedMood));
        if (row.HasSetPositionDiff) diffs.Add(new FieldDiff("Set", row.ExistingSetPosition, row.ProposedSetPosition));
        return diffs;
    }

    /// <summary>
    /// Replaces the contents of <paramref name="target"/> with one <see cref="RowDiffSummary"/>
    /// per row in <paramref name="rows"/> that has at least one field-level diff. Reuses the same
    /// collection instance so existing bindings on it stay live.
    /// </summary>
    public static void Rebuild(ObservableCollection<RowDiffSummary> target, IEnumerable<TrackOutcomeViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rows);
        target.Clear();
        foreach (var row in rows)
        {
            var diffs = Collect(row);
            if (diffs.Count > 0)
            {
                target.Add(new RowDiffSummary(row, diffs));
            }
        }
    }
}
