using System.Collections.Generic;
using HarmonyLib;
using TurboTick.Networking;
using UnityEngine;
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

    /// <summary>Opt-in local prediction. Measured on a live room it does not hold up: the two simulations
    /// run independent AI, so roughly a quarter of the pawns exceed the correction radius every frame and
    /// are snapped back, which reads as teleporting. The display path is the shipping default.</summary>
    internal static readonly bool Predictive =
        string.Equals(Environment.GetEnvironmentVariable("TURBOTICK_PREDICTIVE"), "1", StringComparison.Ordinal);

    /// <summary>Local simulation must not advance. Derived from the live phase so no tick can slip in
    /// between the load finishing and the first frame.</summary>
    internal static bool Frozen => enabled && !Predictive && SharedColony.Session.Phase == Simulation.SharedPhase.Playing;

    internal static int LastFrameTick { get; private set; } = -1;
    internal static int AppliedFrames { get; private set; }
    internal static int StatesApplied { get; private set; }
    internal static int StatesFailed { get; private set; }
    internal static int Created { get; private set; }
    internal static int CreateFailures { get; private set; }
    internal static int MissingDefs { get; private set; }

    internal static void OnWorldStarted(SessionInfo session)
    {
        enabled = session.Authoritative;
        pending = null;
        LastFrameTick = -1;
        AppliedFrames = 0;
        ResetCounters();
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
        // A predictive peer still consumes frames, but as a correction source rather than a state feed.
        if (!Frozen && !(Predictive && enabled)) return;
        var frame = Interlocked.Exchange(ref pending, null);
        if (frame is null) return;
        if (frame.Tick <= LastFrameTick) return;
        var arrival = Clock.Now;        if (lastFrameArrival > 0f)
        {
            var measured = arrival - lastFrameArrival;
            if (measured > 0.001f && measured < 0.5f) frameInterval = frameInterval * 0.8f + measured * 0.2f;
            // A gap far longer than the smoothing window is a visible hitch: the timeline stopped.
            if (measured > 0.25f) { hitchCount++; worstGap = Math.Max(worstGap, measured); }
        }
        lastFrameArrival = arrival;
        LastFrameTick = frame.Tick;
        AppliedFrames++;
        SharedColony.Session.Barrier.Observe(frame.Tick);
        foreach (var state in frame.Pawns) ApplyPawn(state);
        // Bound the per-frame object work by wall time: a burst of changes must never block the render
        // thread long enough for Windows to declare the window hung. Leftovers are dropped, because the
        // next frame carries newer state for the same objects anyway.
        var deadline = Clock.Now + ObjectBudgetSeconds;
        if (frame.Changed is { Count: > 0 } changed)
            foreach (var state in changed)
            {
                if (Clock.Now > deadline) { DroppedWork++; break; }
                ApplyThing(state);
            }
        if (frame.Removed is { Count: > 0 } removed)
            foreach (var id in removed)
            {
                if (Clock.Now > deadline) { DroppedWork++; break; }
                RemoveThing(id);
            }
        if (frame.Created is { Count: > 0 } created)
            foreach (var state in created)
            {
                if (Clock.Now > deadline) { DroppedWork++; break; }
                CreateThing(state);
            }
        if (AppliedFrames % 600 == 0)
        {
            PruneMotions();
            Log.Message($"[TurboTick] 权威帧 {frame.Tick}：{frame.Pawns.Count} 个角色（滑动中 {SmoothCount()}），{frame.Changed?.Count ?? 0} 个物体变化，" +
                $"新建 {Created} 失败 {CreateFailures} 缺Def {MissingDefs}，" +
                $"Scribe 状态 成功 {StatesApplied} / 失败 {StatesFailed}，" +
                $"失去地图 {DetachCount()}，帧间隔 最差 {worstGap:0.00}s / 停顿 {hitchCount} 次，超预算丢弃 {DroppedWork}，" +
                $"校正 {corrections}，累计 {AppliedFrames} 帧。");
            hitchCount = 0;
            worstGap = 0f;
        }
    }

    /// <summary>Objects left in the draw list with a null map: the exact condition behind the
    /// GetSnowDepth NullReferenceException. Recovering any means a location field leaked through.</summary>
    /// <summary>Live objects that lost their map. Destroyed objects linger in the section layer until the
    /// next rebuild and are expected; anything still alive here means a location field leaked through.</summary>
    private static int DetachCount()
    {
        var count = 0;
        foreach (var map in Find.Maps)
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not RimWorld.Plant plant || plant.Destroyed) continue;
                if (plant.Map is not null && plant.Spawned && plant.def?.plant is not null) continue;
                count++;
                ReportBrokenPlant(plant);
            }
        return count;
    }

    private static readonly HashSet<int> BrokenLogged = [];

    internal static void ReportBrokenPlant(RimWorld.Plant plant)
    {
        if (plant.Destroyed) return;
        if (!BrokenLogged.Add(plant.thingIDNumber) || BrokenLogged.Count > 12) return;
        Log.Warning($"[TurboTick] 跳过绘制 id={plant.thingIDNumber} def={plant.def?.defName} " +
            $"graphicData={(plant.def?.graphicData is null ? "null" : "ok")} " +
            $"defPlant={(plant.def?.plant is null ? "null" : "ok")} map={(plant.Map is null ? "null" : "ok")} " +
            $"spawned={plant.Spawned} pos={plant.Position}");
    }
    private static int RespawnFailures;

    /// <summary>Drops interpolation entries for pawns that are gone, so a long session cannot leak them.</summary>
    private static void PruneMotions()
    {
        if (motions.Count == 0) return;
        var alive = new HashSet<int>();
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawnsSpawned) alive.Add(pawn.thingIDNumber);
        foreach (var id in motions.Keys.Where(id => !alive.Contains(id)).ToList()) motions.Remove(id);
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
        if (thing is null) return; // A creation frame that has not arrived yet; the next one carries it.
        var cell = new IntVec3(state.X, 0, state.Z);
        var map = thing.Map;
        if (map is null) return;
        if (thing.Position != cell && cell.InBounds(map))
        {
            // DeSpawn clears Thing.Map, so the map and rotation must be captured first; otherwise the
            // object is respawned onto a null map and every later Print throws in GetSnowDepth.
            var rotationBefore = thing.Rotation;
            thing.DeSpawn(DestroyMode.Vanish);
            var respawned = GenSpawn.Spawn(thing, cell, map, rotationBefore, WipeMode.VanishOrMoveAside);
            if (respawned is null || !thing.Spawned)
            {
                if (RespawnFailures++ < 8)
                    Log.Warning($"[TurboTick] 重定位失败 {thing.def.defName} id={thing.thingIDNumber} -> {cell}");
            }
        }
        var rotation = new Rot4(state.Rotation);
        if (thing.Rotation != rotation) thing.Rotation = rotation;
        if (thing.def.stackLimit > 1 && thing.stackCount != state.Stack) thing.stackCount = state.Stack;
        if (thing.def.useHitPoints && thing.HitPoints != state.HitPoints) thing.HitPoints = state.HitPoints;
        // Mod state rides along as the object's own Scribe XML; no per-mod handling is required.
        if (state.State is { Length: > 0 } xml && thing is IExposable exposable)
            ApplyPreservingIdentity(thing, exposable, xml);
    }

    /// <summary>A Scribe fragment rewrites the def reference too. If that lookup cannot resolve, the def
    /// would become null and every later draw would throw, so the identity is restored on failure.</summary>
    private static void ApplyPreservingIdentity(Thing thing, IExposable exposable, string xml)
    {
        var def = thing.def;
        var id = thing.thingIDNumber;
        if (ScribeBridge.Load(exposable, xml))
        {
            if (thing.def is null) thing.def = def;
            if (thing.thingIDNumber != id) thing.thingIDNumber = id;
            StatesApplied++;
            return;
        }
        thing.def = def;
        thing.thingIDNumber = id;
        StatesFailed++;
    }

    private static void RemoveThing(int id)
    {
        Forget(id);
        FindThing(id)?.Destroy(DestroyMode.Vanish);
    }

    /// <summary>Recreates an object the host created after the snapshot. The definition is looked up by
    /// defName, so a modded building or item arrives as long as both sides run the same mod set.</summary>
    private static void CreateThing(ReplicaCreation state)
    {
        if (FindThing(state.Id) is not null) return; // Already present; a repeated creation is harmless.
        Map? map = null;
        foreach (var candidate in Find.Maps)
            if (candidate.uniqueID == state.Map) { map = candidate; break; }
        if (map is null) return;
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(state.Def);
        if (def is null)
        {
            if (MissingDefs++ < 8) Log.Warning("[TurboTick] 副本缺少 Def：" + state.Def);
            return;
        }
        try
        {
            var thing = ThingMaker.MakeThing(def);
            if (def.stackLimit > 1 && state.Stack > 1) thing.stackCount = state.Stack;
            var cell = new IntVec3(state.X, 0, state.Z);
            var rotation = new Rot4(state.Rotation);
            if (!cell.InBounds(map)) return;
            // Never WipeMode.Vanish here: the host may legitimately have two things in one cell, and
            // destroying the receiver's copy to make room would silently desync the colony.
            GenSpawn.Spawn(thing, cell, map, rotation, WipeMode.VanishOrMoveAside);
            if (!thing.Spawned)
            {
                if (RespawnFailures++ < 8)
                    Log.Warning($"[TurboTick] 新建后未落地 {state.Def} id={state.Id} -> {cell}");
                thing.Destroy(DestroyMode.Vanish);
                return;
            }
            if (state.State is { Length: > 0 } xml && thing is IExposable exposable)
                ApplyPreservingIdentity(thing, exposable, xml);
            Created++;
        }
        catch (Exception error)
        {
            if (CreateFailures++ < 8)
                Log.Warning("[TurboTick] 副本创建失败 " + state.Def + "：" + error.GetType().Name + ": " + error.Message);
        }
    }

    private static void ResetCounters()
    {
        StatesApplied = 0;
        StatesFailed = 0;
        Created = 0;
        CreateFailures = 0;
        MissingDefs = 0;
    }

    private static void ApplyPawn(ReplicaPawn state)
    {
        var pawn = FindPawn(state.Id, state.Map);
        if (pawn is null || !pawn.Spawned) return;
        var target = new Vector3(state.X, 0f, state.Z);
        if (Predictive)
        {
            // Only correct real drift. Snapping to every frame would undo the local pather and bring back
            // the stutter the predictive path exists to remove.
            if ((pawn.DrawPos - target).MagnitudeHorizontal() <= CorrectionRadius) return;
            corrections++;
        }
        var now = Clock.Now;
        if (motions.TryGetValue(state.Id, out var motion))
        {
            // Continue from wherever the portrait currently is, so a new frame never snaps backwards.
            motion.From = Sample(motion, now);
            motion.To = target;
            motion.Start = now;
        }
        else
        {
            motions[state.Id] = new Motion { From = target, To = target, Start = now };
        }
        // Still keep the integer cell in step for selection, targeting and culling.
        var cell = target.ToIntVec3();
        if (cell.InBounds(pawn.Map) && pawn.Position != cell) pawn.Position = cell;
        var rotation = new Rot4(state.Rotation);
        if (pawn.Rotation != rotation) pawn.Rotation = rotation;
    }

    /// <summary>How far a predictive peer may drift before the host's position wins. Roughly one cell:
    /// smaller than that is ordinary pathing noise, larger means the two simulations disagree.</summary>
    private static readonly float CorrectionRadius = 1.5f;
    private static int corrections;

    private sealed class Motion
    {
        public Vector3 From;
        public Vector3 To;
        public float Start;
    }

    private static readonly Dictionary<int, Motion> motions = [];
    private static float lastFrameArrival;
    private static int hitchCount;
    private static float worstGap;
    /// <summary>Wall-clock budget for object updates in one rendered frame. Each update is a Scribe round
    /// trip, and enough of them in one frame blocks rendering until Windows calls the window hung.</summary>
    private static readonly float ObjectBudgetSeconds =
        float.TryParse(Environment.GetEnvironmentVariable("TURBOTICK_OBJECT_BUDGET_MS"), out var ms) && ms > 0
            ? ms / 1000f : 0.006f;
    private static int DroppedWork;

    /// <summary>Smoothed time between frames; the interpolation window matches what actually arrives.</summary>
    private static float frameInterval = 1f / 30f;

    private static Vector3 Sample(Motion motion, float now)
    {
        var span = Mathf.Clamp(frameInterval, 0.016f, 0.25f);
        var t = Mathf.Clamp01((now - motion.Start) / span);
        if (t >= 1f) return motion.To;
        // Ease-out so a stop decelerates instead of halting abruptly.
        var eased = 1f - (1f - t) * (1f - t);
        return Vector3.Lerp(motion.From, motion.To, eased);
    }

    /// <summary>Interpolated world position for a replica pawn, or false when this pawn has no motion yet.</summary>
    internal static bool TryDrawPos(int id, out Vector3 position)
    {
        if (motions.TryGetValue(id, out var motion))
        {
            position = Sample(motion, Clock.Now);
            return true;
        }
        position = default;
        return false;
    }

    internal static void Forget(int id) => motions.Remove(id);

    /// <summary>Pawns currently between two frames. A non-zero count proves rendering is interpolating
    /// instead of sitting on cell centres.</summary>
    private static int SmoothCount()
    {
        var now = Clock.Now;
        var count = 0;
        foreach (var motion in motions.Values)
            if (now - motion.Start < frameInterval) count++;
        return count;
    }
}

