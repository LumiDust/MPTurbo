using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace TurboTick;

/// <summary>The camera's view and the local map selection are presentation state. A shared world must
/// not choose its simulation interval from either, particularly when the resident has no camera.</summary>
[HarmonyPatch(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate), new[] { typeof(Thing) })]
internal static class SharedTickCadence
{
    private static bool Prefix(ref int __result)
    {
        if (!SharedColony.HasSharedWorld) return true;
        // Full-rate simulation everywhere. A future global interval may be negotiated, never inferred
        // from each machine's camera distance or frame rate.
        __result = 1;
        return false;
    }
}

/// <summary>Projectiles override the base cadence with a camera-visibility test of their own, so the
/// <see cref="SharedTickCadence"/> patch never reaches them. A resident without a camera would update
/// every 15th tick while on-screen clients update every tick, which changes how often impact and
/// explosion rolls consume the simulation stream. Pin one interval for every replica.</summary>
[HarmonyPatch]
internal static class SharedProjectileCadence
{
    private static MethodBase TargetMethod() => AccessTools.PropertyGetter(typeof(Projectile), nameof(Projectile.UpdateRateTicks));

    private static bool Prefix(ref int __result)
    {
        if (!SharedColony.HasSharedWorld) return true;
        __result = 1;
        return false;
    }
}

/// <summary>World objects pick their interval from the planet view selection. The resident never has the
/// planet screen selected, so caravans and ships would advance at a fifteenth of the client rate.</summary>
[HarmonyPatch]
internal static class SharedWorldObjectCadence
{
    private static MethodBase TargetMethod() => AccessTools.PropertyGetter(typeof(WorldObject), "UpdateRateTicks");

    private static bool Prefix(ref int __result)
    {
        if (!SharedColony.HasSharedWorld) return true;
        __result = 1;
        return false;
    }
}
