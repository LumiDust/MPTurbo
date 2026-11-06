using TurboTick.Networking;
using TurboTick.Simulation;
using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>Compact local room display, below the colonist bar on the left. The original right-hand
/// global controls and native speed controls keep their own space.</summary>
public static class TurboHud
{
    private static bool expanded = true;
    private static Vector2 playerScroll;
    private static readonly Color Background = new(0.045f, 0.065f, 0.085f, 0.94f);
    private static readonly Color Border = new(0.20f, 0.29f, 0.35f, 0.95f);
    private static readonly Color Accent = new(0.35f, 0.85f, 0.77f);
    private static readonly Color Muted = new(0.67f, 0.75f, 0.79f);

    private static bool Visible => TurboTickMod.Settings is { showHud: true } && Current.ProgramState == ProgramState.Playing &&
        (RoomConnection.Client?.IsConnected == true || TurboState.Authority.SessionRequested || RoomConnection.Busy);

    private static Rect Panel
    {
        get
        {
            var count = RoomConnection.Client?.Players.Count ?? 0;
            var width = Math.Max(220, Math.Min(314, UI.screenWidth - 24));
            var listHeight = Math.Min(164, count * 40);
            var height = expanded ? 144 + listHeight : 36;
            return new Rect(12, Math.Min(108, Math.Max(12, UI.screenHeight - height - 64)), width, height);
        }
    }

    internal static bool Contains(Vector2 point) => Visible && Panel.Contains(point);

    public static void Draw()
    {
        // Pointer visibility is independent of the HUD setting, including when the card is collapsed.
        PlayerPresence.Draw();
        if (!Visible) return;
        var oldFont = Text.Font;
        var oldAnchor = Text.Anchor;
        var oldWrap = Text.WordWrap;
        var oldColor = GUI.color;
        try
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = false;
            GUI.color = Color.white;
            var client = RoomConnection.Client;
            var players = client?.Players ?? Array.Empty<PlayerInfo>();
            var panel = Panel;
            Widgets.DrawBoxSolidWithOutline(panel, Background, Border, 1);
            var connected = client?.IsConnected == true;
            var statusColor = connected ? Accent : new Color(1f, 0.73f, 0.36f);
            Widgets.DrawBoxSolid(new Rect(panel.x, panel.y, 3, panel.height), statusColor);
            Widgets.DrawBoxSolid(new Rect(panel.x + 12, panel.y + 14, 6, 6), statusColor);
            var title = new Rect(panel.x + 24, panel.y + 6, panel.width - 120, 25);
            Widgets.LabelEllipses(title, T("TurboTick_HudTitle", players.Count));
            var toggle = new Rect(panel.xMax - 94, panel.y + 5, 25, 26);
            TooltipHandler.TipRegion(toggle, T(expanded ? "TurboTick_HudCollapse" : "TurboTick_HudExpand"));
            if (Widgets.ButtonText(toggle, expanded ? "−" : "+")) expanded = !expanded;
            var room = new Rect(panel.xMax - 65, panel.y + 5, 57, 26);
            if (Widgets.ButtonText(room, T("TurboTick_HudRoom"))) MultiplayerWindow.Open();
            if (!expanded) { ConsumePointer(panel); return; }

            Text.Font = GameFont.Tiny;
            var area = new Rect(panel.x + 12, panel.y + 41, panel.width - 24, panel.height - 49);
            var shared = SharedColony.Session;
            var status = shared.Phase == SharedPhase.Faulted ? T("TurboTick_HudFaulted")
                : shared.Phase is SharedPhase.LoadingWorld or SharedPhase.Synchronizing ? T("TurboTick_HudSyncing")
                : TurboState.Authority.IsHeld ? T("TurboTick_Held")
                : shared.Phase == SharedPhase.Playing ? T("TurboTick_HudSynced", shared.Barrier.AppliedTick)
                : T("TurboTick_HudLobby");
            GUI.color = statusColor;
            Widgets.LabelEllipses(new Rect(area.x, area.y, area.width, 21), status);
            GUI.color = Muted;
            var latency = client?.LatencyMilliseconds ?? -1;
            Widgets.Label(new Rect(area.x, area.y + 23, area.width, 20),
                (latency < 0 ? T("TurboTick_HudLatencyUnknown") : T("TurboTick_HudLatency", latency)) +
                "  ·  " + NativeSpeedSync.Label);
            Widgets.Label(new Rect(area.x, area.y + 44, area.width, 20),
                T("TurboTick_HudTps", TurboState.MeasuredTps.ToString("0"), TurboState.TargetTps.ToString("0")));
            TooltipHandler.TipRegion(new Rect(area.x, area.y, area.width, 65), RoomConnection.Status +
                (shared.LastError is { Length: > 0 } error ? "\n" + error : ""));
            Widgets.DrawLineHorizontal(area.x, area.y + 70, area.width, Border);
            GUI.color = Color.white;
            Widgets.Label(new Rect(area.x, area.y + 75, area.width, 20), T("TurboTick_HudPlayers"));
            if (players.Count > 0)
            {
                var viewport = new Rect(area.x, area.y + 96, area.width, Math.Min(164, players.Count * 40));
                var scrolls = players.Count > 4;
                var content = new Rect(0, 0, viewport.width - (scrolls ? 16 : 0), players.Count * 40);
                Widgets.BeginScrollView(viewport, ref playerScroll, content);
                try
                {
                    var presences = client!.Presences;
                    for (var index = 0; index < players.Count; index++)
                    {
                        var player = players[index];
                        var row = new Rect(0, index * 40, content.width, 38);
                        if (index % 2 == 0) Widgets.DrawBoxSolid(row, new Color(1, 1, 1, 0.035f));
                        var color = PlayerPresence.ColorFor(player.ColorIndex);
                        Widgets.DrawBoxSolid(new Rect(4, row.y + 6, 3, 26), color);
                        GUI.color = color;
                        var name = PlayerPresence.DisplayName(player.Name) + (player.Id == client.PlayerId ? " · " + T("TurboTick_HudYou") : "");
                        Widgets.LabelEllipses(new Rect(13, row.y + 1, row.width - 70, 20), name);
                        GUI.color = Muted;
                        Text.Anchor = TextAnchor.UpperRight;
                        Widgets.Label(new Rect(row.width - 58, row.y + 1, 54, 20), player.LatencyMilliseconds < 0 ? "…" : T("TurboTick_HudLatency", player.LatencyMilliseconds));
                        Text.Anchor = TextAnchor.UpperLeft;
                        var role = T(player.IsHost ? "TurboTick_HudHost" : player.CanEdit ? "TurboTick_HudController" : "TurboTick_HudReadOnly");
                        var presence = presences.FirstOrDefault(p => p.PlayerId == player.Id);
                        var detail = role + " · " + PlayerPresence.Status(player, presence);
                        Widgets.LabelEllipses(new Rect(13, row.y + 19, row.width - 17, 19), detail);
                        TooltipHandler.TipRegion(row, PlayerPresence.DisplayName(player.Name) + "\n" + detail);
                        GUI.color = Color.white;
                    }
                }
                finally { Widgets.EndScrollView(); }
            }
            ConsumePointer(panel);
        }
        finally { Text.Font = oldFont; Text.Anchor = oldAnchor; Text.WordWrap = oldWrap; GUI.color = oldColor; }
    }

    private static void ConsumePointer(Rect panel)
    {
        if (panel.Contains(Event.current.mousePosition) && Event.current.type is EventType.MouseDown or EventType.MouseUp or EventType.ScrollWheel)
            Event.current.Use();
    }
}
