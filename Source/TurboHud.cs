using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

public static class TurboHud
{
    public static void Draw()
    {
        var s = TurboTickMod.Settings;
        if (s == null || !s.showHud || Current.ProgramState != ProgramState.Playing) return;
        var oldFont = Text.Font;
        try
        {
            var panel = new Rect(Math.Max(8, UI.screenWidth - 360), 82, 350, 154);
            Widgets.DrawBoxSolid(panel, new Color(0f, 0f, 0f, 0.72f));
            var area = panel.ContractedBy(8);
            var policy = TurboState.Authority.Current;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(area.x, area.y, area.width, 24), $"TurboTick ×{TurboState.EffectiveMultiplier:0.0}  r{policy.Revision}");
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(area.x, area.y + 25, area.width, 20),
                T("TurboTick_HudTps", TurboState.MeasuredTps.ToString("0"), TurboState.TargetTps.ToString("0")));
            var shared = SharedColony.Session;
            Widgets.Label(new Rect(area.x, area.y + 45, area.width, 40),
                TurboState.Authority.IsHeld ? T("TurboTick_Held")
                : shared.Phase == TurboTick.Simulation.SharedPhase.Playing ? T("TurboTick_SharedPlaying", shared.Barrier.ServerTick)
                : shared.Phase == TurboTick.Simulation.SharedPhase.Synchronizing ? T("TurboTick_SharedLoading")
                : shared.Phase == TurboTick.Simulation.SharedPhase.Faulted ? T("TurboTick_SharedFault", shared.LastError)
                : TurboState.Authority.SessionConnected ? T("TurboTick_RoomPrototype")
                : T("TurboTick_Local"));
            if (TurboState.Behind)
                Widgets.Label(new Rect(area.x, area.y + 85, area.width, 18), T("TurboTick_LowTps"));

            float y = area.y + 106;
            bool edit = RoomConnection.CanEdit;
            if (Widgets.ButtonText(new Rect(area.x, y, 118, 25), T(policy.Enabled ? "TurboTick_Disable" : "TurboTick_EnableShort"), true, true, edit))
                TurboState.Request(!policy.Enabled, policy.Multiplier, policy.MaxRate);
            if (Widgets.ButtonText(new Rect(area.x + 124, y, 48, 25), "-", true, true, edit))
                TurboState.Request(policy.Enabled, Math.Max(1f, policy.Multiplier - 1f), policy.MaxRate);
            if (Widgets.ButtonText(new Rect(area.x + 178, y, 48, 25), "+", true, true, edit))
                TurboState.Request(policy.Enabled, Math.Min(20f, policy.Multiplier + 1f), policy.MaxRate);
            if (Widgets.ButtonText(new Rect(area.x + 232, y, 75, 25), "1x", true, true, edit))
                TurboState.Request(policy.Enabled, 1f, policy.MaxRate);
        }
        finally { Text.Font = oldFont; }
    }
}
