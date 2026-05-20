namespace RayTagger.Core.Analysis;

/// <summary>
/// Rounds a BPM value to the nearest multiple of a configurable step (whole BPM, half-BPM, …)
/// when the drift is within a tolerance percentage. Lives in Core (not Analysis) because it's a
/// value-shaping rule with no analyzer dependencies — the pipeline applies it as the final step
/// on the resolved BPM regardless of source, so values from existing tags AND from the analyzer
/// get the same cleanup.
/// </summary>
/// <remarks>
/// Drift is computed as <c>|bpm - target| / target * 100</c>, where <c>target</c> is the nearest
/// multiple of <c>step</c>. Examples at the default 0.12% tolerance with step 0.5:
/// <list type="bullet">
///   <item>122.07 → target 122.0 → drift 0.057% → snaps to 122.0</item>
///   <item>173.48 → target 173.5 → drift 0.012% → snaps to 173.5</item>
///   <item>129.87 → target 130.0 → drift 0.10%  → snaps to 130.0</item>
///   <item>122.30 → target 122.5 → drift 0.163% → stays at 122.30</item>
/// </list>
/// With step 1.0 the algorithm collapses to "round to nearest integer" — preserving the original
/// behaviour for users who explicitly want integer-only BPM.
/// </remarks>
public static class BpmSnapper
{
    /// <summary>
    /// Returns the snapped value when within tolerance, otherwise the input verbatim.
    /// <paramref name="wasSnapped"/> tells the caller whether rounding fired — drives the UI's
    /// "this value was corrected" highlight.
    /// </summary>
    public static double Snap(double bpm, double tolerancePercent, double step, out bool wasSnapped)
    {
        wasSnapped = false;
        // <=0 disables snapping. Also guard against pathological inputs that would divide by zero
        // further down (target == 0).
        if (tolerancePercent <= 0 || bpm <= 0 || step <= 0) return bpm;

        var target = Math.Round(bpm / step, MidpointRounding.AwayFromZero) * step;
        if (target == 0) return bpm;  // tiny bpm + huge step → target rounds to 0.

        var driftPercent = Math.Abs(bpm - target) / target * 100.0;
        if (driftPercent <= tolerancePercent)
        {
            // Epsilon comparison: bpm == target after float math doesn't mean they were originally
            // identical (122.5 / 0.5 * 0.5 reconstructs 122.5 exactly, but other multipliers may
            // round-trip with ulps). 1e-9 is well below any sane BPM precision.
            wasSnapped = Math.Abs(bpm - target) > 1e-9;
            return target;
        }
        return bpm;
    }
}
