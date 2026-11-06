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
            var panel = new Rect(Math.Max(8, UI.screenWidth - 360), 82, 350, TurboState.Behind ? 112 : 94);
            Widgets.DrawBoxSolid(panel, new Color(0f, 0f, 0f, 0.72f));
            var area = panel.ContractedBy(8);
            var policy = TurboState.Authority.Current;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(area.x, area.y, area.width, 24), T("TurboTick_NativeHud", NativeSpeedSync.Label, policy.Revision));
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

        }
        finally { Text.Font = oldFont; }
    }
}
