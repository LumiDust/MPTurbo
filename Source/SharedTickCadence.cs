using HarmonyLib;
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
