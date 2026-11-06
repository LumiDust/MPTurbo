using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Objects coexisting in a shared save must coexist after restoration. Vanilla load spawning
/// re-applies placement wipe rules, deleting saved filth under pass-through chunks.</summary>
internal static class SharedRespawn
{
    [ThreadStatic] private static int depth;
    internal static bool Active => depth > 0;
    internal static void Enter() => ++depth;
    internal static void Exit() => --depth;
}

[HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] {
    typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) })]
internal static class SharedRespawnScopePatch
{
    private static void Prefix(bool __5, out bool __state)
    {
        __state = __5 && SharedColony.IsExpectedWorldLoad(WorldLoadScope.CurrentSave);
        if (__state) SharedRespawn.Enter();
    }

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state) SharedRespawn.Exit();
        return __exception;
    }
}

[HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.SpawningWipes))]
internal static class SharedSavedFilthPatch
{
    private static bool Prefix(BuildableDef __1, ref bool __result)
    {
        if (!SharedRespawn.Active || __1 is not ThingDef { category: ThingCategory.Filth }) return true;
        __result = false;
        return false;
    }
}
