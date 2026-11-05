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
        Try(harmony, "designation", typeof(DesignationCapturePatch));
        Try(harmony, "blueprint", typeof(BlueprintCapturePatch));
        Try(harmony, "draft", typeof(DraftCapturePatch));
        Try(harmony, "order", typeof(OrderCapturePatch));
        Try(harmony, "transport", typeof(TransportCapturePatch));
        Try(harmony, "tradeguard", typeof(TradeGuardPatch));
        Try(harmony, "gate", typeof(TickGatePatch));
        Try(harmony, "apply", typeof(TickApplyPatch));
        // Without the gate a peer could simulate past the server and diverge; without capture nothing syncs.
        AllCritical = status.GetValueOrDefault("designation")
            && status.GetValueOrDefault("blueprint")
            && status.GetValueOrDefault("draft")
            && status.GetValueOrDefault("order")
            && status.GetValueOrDefault("gate")
            && status.GetValueOrDefault("apply");
        if (!AllCritical)
            Log.Warning("[TurboTick] 部分补丁未挂载，共享殖民地将被禁用：" + string.Join(" ",
                status.Select(pair => pair.Key + (pair.Value ? "✓" : "✗"))));
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
