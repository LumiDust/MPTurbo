using System.Reflection;
using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>
/// Installs each patch independently. A RimWorld build that moved one method should cost exactly one
/// feature, not silently disable every later patch in the same assembly.
/// </summary>
public static class PatchInstaller
{
    private static readonly Dictionary<string, bool> status = new(StringComparer.Ordinal);

    public static bool AllCritical { get; private set; }

    public static IReadOnlyDictionary<string, bool> Status => status;

    public static void InstallAll(Harmony harmony)
    {
        status.Clear();
        try { SimulationRandom.Validate(); status["simulation-rng"] = true; }
        catch (Exception error) { status["simulation-rng"] = false; Log.Warning("[TurboTick] RNG adapter: " + error.Message); }
        try { TurboEnvironmentState.ValidateBindings(); status["environment-state"] = true; }
        catch (Exception error) { status["environment-state"] = false; Log.Warning("[TurboTick] Environment adapter: " + error.Message); }
        try { TurboPawnPathState.ValidateBindings(); status["pawn-path-state"] = true; }
        catch (Exception error) { status["pawn-path-state"] = false; Log.Warning("[TurboTick] Pawn path adapter: " + error.Message); }
        Try(harmony, "designation", typeof(DesignationCapturePatch));
        Try(harmony, "designation-remove", typeof(DesignationRemoveCapturePatch));
        Try(harmony, "blueprint", typeof(BlueprintCapturePatch));
        Try(harmony, "blueprint-cancel", typeof(BlueprintCancelCapturePatch));
        Try(harmony, "frame-cancel", typeof(FrameCancelCapturePatch));
        Try(harmony, "draft", typeof(DraftCapturePatch));
        Try(harmony, "order", typeof(OrderCapturePatch));
        Try(harmony, "forbid", typeof(ForbidCapturePatch));
        Try(harmony, "priority", typeof(PriorityCapturePatch));
        Try(harmony, "zone-add", typeof(ZoneAddCellCapturePatch));
        Try(harmony, "zone-remove", typeof(ZoneRemoveCellCapturePatch));
        Try(harmony, "zone-delete", typeof(ZoneDeleteCapturePatch));
        Try(harmony, "bill-add", typeof(BillAddCapturePatch));
        Try(harmony, "bill-delete", typeof(BillDeleteCapturePatch));
        Try(harmony, "area-assign", typeof(AreaAssignCapturePatch));
        Try(harmony, "transport", typeof(TransportCapturePatch));
        Try(harmony, "tradeguard", typeof(TradeGuardPatch));
        Try(harmony, "caravanguard", typeof(CaravanGuardPatch));
        Try(harmony, "gate", typeof(TickGatePatch));
        Try(harmony, "apply", typeof(TickApplyPatch));
        Try(harmony, "shared-snapshot-ids", typeof(SharedCompressionPatch));
        Try(harmony, "shared-respawn", typeof(SharedRespawnScopePatch));
        Try(harmony, "shared-filth", typeof(SharedSavedFilthPatch));
        Try(harmony, "pawn-path-resume", typeof(PawnPathResumePatch));
        Try(harmony, "world-pawns-sort", typeof(WorldPawnsDeterministicTickPatch));
        Try(harmony, "wander-herd", typeof(HerdWanderRootDeterministicPatch));
        Try(harmony, "wander-dest", typeof(RandomWanderDestDeterministicPatch));
        Try(harmony, "effect-isolation", typeof(TurboEffectIsolation));
        Try(harmony, "shared-tick-cadence", typeof(SharedTickCadence));
        Try(harmony, "shared-projectile-cadence", typeof(SharedProjectileCadence));
        Try(harmony, "shared-worldobject-cadence", typeof(SharedWorldObjectCadence));
        Try(harmony, "shared-path-requests", typeof(SharedPathRequests));
        // Display-replica presentation: interpolated motion and a clock that follows the host.
        Try(harmony, "replica-draw-pos", typeof(ReplicaDrawPosPatch));
        Try(harmony, "replica-plant-guard", typeof(ReplicaPlantPrintGuard));
        // A shared timeline must not stop when the window loses focus.
        Try(harmony, "background-run", typeof(BackgroundRunPatch));
        Try(harmony, "background-prefs", typeof(BackgroundPrefsPatch));
        // Vanilla autosave's LongEventHandler freezes the shared timeline once per in-game day.
        Try(harmony, "shared-autosave", typeof(SharedAutosaveSkip));
        // A replica has no local path, so vanilla would draw its path line to the origin.
        Try(harmony, "replica-path-line", typeof(ReplicaPathLineSkip));
        Try(harmony, "replica-clock", typeof(ReplicaClockPatch));
        if (SimulationDiagnostics.Enabled)
        {
            Try(harmony, "load-filth-trace", typeof(SharedFilthRemovalTracePatch));
            Try(harmony, "map-stage-trace", typeof(MapStageTracePatch));
            Try(harmony, "tick-list-trace", typeof(TickListStageTracePatch));
            Try(harmony, "environment-stage-trace", typeof(EnvironmentStageTracePatch));
            Try(harmony, "camera-rate-trace", typeof(CameraRateTracePatch));
            Try(harmony, "pawn-tick-trace", typeof(PawnTickTracePatch));
            Try(harmony, "pawn-path-trace", typeof(PawnPathTracePatch));
            Try(harmony, "pawn-job-trace", typeof(PawnJobTracePatch));
            Try(harmony, "rand-value-trace", typeof(RandValueTracePatch));
            Try(harmony, "rand-int-trace", typeof(RandIntTracePatch));
        }
        Try(harmony, "world-load", typeof(WorldLoadScope));
        Try(harmony, "world-load-failure", typeof(SharedLoadFailurePatch));
        Try(harmony, "native-controls", typeof(NativeControlsPatch));
        Try(harmony, "native-speed", typeof(NativeSpeedSetterPatch));
        Try(harmony, "native-pause", typeof(NativePausePatch));
        Try(harmony, "multiplayer-menu-scope", typeof(MultiplayerMenuScope));
        Try(harmony, "multiplayer-menu-option", typeof(MultiplayerMenuOption));
        Try(harmony, "multiplayer-entry-update", typeof(MultiplayerEntryUpdate));
        Try(harmony, "multiplayer-return", typeof(MultiplayerReturnToMenu));
        Try(harmony, "multiplayer-shutdown", typeof(MultiplayerShutdown));
        // Without the gate a peer could simulate past the server and diverge; without capture nothing syncs.
        AllCritical = status.GetValueOrDefault("simulation-rng")
            && status.GetValueOrDefault("environment-state")
            && status.GetValueOrDefault("pawn-path-state")
            && status.GetValueOrDefault("designation")
            && status.GetValueOrDefault("blueprint")
            && status.GetValueOrDefault("draft")
            && status.GetValueOrDefault("order")
            && status.GetValueOrDefault("gate")
            && status.GetValueOrDefault("apply")
            && status.GetValueOrDefault("shared-snapshot-ids")
            && status.GetValueOrDefault("shared-respawn")
            && status.GetValueOrDefault("shared-filth")
            && status.GetValueOrDefault("pawn-path-resume")
            && status.GetValueOrDefault("world-pawns-sort")
            && status.GetValueOrDefault("wander-herd")
            && status.GetValueOrDefault("wander-dest")
            && status.GetValueOrDefault("effect-isolation")
            && status.GetValueOrDefault("shared-tick-cadence")
            && status.GetValueOrDefault("shared-projectile-cadence")
            && status.GetValueOrDefault("shared-worldobject-cadence")
            && status.GetValueOrDefault("shared-path-requests")
            && status.GetValueOrDefault("world-load")
            && status.GetValueOrDefault("world-load-failure")
            && status.GetValueOrDefault("native-controls")
            && status.GetValueOrDefault("native-speed")
            && status.GetValueOrDefault("native-pause");
        var summary = string.Join(" ", status.Select(pair => pair.Key + (pair.Value ? "✓" : "✗")));
        if (AllCritical) Log.Message("[TurboTick] 补丁已就绪：" + summary);
        else Log.Warning("[TurboTick] 部分补丁未挂载，共享殖民地将被禁用：" + summary);
    }

    private static void Try(Harmony harmony, string name, Type patchClass)
    {
        try
        {
            var patched = harmony.CreateClassProcessor(patchClass).Patch();
            status[name] = patched.Count > 0;
            if (patched.Count == 0) Log.Warning($"[TurboTick] 补丁 {name} 没有匹配到目标方法");
        }
        catch (Exception error)
        {
            status[name] = false;
            Log.Warning($"[TurboTick] 补丁 {name} 失败：{error.Message}");
        }
    }
}