/// <summary>Plant.Print reads Map, def and graphic through several paths and throws if any is missing.
/// A replica can transiently hold such an object between a creation and its state, so printing is
/// skipped and the real condition is reported instead of spamming the log once per section rebuild.</summary>
[HarmonyPatch(typeof(RimWorld.Plant), nameof(RimWorld.Plant.Print))]
internal static class ReplicaPlantPrintGuard
{
    private static bool Prefix(RimWorld.Plant __instance)
    {
        // Not gated on Frozen: a predictive peer simulates locally and can also hold an object that lost
        // its map between a creation and its state, which is exactly what Print throws on.
        if (!AuthoritativeReplica.Enabled) return true;
        if (__instance.Map is not null && __instance.Spawned && __instance.def?.graphicData is not null
            && __instance.def.plant is not null) return true;
        AuthoritativeReplica.ReportBrokenPlant(__instance);
        return false;
    }
}

/// <summary>A display replica never runs the pather, so the vanilla draw tracker has no tween to follow
/// and pawns jump one cell per frame. World positions arrive in frames and are interpolated here instead,
/// which is what makes walking look continuous.</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.DrawPos), MethodType.Getter)]
internal static class ReplicaDrawPosPatch
{
    private static bool Prefix(Pawn __instance, ref Vector3 __result)
    {
        if (!AuthoritativeReplica.Frozen) return true;
        if (!AuthoritativeReplica.TryDrawPos(__instance.thingIDNumber, out var position)) return true;
        __result = position;
        return false;
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
