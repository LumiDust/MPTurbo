namespace TurboTick.Simulation;

/// <summary>
/// Decides how many extra simulation ticks to run in one frame while a peer is behind the server.
/// Without this a late joiner advances at the same rate as the server and can never close the gap,
/// because both run about 60 ticks per second.
/// </summary>
public static class CatchUpPlan
{
    /// <summary>Upper bound per frame; guards against a huge gap freezing the client.</summary>
    public const int MaxPerFrame = 8;

    /// <summary>
    /// Steps to run this frame. Zero means "let the normal tick happen".
    /// The budget scales with the gap but never exceeds <paramref name="maxPerFrame"/>.
    /// </summary>
    public static int Steps(int backlog, int maxPerFrame = MaxPerFrame)
    {
        if (backlog <= 0) return 0;
        if (maxPerFrame < 1) return 0;
        return Math.Min(backlog, maxPerFrame);
    }

    /// <summary>
    /// True when replaying the gap is more expensive than reloading the newest checkpoint.
    /// Reloading costs a snapshot transfer; replaying costs one frame per few ticks.
    /// </summary>
    public static bool PreferCheckpoint(int backlog, int threshold = 3600) =>
        backlog > threshold;
}
