using AttcksMergeTool.Services;

namespace AttcksMergeTool.Tests;

/// <summary>
/// The arithmetic that decides how much black goes between two scenes: enough for the axis
/// with the furthest to travel to make the trip at the configured speed, and no more.
/// </summary>
public class TransitionGapTests
{
    /// <remarks>
    /// The worked example the feature was specified from. Ending at 90 and opening at 10 means
    /// 40 units out to the halfway park and 40 back in, and at 100 units per second each leg
    /// costs 400ms.
    /// </remarks>
    [Fact]
    public void Both_legs_are_paid_for() {
        Assert.Equal(800, Gap(last: [("L0", 90)], next: [("L0", 10)]));
    }

    /// <remarks>
    /// Each leg gets half the gap, so the longer one is what has to fit - not the total travel.
    /// Summing them would buy the shorter leg time it does not need.
    /// </remarks>
    [Fact]
    public void The_longer_leg_sizes_the_gap_not_the_sum_of_both() {
        // Out is 50 units, in is 10. A gap of 1000 gives each leg 500ms, which the 50-unit
        // leg needs exactly; sizing on the sum would have asked for 1200.
        Assert.Equal(1000, Gap(last: [("L0", 100)], next: [("L0", 40)]));
    }

    /// <remarks>
    /// Every axis moves at once, so the gap is set by whichever has the furthest to go rather
    /// than by the stroke axis alone.
    /// </remarks>
    [Fact]
    public void The_furthest_travelling_axis_sizes_the_gap() {
        Assert.Equal(
            1000,
            Gap(last: [("L0", 55), ("R0", 0)], next: [("L0", 45), ("R0", 50)]));
    }

    /// <remarks>
    /// There is nowhere for it to travel from, so only the leg into the new scene is real.
    /// Charging it for both would stretch the gap for a move that never happens.
    /// </remarks>
    [Fact]
    public void An_axis_appearing_for_the_first_time_only_pays_its_inbound_leg() {
        Assert.Equal(1000, Gap(last: [], next: [("R0", 0)]));
    }

    /// <remarks>
    /// It still has to get to the halfway park, which is where it then holds for the whole of
    /// the scene that does not script it.
    /// </remarks>
    [Fact]
    public void An_axis_the_next_scene_does_not_script_still_pays_its_outbound_leg() {
        Assert.Equal(1000, Gap(last: [("R0", 100)], next: []));
    }

    /// <remarks>
    /// Nothing has to move, so there is nothing to buy time for and no reason to interrupt the
    /// video at all.
    /// </remarks>
    [Fact]
    public void Nothing_to_travel_means_no_gap() {
        Assert.Equal(0, Gap(last: [("L0", 50)], next: [("L0", 50)]));
        Assert.Equal(0, Gap(last: [], next: []));
    }

    /// <remarks>
    /// Two frames of black reads as a glitch rather than as a transition, and the travel it
    /// buys is not worth that.
    /// </remarks>
    [Fact]
    public void A_tiny_move_is_still_given_the_minimum_gap() {
        // 1 unit at 100 units/sec is 20ms of travel across both legs.
        Assert.Equal(TransitionGap.MinGapMs, Gap(last: [("L0", 50)], next: [("L0", 51)]));
    }

    [Fact]
    public void A_slower_limit_buys_a_longer_gap() {
        Assert.Equal(1600, Gap(last: [("L0", 90)], next: [("L0", 10)], maxAxisSpeed: 50));
        Assert.Equal(400, Gap(last: [("L0", 90)], next: [("L0", 10)], maxAxisSpeed: 200));
    }

    /// <remarks>
    /// The gap is encoded as whole frames, so a length between two of them would be truncated
    /// back down - under the limit the gap exists to enforce. Rounding up keeps it honest.
    /// </remarks>
    [Theory]
    [InlineData(60, 800)]
    [InlineData(30, 800)]
    [InlineData(24, 834)]
    [InlineData(25, 800)]
    public void The_gap_is_rounded_up_to_a_whole_number_of_frames(int targetFps, int expectedMs) {
        int gapMs = Gap(last: [("L0", 90)], next: [("L0", 10)], targetFps: targetFps);

        // 24fps has no whole number of frames at 800ms, so it rounds up to the next one -
        // 20 frames, 833.33ms, and then up again to the millisecond that contains it.
        Assert.Equal(expectedMs, gapMs);
        Assert.True(gapMs >= 800);
    }

    /// <remarks>
    /// A frame is 16.66ms at 60fps. Doing this in floating point rounds a whole number of them
    /// up to one more, which showed up as every 1000ms gap coming back as 1001.
    /// </remarks>
    [Fact]
    public void A_gap_already_on_a_frame_boundary_is_left_where_it_is() {
        Assert.Equal(1000, Gap(last: [("L0", 100)], next: [("L0", 50)]));
    }

    [Fact]
    public void A_speed_of_nothing_disables_the_gap_rather_than_dividing_by_zero() {
        Assert.Equal(0, Gap(last: [("L0", 100)], next: [("L0", 0)], maxAxisSpeed: 0));
    }

    [Fact]
    public void Every_axis_either_side_of_the_seam_is_active() {
        Dictionary<string, int> last = Positions([("L0", 10), ("R0", 20)]);
        Dictionary<string, int> next = Positions([("R0", 30), ("R1", 40)]);

        Assert.Equal(["L0", "R0", "R1"], TransitionGap.ActiveAxes(last, next).Order());
    }

    private static int Gap(
        (string AxisId, int Pos)[] last,
        (string AxisId, int Pos)[] next,
        int maxAxisSpeed = 100,
        int targetFps = 60) =>
        TransitionGap.DurationMs(Positions(last), Positions(next), maxAxisSpeed, targetFps);

    private static Dictionary<string, int> Positions((string AxisId, int Pos)[] positions) =>
        positions.ToDictionary(position => position.AxisId, position => position.Pos, StringComparer.Ordinal);
}
