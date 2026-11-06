using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>1.7 game adapter; no calls to any reference multiplayer mod or its API.</summary>
public static class TickRatePatch
{
    public static void Install(Harmony harmony)
    {
        var getter = AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TickRateMultiplier))
            ?? throw new MissingMethodException("RimWorld 1.7 TickManager.TickRateMultiplier");
        var info = Harmony.GetPatchInfo(getter);
        if (info != null && info.Owners.Any(owner => owner != "lumidust.turbotick"))
            Log.Warning("[TurboTick] 倍率入口已有其他补丁；请核对兼容清单，运行兼容性尚未验证。");
        harmony.Patch(getter, postfix: new HarmonyMethod(typeof(TickRatePatch), nameof(Postfix)));
    }

    private static void Postfix(TickManager __instance, ref float __result) =>
        __result = TurboState.Apply(__result, __instance.slower?.ForcedNormalSpeed == true);
}
