namespace TurboTick.Networking;

/// <summary>Wall-clock budget for the native speed selection. Paused time never becomes catch-up debt.</summary>
public sealed class NativeTickClock
{
    private int speed = -1;
    private double remainder;

    public static double Rate(int timeSpeed) => SpeedPolicy.ValidateTimeSpeed(timeSpeed) switch
    {
        0 => 0d,
        1 => 1d,
        2 => 3d,
        3 => 6d,
        4 => 15d,
        _ => throw new ArgumentOutOfRangeException(nameof(timeSpeed))
    };

    public int Advance(double elapsedSeconds, int timeSpeed)
    {
        var rate = Rate(timeSpeed);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (speed != timeSpeed) { speed = timeSpeed; remainder = 0; }
        if (rate == 0) { remainder = 0; return 0; }
        // A stalled process should not publish minutes of ticks in one network burst.
        remainder += Math.Min(elapsedSeconds, 0.25d) * 60d * rate;
        var ticks = (int)Math.Floor(remainder + 1e-9d);
        remainder -= ticks;
        return ticks;
    }
}
