using HarmonyLib;
using RimWorld;
using Verse;

namespace MpTurbo
{
    /// <summary>
    /// 兜底路径：正常情况下 HUD 与每帧更新由 <see cref="TurboGameComponent"/>（GameComponent 会被
    /// RimWorld 自动实例化）驱动。但如果某个版本/某个 mod 让 GameComponent 没被创建，
    /// 这两个补丁保证功能仍然可用——只在组件不存在时才动手，存在时直接 return，零额外开销。
    /// </summary>
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootUpdate))]
    static class UiRootUpdateFallback
    {
        static void Postfix()
        {
            if (TurboGameComponent.Alive) return;
            MpBridge.Refresh();
            TurboState.Update();
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    static class UiRootGuiFallback
    {
        static void Postfix()
        {
            if (TurboGameComponent.Alive) return;
            TurboHud.Draw();
        }
    }
}
