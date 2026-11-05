using UnityEngine;
using Verse;

namespace MpTurbo
{
    public class MpTurboSettings : ModSettings
    {
        /// <summary>总开关。关闭后倍率恒为 1x，其他功能（HUD/诊断）仍可用。</summary>
        public bool enabled = true;

        /// <summary>倍率上限（UI 滑块上限）。</summary>
        public float maxMultiplier = 20f;

        /// <summary>生效速率上限：rate * multiplier 不超过这个值，防止把 CPU 打爆。</summary>
        public float maxRateCap = 60f;

        /// <summary>只有房主能改倍率（非房主按钮禁用；注意这仍是客户端约束，见 docs/COMPAT.md）。</summary>
        public bool hostOnly = false;

        /// <summary>落后时自动把倍率同步降回 1x（**全服生效**），追上后恢复。</summary>
        public bool autoDownshift = false;

        /// <summary>落后多少 tick 判定为"需要追赶"。</summary>
        public int downshiftThreshold = 90;

        /// <summary>显示 HUD。</summary>
        public bool showHud = true;

        /// <summary>HUD 显示的详细程度：0 简略，1 含玩家列表。</summary>
        public int hudDetail = 1;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref maxMultiplier, "maxMultiplier", 20f);
            Scribe_Values.Look(ref maxRateCap, "maxRateCap", 60f);
            Scribe_Values.Look(ref hostOnly, "hostOnly", false);
            Scribe_Values.Look(ref autoDownshift, "autoDownshift", false);
            Scribe_Values.Look(ref downshiftThreshold, "downshiftThreshold", 90);
            Scribe_Values.Look(ref showHud, "showHud", true);
            Scribe_Values.Look(ref hudDetail, "hudDetail", 1);
        }

        public void Draw(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled(T("MPTurbo_Enable"), ref enabled, T("MPTurbo_EnableDesc"));
            listing.Gap(6f);

            listing.Label(T("MPTurbo_Multiplier", TurboState.Multiplier.ToString("0.0")), 24f);
            float m = listing.Slider(TurboState.Multiplier, 1f, maxMultiplier);
            if (Mathf.Abs(m - TurboState.Multiplier) > 0.001f)
                MpSync.SetMultiplier(m);
            listing.Gap(6f);

            listing.CheckboxLabeled(T("MPTurbo_HostOnly"), ref hostOnly, T("MPTurbo_HostOnlyDesc"));
            listing.CheckboxLabeled(T("MPTurbo_AutoDownshift"), ref autoDownshift, T("MPTurbo_AutoDownshiftDesc"));
            listing.Label(T("MPTurbo_Threshold", downshiftThreshold), 24f);
            downshiftThreshold = (int)listing.Slider(downshiftThreshold, 30f, 300f);
            listing.Gap(6f);

            listing.CheckboxLabeled(T("MPTurbo_ShowHud"), ref showHud, T("MPTurbo_ShowHudDesc"));
            listing.Label(T("MPTurbo_MaxRateCap", maxRateCap.ToString("0")), 24f);
            maxRateCap = listing.Slider(maxRateCap, 15f, 240f);

            listing.Gap(12f);
            listing.Label(MpBridge.StatusLine, 24f);

            listing.End();
        }

        static string T(string key, params object[] args)
        {
            string raw = key.Translate().ToString();
            return (args == null || args.Length == 0) ? raw : string.Format(raw, args);
        }
    }
}
