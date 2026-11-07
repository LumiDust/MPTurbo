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
    internal static int StatesApplied { get; private set; }
    internal static int StatesFailed { get; private set; }

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
        foreach (var state in frame.Pawns) ApplyPawn(state);
        if (frame.Changed is { Count: > 0 } changed)
            foreach (var state in changed) ApplyThing(state);
        if (frame.Removed is { Count: > 0 } removed)
            foreach (var id in removed) RemoveThing(id);
        if (AppliedFrames % 600 == 0)
            Log.Message($"[TurboTick] 权威帧 {frame.Tick}：{frame.Pawns.Count} 个角色，{frame.Changed?.Count ?? 0} 个物体变化，" +
                $"Scribe 状态 成功 {StatesApplied} / 失败 {StatesFailed}，累计 {AppliedFrames} 帧。");
    }

    private static Pawn? FindPawn(int id, int mapId)
    {
        foreach (var map in Find.Maps)
        {
            if (map.uniqueID != mapId) continue;
            return map.mapPawns.AllPawnsSpawned.FirstOrDefault(candidate => candidate.thingIDNumber == id);
        }
        return null;
    }

    private static Thing? FindThing(int id)
    {
        foreach (var map in Find.Maps)
        {
            var found = map.listerThings.AllThings.FirstOrDefault(candidate => candidate.thingIDNumber == id);
            if (found is not null) return found;
        }
        return null;
    }

    private static void ApplyThing(ReplicaThing state)
    {
        var thing = FindThing(state.Id);
        if (thing is null) return; // Objects spawned after the load are not created yet.
        var cell = new IntVec3(state.X, 0, state.Z);
        if (thing.Position != cell) thing.Position = cell;
        var rotation = new Rot4(state.Rotation);
        if (thing.Rotation != rotation) thing.Rotation = rotation;
        if (thing.def.stackLimit > 1 && thing.stackCount != state.Stack) thing.stackCount = state.Stack;
        if (thing.def.useHitPoints && thing.HitPoints != state.HitPoints) thing.HitPoints = state.HitPoints;
        // Mod state rides along as the object's own Scribe XML; no per-mod handling is required.
        if (state.State is { Length: > 0 } xml && thing is IExposable exposable)
        {
            if (ScribeBridge.Load(exposable, xml)) StatesApplied++;
            else StatesFailed++;
        }
    }

    private static void RemoveThing(int id) => FindThing(id)?.Destroy(DestroyMode.Vanish);

    private static void ApplyPawn(ReplicaPawn state)
    {
        var pawn = FindPawn(state.Id, state.Map);
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
