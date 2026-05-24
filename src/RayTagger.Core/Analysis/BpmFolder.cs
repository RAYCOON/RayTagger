using RayTagger.Core.Models;

namespace RayTagger.Core.Analysis;

/// <summary>
/// Per-genre BPM fold algorithm. Pure computation — no IO, no Essentia. Originally inlined in
/// <c>EssentiaBpmAnalyzer.AnalyzeAsync</c>; extracted so the pipeline can re-apply the same fold
/// once a lookup-resolved genre supplies a tempo range that the initial analyzer pass didn't
/// have. Centralising the rule here also keeps Sprint 5 from drifting away from the analyzer's
/// historical behaviour — both code paths consume the same function.
/// </summary>
public static class BpmFolder
{
    /// <summary>
    /// Applies the genre-range fold to a raw BPM reading and returns the resolved
    /// <see cref="BpmResult"/>. Algorithm (matches the original <c>EssentiaBpmAnalyzer</c>):
    /// <list type="number">
    ///   <item>No range configured → return <c>(raw, confidence)</c> verbatim (no snap; the
    ///         pipeline-level snap takes care of grid alignment uniformly).</item>
    ///   <item>Raw ∈ [Min, Max] → snap to grid and return.</item>
    ///   <item>Raw &lt; Min → fold via <c>raw × 2</c>; raw &gt; Max → fold via <c>raw / 2</c>;
    ///         snap; if back in range, return.</item>
    ///   <item>Folded value still out of range → return <c>snap(raw)</c> with
    ///         <see cref="BpmResult.IsForcedFallback"/> set, so the UI surfaces the irreconcilable
    ///         genre-range vs detected-tempo conflict.</item>
    /// </list>
    /// </summary>
    public static BpmResult Apply(
        double raw,
        double confidence,
        BpmTempoRange? range,
        double snapTolerancePercent,
        double snapStep)
    {
        if (range is null || !range.HasRange)
        {
            return new BpmResult(raw, confidence);
        }

        if (range.Contains(raw))
        {
            var snappedInRange = BpmSnapper.Snap(raw, snapTolerancePercent, snapStep, out var didSnapInRange);
            return new BpmResult(snappedInRange, confidence, WasSnapped: didSnapInRange);
        }

        var folded = raw < range.Min!.Value ? raw * 2.0 : raw / 2.0;
        var snappedFolded = BpmSnapper.Snap(folded, snapTolerancePercent, snapStep, out var foldedSnapped);

        if (range.Contains(snappedFolded))
        {
            return new BpmResult(snappedFolded, confidence, WasSnapped: foldedSnapped);
        }

        var snappedRaw = BpmSnapper.Snap(raw, snapTolerancePercent, snapStep, out var rawSnapped);
        return new BpmResult(snappedRaw, confidence, WasSnapped: rawSnapped, IsForcedFallback: true);
    }
}
