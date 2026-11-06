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
    private static readonly SimulationTickGuard tickGuard = new(Session.Barrier);
    internal static bool HasSharedWorld => Session.Phase != SharedPhase.Offline;

    public static bool Gating => Session.Phase == SharedPhase.Playing;

    public static long NextSequence() => ++nextSequence;

    public static void Reset()
    {
        Session.Leave();
        SharedActions.ResetTickWork();
        tickGuard.Cancel();
        reportingArmed = false;
        loadPending = false;
        SimulationRandom.Reset();
    }

    private static int lastCheckpointTick;
    private const int CheckpointInterval = 3000;

    public static void OnWorldStarted(SessionInfo info, SnapshotManifest manifest)
    {
        reportingArmed = false;
        loadPending = false;
        expectedSaveName = null;
        tickGuard.Cancel();
        Session.BeginLoading(info.Epoch, manifest.Id);
        Session.Barrier.Reset(Math.Max(0, info.SnapshotTick));
        Session.Barrier.Publish(info.Tick);
        lastCheckpointTick = info.SnapshotTick;
        // Stay in LoadingWorld until the snapshot is actually in the game; commands must not apply to the
        // previous colony, which is still resident while the download runs.
        Log.Message($"[TurboTick] 共享殖民地已开始：epoch={info.Epoch} tick={info.Tick} world={manifest.Id}");
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
        if (!HasSharedWorld) return true;
        if (!Gating || TurboState.Authority.IsHeld) return false;
        // A pause is normal; vanilla Update handles it. No timeout may release a shared world locally.
        if (TurboState.Authority.Current.TimeSpeed == 0 || Find.TickManager?.Paused == true) return true;
        return Session.Barrier.CanAdvance;
    }

    private static bool catchingUp;

    /// <summary>
    /// Runs extra ticks while behind the server. Both sides advance at roughly 60 ticks per second, so
    /// without a burst a peer that joined late would stay behind forever.
    /// </summary>
    public static void CatchUpTick()
    {
        if (HeadlessRuntime.HoldLocalTicks || catchingUp || !Gating || TurboState.Authority.IsHeld
            || TurboState.Authority.Current.TimeSpeed == 0 || Find.TickManager?.Paused == true
            || Find.TickManager?.slower?.ForcedNormalSpeed == true) return;
        var steps = CatchUpPlan.Steps(Session.Barrier.Backlog);
        if (steps == 0) return;
        catchingUp = true;
        var started = Clock.Now;
        try
        {
            var tickManager = Find.TickManager;
            for (var i = 0; i < steps; i++)
            {
                if (tickManager == null || Session.Phase != SharedPhase.Playing || !Session.Barrier.CanAdvance) break;
                tickManager.DoSingleTick();
                // Late-game ticks are expensive; never spend the whole frame catching up.
                if (Clock.Now - started > 0.025f) break;
            }
        }
        finally { catchingUp = false; }
    }

    internal static bool TryBeginTick(TickManager manager, out SimulationTickGuard.Permit permit)
    {
        permit = default;
        return Gating && !TurboState.Authority.IsHeld && TurboState.Authority.Current.TimeSpeed != 0
            && !manager.Paused && tickGuard.TryBegin(manager.TicksGame, out permit);
    }

    internal static void CompleteTick(TickManager manager, SimulationTickGuard.Permit permit)
    {
        if (!tickGuard.Complete(permit, manager.TicksGame))
        {
            Session.Fault("实际游戏 tick 与共享时间线不一致；模拟已挂起");
            return;
        }
        OnLocalTick();
        HeadlessRuntime.ReportProgress();
    }

    internal static void SkipTick() => tickGuard.Cancel();

    internal static void CancelTick(Exception error)
    {
        tickGuard.Cancel();
        Session.Fault("共享模拟 tick 异常，已挂起：" + error.Message);
    }

    /// <summary>After a permitted engine tick has completed and the barrier committed exactly once.</summary>
    public static void OnLocalTick()
    {
        if (!Gating) return;
        ApplyDue();
        if (Session.Barrier.AppliedTick % 60 == 0) ReportHash();
        if (RoomConnection.IsCheckpointOwner && !HeadlessRuntime.StopRequested
            && Session.Barrier.AppliedTick - lastCheckpointTick >= CheckpointInterval)
        {
            lastCheckpointTick = Session.Barrier.AppliedTick;
            RoomConnection.PublishCheckpoint(lastCheckpointTick);
        }
    }

    /// <summary>Periodic canonical hash so the server can prove all peers still simulate the same colony.</summary>
    private static int lastHashedTick = -1;
    private static bool reportingArmed;

    private static void ReportHash()
    {
        var tick = Session.Barrier.AppliedTick;
        // One report per tick: a paused or throttled tick would otherwise be re-reported with drifted state
        // and look like a desync to the server.
        // Only report once this peer actually runs the shared colony; a peer still sitting in its own
        // quicktest colony would otherwise publish unrelated hashes and look like a desync.
        if (!reportingArmed || tick <= lastHashedTick) return;
        lastHashedTick = tick;
        try
        {
            var (entries, categories) = DescribeColony();
            var hash = StateHash.Compute(entries);
            SimulationDiagnostics.Hash(tick, hash, categories);
            _ = RoomConnection.ReportHash(tick, hash, categories);
        }
        catch (Exception error) { Log.Warning("[TurboTick] 状态哈希计算失败：" + error.Message); }
    }

    /// <summary>Splits the colony into categories so a divergence can name what actually differs.</summary>
    internal static (List<string> All, List<string> Categories) DescribeColony()
    {
        var pawns = new List<string>();
        var things = new List<string>();
        var designations = new List<string>();
        foreach (var map in Find.Maps)
        {
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                pawns.Add(StateHash.Pawn(pawn.thingIDNumber, map.Index, pawn.Position.x, pawn.Position.z,
                    pawn.health?.summaryHealth?.SummaryHealthPercent ?? 0f, pawn.CurJobDef?.defName ?? ""));
            foreach (var thing in map.listerThings.AllThings)
                if (thing.def.category is not (ThingCategory.Pawn or ThingCategory.Mote or ThingCategory.Ethereal))
                    things.Add(StateHash.Thing(thing.thingIDNumber, map.Index, thing.Position.x, thing.Position.z,
                        thing.stackCount, thing.HitPoints));
            foreach (var designation in map.designationManager.AllDesignations)
                designations.Add($"d:{designation.def.defName}:{map.Index}:{designation.target.Cell.x}:{designation.target.Cell.z}");
        }
        var all = new List<string>(pawns.Count + things.Count + designations.Count);
        all.AddRange(pawns);
        all.AddRange(things);
        all.AddRange(designations);
        return (all, [StateHash.Compute(pawns), StateHash.Compute(things), StateHash.Compute(designations)]);
    }

    private static float lastRecoveryAt = -999f;

    public static void OnDesync(int tick, string detail)
    {
        Session.Fault($"状态分叉：tick {tick}（{detail}）与其他玩家不一致");
        Log.Error($"[TurboTick] 检测到状态分叉（tick {tick}，类别 {detail}）。");
        var settings = TurboTickMod.Settings;
        if (settings == null || !settings.autoRecover) return;
        // Reuse the join path: reconnect, reload the checkpoint and replay the delta. Rate-limited so a
        // persistent divergence cannot become a reconnect storm.
        if (Clock.Now - lastRecoveryAt < 60f) return;
        lastRecoveryAt = Clock.Now;
        Log.Message("[TurboTick] 自动恢复：重新连接并从检查点追赶。");
        RoomConnection.ReconnectFromFault();
    }

    private static bool loadPending;
    private static float loadStartedAt;

    public static bool LoadPending => loadPending;
    internal static bool IsExpectedWorldLoad(string? saveName) => loadPending
        && Session.Phase == SharedPhase.Synchronizing && saveName is not null
        && string.Equals(saveName, expectedSaveName, StringComparison.Ordinal);

    private static string? expectedSaveName;

    public static void HoldForPublication()
    {
        reportingArmed = false;
        loadPending = false;
        expectedSaveName = null;
        tickGuard.Cancel();
        Session.BeginLoading(Session.Epoch, "publishing");
        SimulationDiagnostics.Event("publication-held");
    }

    public static void BeginWorldLoad(string saveName)
    {
        loadPending = true;
        expectedSaveName = saveName;
        loadStartedAt = Clock.Now;
        Session.WorldLoaded();
        SimulationDiagnostics.Event("load-requested");
    }

    /// <summary>Only the final deserialized Game receives LoadedGame. Its queued callback runs after
    /// post-load fixups and the vanilla pause-on-load callback, while our tick guard still holds simulation.</summary>
    internal static void ScheduleWorldLoaded(Game owner, string? actualSaveName, ulong? savedRandomState, bool stableSnapshotIds)
    {
        if (!loadPending || !string.Equals(actualSaveName, expectedSaveName, StringComparison.Ordinal)) return;
        var generation = Session.LoadGeneration;
        var world = Session.WorldId;
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            if (!loadPending || !ReferenceEquals(Current.Game, owner)
                || Current.ProgramState != ProgramState.Playing || Scribe.mode != LoadSaveMode.Inactive
                || Session.WorldId != world || !string.Equals(actualSaveName, expectedSaveName, StringComparison.Ordinal)
                || generation != Session.LoadGeneration || Session.Phase != SharedPhase.Synchronizing) return;
            if (!stableSnapshotIds)
            {
                OnWorldLoadFailed("共享快照未保留稳定对象 ID，请由控制者重新发布殖民地");
                return;
            }
            if (savedRandomState is not { } randomState)
            {
                OnWorldLoadFailed("共享快照缺少完整随机状态，请重新发布殖民地");
                return;
            }
            try { TurboEnvironmentState.RestoreAll(Find.Maps, Find.TickManager.TicksGame); }
            catch (Exception error) { OnWorldLoadFailed(error.Message); return; }
            SimulationRandom.Restore(randomState);
            try { SharedPathWarmup.CompleteLoadedRequests(Find.Maps); }
            catch (Exception error) { OnWorldLoadFailed(error.Message); return; }
            if (!Session.TryFinishLoading(generation)) return;
            loadPending = false;
            expectedSaveName = null;
            reportingArmed = true;
            lastHashedTick = -1;
            NativeSpeedSync.ApplyConfirmed();
            SetLoadedStatus();
            SimulationDiagnostics.Event("world-ready");
        });
    }

    private static void SetLoadedStatus() => Log.Message("[TurboTick] 共享殖民地已载入并开始");

    public static void OnWorldLoadFailed(string reason)
    {
        loadPending = false;
        reportingArmed = false;
        expectedSaveName = null;
        tickGuard.Cancel();
        Session.Fault("载入共享存档失败：" + reason);
        Log.Error("[TurboTick] " + reason);
    }

    /// <summary>Applies commands whose tick has arrived. Also used while paused so late commands still land.</summary>
    public static void ApplyDue()
    {
        // Never apply to the previous colony while a replacement snapshot is still loading.
        if (Session.Phase != SharedPhase.Playing) return;
        var due = Session.Commands.TakeThrough(Session.Barrier.AppliedTick);
        if (due.Count == 0) return;
        using var random = SimulationRandom.Enter();
        foreach (var command in due) SharedActions.Apply(command);
    }

    public static void Update()
    {
        if (loadPending && Clock.Now - loadStartedAt > 120f)
        {
            loadPending = false;
            OnWorldLoadFailed("载入超时：120 秒内没有切换到共享存档");
        }
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
        if (HeadlessRuntime.HoldLocalTicks) return false;
        return SharedColony.MayAdvanceTick();
    }

    // One bounded burst per frame, after the native loop returns. Never recurse from a tick postfix.
    private static void Postfix() => SharedColony.CatchUpTick();
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class TickApplyPatch
{
    internal readonly record struct Execution(bool Shared, SimulationTickGuard.Permit Permit,
        SimulationDiagnostics.Observation Audit, IDisposable? Random);

    private static bool Prefix(TickManager __instance, out Execution __state)
    {
        __state = default;
        if (!SharedColony.HasSharedWorld) return true;
        var audit = SimulationDiagnostics.Before(__instance);
        if (!SharedColony.TryBeginTick(__instance, out var permit)) return false;
        try { __state = new(true, permit, audit, SimulationRandom.Enter()); }
        catch (Exception error) { SharedColony.CancelTick(error); throw; }
        return true;
    }

    private static void Postfix(TickManager __instance, Execution __state, bool __runOriginal)
    {
        // Harmony also runs postfixes after a prefix skipped the original. A default state commits nothing.
        if (!__state.Shared) return;
        if (!__runOriginal) { SharedColony.SkipTick(); return; }
        SharedColony.CompleteTick(__instance, __state.Permit);
        SimulationDiagnostics.After(__instance, __state.Audit);
    }

    private static Exception? Finalizer(Exception? __exception, Execution __state)
    {
        try
        {
            if (__exception is not null && __state.Shared) SharedColony.CancelTick(__exception);
        }
        finally { __state.Random?.Dispose(); }
        return __exception;
    }
}
