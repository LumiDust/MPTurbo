using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MpTurbo
{
    /// <summary>
    /// 右上角小面板：当前倍率、落后多少 tick、谁在拖后腿，以及三个按钮（-、+、1x）。
    /// 全部走 GameComponentOnGUI，不补任何 UI 方法，避免和其它 HUD mod 抢补丁点。
    /// </summary>
    public static class TurboHud
    {
        const float Width = 268f;
        const float LineH = 20f;

        static readonly List<string> tmp = new List<string>();

        public static void Draw()
        {
            var s = MpTurboMod.Settings;
            if (s == null || !s.showHud) return;
            if (Current.ProgramState != ProgramState.Playing) return;

            bool detail = s.hudDetail > 0 && MpBridge.IsInMultiplayer;
            int lines = 2 + (detail ? 1 : 0);
            float height = LineH * lines + 34f;

            var rect = new Rect(UI.screenWidth - Width - 8f, 8f, Width, height);
            Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.55f));

            var inner = rect.ContractedBy(6f);
            float y = inner.y;

            // 标题行
            string eff = TurboState.EffectiveMultiplier.ToString("0.0");
            string suffix = !s.enabled ? "MPTurbo_Off".Translate().ToString()
                : TurboState.AutoActive ? "MPTurbo_CatchingUp".Translate().ToString()
                : !MpBridge.MpPresent ? "MPTurbo_Solo".Translate().ToString()
                : MpBridge.Frozen ? "MPTurbo_Frozen".Translate().ToString()
                : "";

            var title = new Rect(inner.x, y, inner.width, LineH);
            Text.Font = GameFont.Small;
            Widgets.Label(title, "MP Turbo  ×" + eff + (suffix.Length > 0 ? "  [" + suffix + "]" : ""));
            y += LineH;

            // 状态行
            Text.Font = GameFont.Tiny;
            string status;
            if (MpBridge.IsInMultiplayer)
                status = "MPTurbo_HudMp".Translate(MpBridge.PlayerCount, MpBridge.Lag).ToString();
            else
                status = "MPTurbo_HudSolo".Translate().ToString();
            Widgets.Label(new Rect(inner.x, y, inner.width, LineH), status);
            y += LineH;

            // 拖后腿的人
            if (detail)
            {
                MpBridge.WorstBehind(2, tmp);
                string who = tmp.Count > 0
                    ? "MPTurbo_HudBehind".Translate(string.Join(", ", tmp.ToArray())).ToString()
                    : "MPTurbo_HudNoLag".Translate().ToString();
                Widgets.Label(new Rect(inner.x, y, inner.width, LineH), who);
            }

            // 按钮
            float by = rect.yMax - 26f;
            float bw = 54f;
            bool can = MpSync.CanEdit;

            if (Widgets.ButtonText(new Rect(inner.x, by, bw, 22f), "-", true, true, can))
                MpSync.Step(-1f);
            if (Widgets.ButtonText(new Rect(inner.x + bw + 4f, by, bw, 22f), "+", true, true, can))
                MpSync.Step(1f);
            if (Widgets.ButtonText(new Rect(inner.x + (bw + 4f) * 2f, by, bw + 12f, 22f), "1x", true, true, can))
                MpSync.SetMultiplier(1f);

            Text.Font = GameFont.Small;
        }
    }
}
