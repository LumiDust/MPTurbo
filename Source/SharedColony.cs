using HarmonyLib;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Shared-colony session: the controller publishes the world, everyone loads the same save, and player
/// actions travel through the server. The server owns the tick, so peers only advance inside the ticks it
/// published; actions are applied at the tick they were ordered for.
/// </summary>
public static class SharedColony
{
    public static SharedSession Session { get; } = new();
    private static long nextSequence;
    private static float stalledSince = -1f;

    public static bool Gating => Session.Phase == SharedPhase.Playing;

    public static long NextSequence() => ++nextSequence;

    public static void Reset()
    {
        Session.Leave();
        SharedActions.ResetTickWork();
        stalledSince = -1f;
    }

    public static void OnWorldStarted(long epoch, int tick, SnapshotManifest manifest)
    {
        Session.BeginLoading(epoch, manifest.Id);
        Session.Barrier.Publish(tick);
        Session.WorldLoaded();
        Session.Playing();
        Log.Message($"[TurboTick] 共享殖民地已开始：epoch={epoch} world={manifest.Id}");
    }

    public static void OnTick(int tick) => Session.Barrier.Publish(tick);

    public static void OnCommands(IReadOnlyList<GameCommand> commands)
    {
        foreach (var command in commands) Session.Commands.Accept(command);
    }

    internal static void Send(GameCommand command) => _ = RoomConnection.SendCommand(command.Kind, command.Payload, command.Sequence);

    /// <summary>True when the local game may run another tick. Prevents one slow machine from racing ahead.</summary>
    public static bool MayAdvanceTick()
    {
        if (!Gating) return true;
        if (Session.Barrier.CanAdvance) { stalledSince = -1f; return true; }
        // Never freeze the game because the server went quiet: fall back and surface the condition.
        if (stalledSince < 0f) stalledSince = Clock.Now;
        if (Clock.Now - stalledSince > 3f)
        {
            if (Session.Phase == SharedPhase.Playing) Session.Fault("等待服务器 tick 超时；已临时本地推进");
            return true;
        }
        return false;
    }

    /// <summary>Called after each local simulation tick: consume the tick and apply the commands due at it.</summary>
    public static void OnLocalTick()
    {
        if (!Gating) return;
        Session.Barrier.TryAdvance();
        ApplyDue();
    }

    /// <summary>Applies commands whose tick has arrived. Also used while paused so late commands still land.</summary>
    public static void ApplyDue()
    {
        if (!Session.IsActive) return;
        foreach (var command in Session.Commands.TakeThrough(Session.Barrier.AppliedTick))
            SharedActions.Apply(command);
    }

    public static void Update()
    {
        if (!Gating) return;
        // While paused the game does not tick, so drain anything that became due.
        ApplyDue();
    }
}

/// <summary>Lockstep gate: the local simulation may not outrun the server's published tick.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
internal static class TickGatePatch
{
    private static bool Prefix()
    {
        if (Current.ProgramState != ProgramState.Playing) return true;
        return SharedColony.MayAdvanceTick();
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class TickApplyPatch
{
    private static void Postfix() => SharedColony.OnLocalTick();
}
