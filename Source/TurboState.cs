using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>Game-thread adapter for the independent room protocol. No reference-mod integration.</summary>
public static class TurboState
{
    public static SpeedAuthority Authority { get; } = new();
    private static readonly TickRateMeter meter = new();
    public static float Multiplier => Authority.Current.Multiplier;
    public static float EffectiveMultiplier => Authority.Current.Enabled ? Multiplier : 1f;
    public static double MeasuredTps => meter.ActualTps;
    public static double TargetTps => meter.TargetTps;
    public static bool Behind => meter.Behind;

    public static void InitializeLocal(TurboTickSettings settings) =>
        Authority.SetLocal(settings.enabled, Math.Clamp(settings.multiplier, 1f, 20f), Math.Clamp(settings.maxRateCap, 1f, 240f));

    public static float Apply(float originalRate, bool forcedNormal) => Authority.Scale(originalRate, forcedNormal);

    public static void Request(bool enabled, float multiplier, float maxRate) =>
        RoomConnection.Request(enabled, multiplier, maxRate);

    public static void Update()
    {
        RoomConnection.Pump();
        var tm = Find.TickManager;
        if (Current.ProgramState != ProgramState.Playing || tm == null)
        {
            meter.Reset();
            return;
        }
        // TicksGame is an actual completed-tick counter; MeanTickTime is only CPU cost/capacity.
        meter.Sample(tm.TicksGame, Clock.Now, tm.TickRateMultiplier);
        // Diagnostic only. Local auto-downshift would split a future shared simulation.
    }
}
