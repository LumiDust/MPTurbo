using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace MpTurbo
{
    /// <summary>
    /// 唯一的补丁点：TickRateMultiplier。
    ///
    /// - 单机 / 没装 MP：补原版 <c>TickManager.get_TickRateMultiplier</c> 的 Postfix。
    /// - 装了 MP：**不补原版**（MP 自己已经 Prefix 接管了它），改为补 MP 的两个
    ///   <c>TickRateMultiplier(TimeSpeed)</c> 实例方法：
    ///     · Multiplayer.Client.AsyncTimeComp         （每张地图一份）
    ///     · Multiplayer.Client.AsyncWorldTimeComp    （世界/星球层）
    ///   MP 的 TickPatch.ActualRateMultiplier 正是从这两个方法取速率（asyncTime 开时取当前 map，
    ///   关时取所有 map + world 的最小值），所以在这两个方法上乘系数，等价于"游戏整体提速"，
    ///   而且与 MP 自己的 Fast/Superfast/Ultrafast 走同一条路径，不会破坏命令调度。
    /// </summary>
    public static class TickRatePatches
    {
        public static void Install(Harmony harmony)
        {
            var vanilla = AccessTools.Method(typeof(TickManager), "get_TickRateMultiplier");
            if (vanilla != null)
                harmony.Patch(vanilla, postfix: new HarmonyMethod(typeof(TickRatePatches), nameof(VanillaPostfix)));
            else
                Log.Warning("[MP Turbo] 找不到 TickManager.get_TickRateMultiplier，倍率可能无效。");

            TryPatchMp(harmony, "Multiplayer.Client.AsyncTimeComp");
            TryPatchMp(harmony, "Multiplayer.Client.AsyncWorldTimeComp");
        }

        /// <summary>单机路径。装了 MP 时直接返回，避免和 MP 的 Postfix 叠加造成双重缩放。</summary>
        static void VanillaPostfix(ref float __result)
        {
            if (MpBridge.MpPresent) return;
            __result = TurboState.Apply(__result);
        }

        /// <summary>MP 路径：两个 comp 共用一个 Postfix。</summary>
        public static void MpPostfix(ref float __result)
        {
            __result = TurboState.Apply(__result);
        }

        static void TryPatchMp(Harmony harmony, string typeName)
        {
            try
            {
                var type = GenTypes.GetTypeInAnyAssembly(typeName);
                if (type == null) return;   // 没装 MP，正常

                var method = type.GetMethod("TickRateMultiplier",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(TimeSpeed) }, null);

                if (method == null)
                {
                    Log.Warning("[MP Turbo] " + typeName + ".TickRateMultiplier 签名变了，倍率对该部分无效（其余功能正常）。");
                    return;
                }

                harmony.Patch(method, postfix: new HarmonyMethod(typeof(TickRatePatches), nameof(MpPostfix)));
                Log.Message("[MP Turbo] 已挂载 " + typeName + ".TickRateMultiplier");
            }
            catch (Exception e)
            {
                Log.Warning("[MP Turbo] 补丁 " + typeName + " 失败: " + e.Message);
            }
        }
    }
}
