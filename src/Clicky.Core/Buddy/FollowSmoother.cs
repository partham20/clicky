namespace Clicky.Core.Buddy;

/// <summary>
/// Eases a position toward a moving target. Each step closes 1 - e^(-dt/τ) of the gap, so the
/// motion looks the same at 60 Hz, 144 Hz or through a dropped frame.
/// </summary>
/// <remarks>
/// Runs in physical pixels on the virtual desktop, so the path stays continuous when the target
/// crosses between monitors with different scales. Not thread-safe; the UI thread owns it.
/// </remarks>
public sealed class FollowSmoother
{
    private readonly double _settleDistanceSquared;
    private bool _hasPosition;

    public FollowSmoother(TimeSpan timeConstant, double settleDistance = 0.25)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(settleDistance);
        TimeConstant = timeConstant;
        _settleDistanceSquared = settleDistance * settleDistance;
    }

    /// <summary>τ: after this long the remaining gap is 1/e (~37%). Zero or less locks to the target.</summary>
    public TimeSpan TimeConstant { get; set; }

    public double X { get; private set; }

    public double Y { get; private set; }

    /// <summary>True once the position has reached the target (within the settle distance).</summary>
    public bool IsSettled { get; private set; } = true;

    /// <summary>Jumps straight to a position with no easing.</summary>
    public void Reset(double x, double y)
    {
        X = x;
        Y = y;
        _hasPosition = true;
        IsSettled = true;
    }

    public void Step(double targetX, double targetY, TimeSpan elapsed)
    {
        if (!_hasPosition || TimeConstant <= TimeSpan.Zero)
        {
            Reset(targetX, targetY);
            return;
        }

        var dx = targetX - X;
        var dy = targetY - Y;
        if ((dx * dx) + (dy * dy) <= _settleDistanceSquared)
        {
            Reset(targetX, targetY);
            return;
        }

        if (elapsed > TimeSpan.Zero)
        {
            var alpha = 1 - Math.Exp(-elapsed.TotalSeconds / TimeConstant.TotalSeconds);
            X += dx * alpha;
            Y += dy * alpha;
        }

        IsSettled = false;
    }
}
