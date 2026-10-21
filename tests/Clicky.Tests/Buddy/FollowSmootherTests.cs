using Clicky.Core.Buddy;

namespace Clicky.Tests.Buddy;

public sealed class FollowSmootherTests
{
    private static readonly TimeSpan Tau = TimeSpan.FromMilliseconds(40);

    [Fact]
    public void First_step_jumps_to_the_target()
    {
        var smoother = new FollowSmoother(Tau);

        smoother.Step(-1800, 420, TimeSpan.FromMilliseconds(16));

        Assert.Equal(-1800, smoother.X);
        Assert.Equal(420, smoother.Y);
        Assert.True(smoother.IsSettled);
    }

    [Fact]
    public void Closes_one_minus_e_to_the_minus_one_of_the_gap_per_time_constant()
    {
        var smoother = new FollowSmoother(Tau);
        smoother.Reset(0, 0);

        smoother.Step(100, 0, Tau);

        Assert.Equal(100 * (1 - Math.Exp(-1)), smoother.X, 1e-9);
        Assert.False(smoother.IsSettled);
    }

    [Fact]
    public void Two_half_frames_land_where_one_full_frame_does()
    {
        var oneStep = new FollowSmoother(Tau);
        var twoSteps = new FollowSmoother(Tau);
        oneStep.Reset(0, 0);
        twoSteps.Reset(0, 0);

        oneStep.Step(500, -300, TimeSpan.FromMilliseconds(14));
        twoSteps.Step(500, -300, TimeSpan.FromMilliseconds(7));
        twoSteps.Step(500, -300, TimeSpan.FromMilliseconds(7));

        Assert.Equal(oneStep.X, twoSteps.X, 1e-9);
        Assert.Equal(oneStep.Y, twoSteps.Y, 1e-9);
    }

    [Fact]
    public void Settles_exactly_on_a_still_target()
    {
        var smoother = new FollowSmoother(Tau);
        smoother.Reset(0, 0);

        var frames = 0;
        do
        {
            smoother.Step(1920, 1080, TimeSpan.FromMilliseconds(1000.0 / 60));
            frames++;
        }
        while (!smoother.IsSettled && frames < 1000);

        Assert.True(smoother.IsSettled);
        Assert.Equal(1920, smoother.X);
        Assert.Equal(1080, smoother.Y);

        // At τ = 40 ms a full-HD diagonal should settle in well under half a second.
        Assert.InRange(frames, 2, 30);
    }

    [Fact]
    public void Zero_time_constant_pins_to_the_target()
    {
        var smoother = new FollowSmoother(TimeSpan.Zero);
        smoother.Reset(0, 0);

        smoother.Step(37, 91, TimeSpan.FromMilliseconds(1));

        Assert.Equal(37, smoother.X);
        Assert.Equal(91, smoother.Y);
        Assert.True(smoother.IsSettled);
    }

    [Fact]
    public void Zero_elapsed_does_not_move_but_reports_unsettled()
    {
        var smoother = new FollowSmoother(Tau);
        smoother.Reset(10, 10);

        smoother.Step(200, 200, TimeSpan.Zero);

        Assert.Equal(10, smoother.X);
        Assert.False(smoother.IsSettled);
    }
}
