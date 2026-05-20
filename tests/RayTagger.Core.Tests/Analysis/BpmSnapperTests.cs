using RayTagger.Core.Analysis;

namespace RayTagger.Core.Tests.Analysis;

public class BpmSnapperTests
{
    // ===== Integer step (step=1.0) ===================================================================

    [Theory]
    [InlineData(122.07, 0.12, 122.0, true)]      // 0.057% drift — snaps
    [InlineData(129.87, 0.12, 130.0, true)]      // 0.10%  drift — snaps (close to threshold)
    [InlineData(94.93,  0.12, 95.0,  true)]      // 0.074% drift — snaps
    [InlineData(126.01, 0.12, 126.0, true)]      // 0.008% drift — snaps
    [InlineData(173.48, 0.12, 173.48, false)]    // 0.28%  drift — stays
    [InlineData(124.5,  0.12, 124.5, false)]     // 0.40%  drift — stays
    [InlineData(122.0,  0.12, 122.0, false)]     // already integer — Snap returns same, wasSnapped=false
    public void Integer_step_rounds_within_tolerance_only(double input, double tolerance, double expected, bool expectedWasSnapped)
    {
        var actual = BpmSnapper.Snap(input, tolerance, step: 1.0, out var wasSnapped);
        actual.Should().BeApproximately(expected, 0.0001);
        wasSnapped.Should().Be(expectedWasSnapped);
    }

    // ===== Half-BPM step (step=0.5, default) ========================================================

    [Theory]
    [InlineData(173.48, 0.12, 173.5, true)]      // 0.012% drift from 173.5 — snaps (where step=1 left it alone)
    [InlineData(122.07, 0.12, 122.0, true)]      // 0.057% drift from 122.0 — snaps (closer to 122 than 122.5)
    [InlineData(126.01, 0.12, 126.0, true)]      // 0.008% drift — snaps to 126
    [InlineData(124.5,  0.12, 124.5, false)]     // already on grid — wasSnapped=false
    [InlineData(122.30, 0.12, 122.30, false)]    // 0.163% from 122.5 — stays
    public void Half_step_catches_half_integer_values(double input, double tolerance, double expected, bool expectedWasSnapped)
    {
        var actual = BpmSnapper.Snap(input, tolerance, step: 0.5, out var wasSnapped);
        actual.Should().BeApproximately(expected, 0.0001);
        wasSnapped.Should().Be(expectedWasSnapped);
    }

    // ===== Disabling / guards =======================================================================

    [Theory]
    [InlineData(0.0)]    // disabled
    [InlineData(-1.0)]   // negative = disabled
    public void Zero_or_negative_tolerance_disables_snapping(double tolerance)
    {
        var actual = BpmSnapper.Snap(122.07, tolerance, step: 0.5, out var wasSnapped);
        actual.Should().BeApproximately(122.07, 0.0001);
        wasSnapped.Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.25)]
    public void Zero_or_negative_step_disables_snapping(double step)
    {
        var actual = BpmSnapper.Snap(122.07, tolerancePercent: 0.12, step, out var wasSnapped);
        actual.Should().BeApproximately(122.07, 0.0001);
        wasSnapped.Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-15.5)]
    public void Non_positive_bpm_is_a_no_op(double bpm)
    {
        // Caller should already guard, but the snapper must not divide by zero on a 0-rounded
        // target — returning the input verbatim is the documented safe-fallback semantic.
        var actual = BpmSnapper.Snap(bpm, 0.12, step: 1.0, out var wasSnapped);
        actual.Should().Be(bpm);
        wasSnapped.Should().BeFalse();
    }

    [Fact]
    public void Higher_tolerance_catches_wider_drift()
    {
        // 173.48 drifts 0.28% from 173 with step=1.0 — at tolerance 0.5% it should snap to 173,
        // at 0.12% with step=1.0 it shouldn't. (With step=0.5 it would snap at 0.12% — covered above.)
        BpmSnapper.Snap(173.48, tolerancePercent: 0.12, step: 1.0, out var snappedAtTight)
            .Should().BeApproximately(173.48, 0.0001);
        snappedAtTight.Should().BeFalse();

        BpmSnapper.Snap(173.48, tolerancePercent: 0.5, step: 1.0, out var snappedAtLoose)
            .Should().BeApproximately(173.0, 0.0001);
        snappedAtLoose.Should().BeTrue();
    }
}
