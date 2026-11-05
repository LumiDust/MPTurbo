using TurboTick.Networking;

namespace TurboTick.Simulation;

/// <summary>
/// Called only by the game thread. Session policy is never clamped using local preferences.
/// A lost connection holds simulation until reconnect succeeds or the player explicitly leaves.
/// This does not implement shared-world tick scheduling; that is a separate milestone.
/// </summary>
public sealed class SpeedAuthority
{
    private SpeedPolicy local = new();
    private SpeedPolicy? remote;
    public bool SessionRequested { get; private set; }
    public bool SessionConnected { get; private set; }
    public bool IsHeld => SessionRequested && !SessionConnected;
    public SpeedPolicy Current => SessionRequested ? remote ?? new SpeedPolicy() : local;
    public SpeedPolicy Local => local;

    public void SetLocal(bool enabled, float multiplier, float maxRate)
    {
        Validate(multiplier, maxRate);
        if (SessionRequested)
            throw new InvalidOperationException("Active room settings must be confirmed by the server.");
        local = new SpeedPolicy(enabled, multiplier, maxRate, local.Revision + 1);
    }

    public void BeginSession()
    {
        SessionRequested = true;
        SessionConnected = false;
        remote = null;
    }

    public bool ApplyServer(SpeedPolicy policy)
    {
        Validate(policy.Multiplier, policy.MaxRate);
        if (!SessionRequested) return false;
        if (policy.Revision < 0) throw new ArgumentOutOfRangeException(nameof(policy));
        if (remote != null && policy.Revision <= remote.Revision) return false;
        remote = policy;
        SessionConnected = true;
        return true;
    }

    public void ConnectionLost() => SessionConnected = false;

    public void LeaveSession()
    {
        SessionRequested = false;
        SessionConnected = false;
        remote = null;
    }

    public float Scale(float originalRate, bool forcedNormal = false)
    {
        if (IsHeld) return 0f;
        if (!float.IsFinite(originalRate) || originalRate <= 0f) return 0f;
        if (forcedNormal || !Current.Enabled) return originalRate;
        return Math.Min(originalRate * Current.Multiplier, Current.MaxRate);
    }

    private static void Validate(float multiplier, float cap)
    {
        if (!float.IsFinite(multiplier) || multiplier < 1f || multiplier > 20f)
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Multiplier must be finite and within 1..20.");
        if (!float.IsFinite(cap) || cap < 1f || cap > 240f)
            throw new ArgumentOutOfRangeException(nameof(cap), "Rate cap must be finite and within 1..240.");
    }
}

/// <summary>Actual completed simulation ticks per real second; no assumption about MeanTickTime units.</summary>
public sealed class TickRateMeter
{
    private int previousTick;
    private double previousTime;
    private bool started;
    public double ActualTps { get; private set; }
    public double TargetTps { get; private set; }
    public bool HasStableSample { get; private set; }
    public bool Behind => HasStableSample && TargetTps > 0 && ActualTps < TargetTps * 0.75;

    public void Reset()
    {
        started = false;
        HasStableSample = false;
        ActualTps = 0;
        TargetTps = 0;
    }

    public void Sample(int ticks, double seconds, float targetRate)
    {
        var target = float.IsFinite(targetRate) ? Math.Max(0d, targetRate) * 60d : 0;
        if (!started || target != TargetTps || ticks < previousTick || seconds < previousTime || seconds - previousTime > 10)
        {
            started = true;
            HasStableSample = false;
            TargetTps = target;
            previousTick = ticks;
            previousTime = seconds;
            ActualTps = 0;
            return;
        }
        var dt = seconds - previousTime;
        if (dt < 1) return;
        ActualTps = (ticks - previousTick) / dt;
        HasStableSample = true;
        previousTick = ticks;
        previousTime = seconds;
    }
}
