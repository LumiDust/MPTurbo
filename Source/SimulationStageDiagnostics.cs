using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

[HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
internal static class SharedFilthRemovalTracePatch
{
    private static void Prefix(Thing __instance)
    {
        if (__instance is Filth) SimulationDiagnostics.Removal(__instance);
    }
}

/// <summary>These patches are only installed for -turbotick-trace-ticks. They observe, never reorder,
/// a small number of stages in the first eight ticks after a snapshot boundary.</summary>
[HarmonyPatch]
internal static class MapStageTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Map), nameof(Map.MapPreTick));
        yield return AccessTools.Method(typeof(Map), nameof(Map.MapPostTick));
    }
    private static void Prefix(Map __instance, MethodBase __originalMethod) =>
        SimulationDiagnostics.Stage(__originalMethod.Name + ":enter", __instance.uniqueID);
    private static void Postfix(Map __instance, MethodBase __originalMethod) =>
        SimulationDiagnostics.Stage(__originalMethod.Name + ":leave", __instance.uniqueID);
}

[HarmonyPatch(typeof(TickList), nameof(TickList.Tick))]
internal static class TickListStageTracePatch
{
    private static readonly AccessTools.FieldRef<TickList, List<List<Thing>>> Buckets =
        AccessTools.FieldRefAccess<TickList, List<List<Thing>>>("thingLists");
    private static readonly AccessTools.FieldRef<TickList, List<Thing>> Register =
        AccessTools.FieldRefAccess<TickList, List<Thing>>("thingsToRegister");
    private static readonly AccessTools.FieldRef<TickList, List<Thing>> Deregister =
        AccessTools.FieldRefAccess<TickList, List<Thing>>("thingsToDeregister");

    private static object Describe(TickList list)
    {
        var buckets = Buckets(list);
        var phase = Find.TickManager.TicksGame % buckets.Count;
        var ids = buckets[phase].Select(thing => thing.thingIDNumber).ToArray();
        static string Digest(IEnumerable<int> values) => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", values))));
        return new { phase, count = ids.Length, order = Digest(ids), members = Digest(ids.Order()),
            register = Digest(Register(list).Select(thing => thing.thingIDNumber)),
            deregister = Digest(Deregister(list).Select(thing => thing.thingIDNumber)) };
    }

    private static void Prefix(TickList __instance, TickerType ___tickType)
    {
        if (SimulationDiagnostics.RecordingStages)
            SimulationDiagnostics.Stage("TickList:" + ___tickType + ":enter", -1, Describe(__instance));
    }
    private static void Postfix(TickList __instance, TickerType ___tickType)
    {
        if (SimulationDiagnostics.RecordingStages)
            SimulationDiagnostics.Stage("TickList:" + ___tickType + ":leave", -1, Describe(__instance));
    }
}

[HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick))]
internal static class EnvironmentStageTracePatch
{
    private static readonly AccessTools.FieldRef<SteadyEnvironmentEffects, Map> MapRef =
        AccessTools.FieldRefAccess<SteadyEnvironmentEffects, Map>("map");
    private static readonly AccessTools.FieldRef<SteadyEnvironmentEffects, int> Cycle =
        AccessTools.FieldRefAccess<SteadyEnvironmentEffects, int>("cycleIndex");
    private static void Prefix(SteadyEnvironmentEffects __instance)
    {
        if (SimulationDiagnostics.RecordingStages)
            SimulationDiagnostics.Stage("SteadyEnvironment:enter", MapRef(__instance).uniqueID,
                new { cycle = Cycle(__instance) });
    }
    private static void Postfix(SteadyEnvironmentEffects __instance)
    {
        if (SimulationDiagnostics.RecordingStages)
            SimulationDiagnostics.Stage("SteadyEnvironment:leave", MapRef(__instance).uniqueID,
                new { cycle = Cycle(__instance) });
    }
}

[HarmonyPatch(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate))]
internal static class CameraRateTracePatch
{
    private static void Postfix(Thing __0, int __result)
    {
        if (SimulationDiagnostics.RecordingStages)
            SimulationDiagnostics.Stage("CameraUpdateRate", __0.Map?.uniqueID ?? -1,
                new { id = __0.thingIDNumber, rate = __result });
    }
}
