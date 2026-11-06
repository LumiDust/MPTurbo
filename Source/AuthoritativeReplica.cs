using System.Collections.Generic;
using HarmonyLib;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>
/// Authoritative mode: the headless host is the only simulator. A joining client still loads the same
/// save so it has every object to draw, but it never advances its own tick list. Pawn positions arrive
/// in frames and are written straight onto the local objects. Because no local simulation runs, there is
/// no state stream to diverge, and the per-tick hash comparison stops applying to replicas.
/// </summary>
internal static class AuthoritativeReplica
{
    private static volatile bool enabled;
    private static AuthoritativeFrame? pending;

    /// <summary>True once the server told this peer to follow rather than simulate.</summary>
    internal static bool Enabled => enabled;

    /// <summary>Local simulation must not advance. Derived from the live phase so no tick can slip in
    /// between the load finishing and the first frame.</summary>
    internal static bool Frozen => enabled && SharedColony.Session.Phase == Simulation.SharedPhase.Playing;

    internal static int LastFrameTick { get; private set; } = -1;
    internal static int AppliedFrames { get; private set; }

    internal static void OnWorldStarted(SessionInfo session)
    {
        enabled = session.Authoritative;
        pending = null;
        LastFrameTick = -1;
        AppliedFrames = 0;
        if (enabled) Log.Message("[TurboTick] 权威模式：本端为显示副本，不参与本地模拟。");
    }

    internal static void OnLeft() => enabled = false;

    /// <summary>Called from the network thread; keeps only the newest frame.</summary>
    internal static void Receive(AuthoritativeFrame frame)
    {
        if (!enabled) return;
        if (Interlocked.Increment(ref receivedFrames) == 1)
            Log.Message($"[TurboTick] 收到首个权威帧：tick={frame.Tick}，{frame.Pawns.Count} 个角色。");
        Interlocked.Exchange(ref pending, frame);
    }

    private static int receivedFrames;

    /// <summary>Game thread: apply the newest frame and follow the host clock.</summary>
    internal static void Update()
    {
        if (!Frozen) return;
        var frame = Interlocked.Exchange(ref pending, null);
        if (frame is null) return;
        if (frame.Tick <= LastFrameTick) return;
        LastFrameTick = frame.Tick;
        AppliedFrames++;
        SharedColony.Session.Barrier.Observe(frame.Tick);
        foreach (var state in frame.Pawns) Apply(state);
        if (AppliedFrames % 600 == 0)
            Log.Message($"[TurboTick] 权威帧 {frame.Tick}：{frame.Pawns.Count} 个角色，累计 {AppliedFrames} 帧。");
    }

    private static void Apply(ReplicaPawn state)
    {
        Pawn? pawn = null;
        foreach (var map in Find.Maps)
        {
            if (map.uniqueID != state.Map) continue;
            pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(candidate => candidate.thingIDNumber == state.Id);
            break;
        }
        if (pawn is null || !pawn.Spawned) return;
        var cell = new IntVec3((int)state.X, 0, (int)state.Z);
        if (pawn.Position != cell) pawn.Position = cell;
        var rotation = new Rot4(state.Rotation);
        if (pawn.Rotation != rotation) pawn.Rotation = rotation;
    }
}

/// <summary>A display replica must report the host's clock. Without this every TicksGame reader in the UI
/// and in rendering would freeze at the tick the save was loaded, because the replica never ticks.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TicksGame), MethodType.Getter)]
internal static class ReplicaClockPatch
{
    private static bool Prefix(ref int __result)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        __result = SharedColony.Session.Barrier.AppliedTick;
        return false;
    }
}
