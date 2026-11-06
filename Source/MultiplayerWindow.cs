using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>The title-screen and in-game entry point share the same room UI and connection state.</summary>
public sealed class MultiplayerWindow : Window
{
    private enum Tab { Host, Join }
    private enum WorldSource { Save, New, Current }
    private Tab tab;
    private WorldSource source;
    private readonly List<FileInfo> saves = [];
    private FileInfo? selectedSave;
    private string search = "";
    private Vector2 scroll;
    private Vector2 bodyScroll;
    private Vector2 rosterScroll;
    private string error = "";
    private bool enteredRoom;
    private static readonly Color Background = new(0.055f, 0.075f, 0.095f, 1f);
    private static readonly Color Card = new(0.09f, 0.115f, 0.14f, 1f);
    private static readonly Color Accent = new(0.32f, 0.83f, 0.73f, 1f);
    private static readonly Color Muted = new(0.62f, 0.69f, 0.75f, 1f);

    public override Vector2 InitialSize => new(Math.Min(1000f, UI.screenWidth - 40f), Math.Min(700f, UI.screenHeight - 40f));

    public MultiplayerWindow()
    {
        doCloseX = true;
        absorbInputAroundWindow = true;
        preventCameraMotion = true;
        forcePause = false; // A local window must never pause a shared simulation.
        closeOnAccept = false;
        doWindowBackground = true;
        onlyOneOfTypeAllowed = true;
        source = Current.ProgramState == ProgramState.Playing ? WorldSource.Current : WorldSource.New;
        RefreshSaves();
    }

    public static void Open()
    {
        if (Find.WindowStack == null || Find.WindowStack.WindowOfType<MultiplayerWindow>() != null) return;
        Find.WindowStack.Add(new MultiplayerWindow());
    }

    public override void PostClose()
    {
        base.PostClose();
        TurboTickMod.Settings?.Write();
    }

    public override void WindowUpdate()
    {
        base.WindowUpdate();
        if (enteredRoom && SharedColony.Session.Phase == Simulation.SharedPhase.Playing) Close();
    }

    public override void DoWindowContents(Rect inRect)
    {
        var font = Text.Font;
        var anchor = Text.Anchor;
        var wrap = Text.WordWrap;
        var color = GUI.color;
        try
        {
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Widgets.DrawBoxSolid(inRect, Background);
            Widgets.DrawBoxSolid(new Rect(inRect.x, inRect.y, inRect.width, 3f), Accent);
            Label(new Rect(22, 18, inRect.width - 70, 32), T("TurboTick_Multiplayer"), GameFont.Medium, Color.white);
            Label(new Rect(22, 55, inRect.width - 44, 30), T("TurboTick_LobbySubtitle"), GameFont.Small, Muted);
            var viewport = new Rect(18, 98, inRect.width - 36, inRect.height - 185);
            var body = new Rect(0, 0, Math.Max(720, viewport.width - 18), Math.Max(460, viewport.height - 18));
            Widgets.BeginScrollView(viewport, ref bodyScroll, body);
            try
            {
                if (MultiplayerHosting.ReadyToChoose) DrawHost(body);
                else if (MultiplayerHosting.Pending || TurboState.Authority.SessionRequested)
                    DrawRoom(body);
                else
                {
                    var tabWidth = (body.width - 10) / 2;
                    if (Button(new Rect(body.x, body.y, tabWidth, 36), T("TurboTick_HostTab"), tab == Tab.Host)) { tab = Tab.Host; error = ""; }
                    if (Button(new Rect(body.x + tabWidth + 10, body.y, tabWidth, 36), T("TurboTick_JoinTab"), tab == Tab.Join)) { tab = Tab.Join; error = ""; }
                    body.y += 50;
                    body.height -= 50;
                    DrawConnection(body, asHost: tab == Tab.Host);
                }
            }
            finally { Widgets.EndScrollView(); }
            var status = error.Length > 0 ? error : MultiplayerHosting.Status.Length > 0 ? MultiplayerHosting.Status : RoomConnection.Status;
            Label(new Rect(22, inRect.height - 75, inRect.width - 44, 43), status, GameFont.Tiny,
                error.Length > 0 ? new Color(1f, 0.65f, 0.5f) : Muted);
            Label(new Rect(22, inRect.height - 26, inRect.width - 44, 22), T("TurboTick_LobbyFooter"), GameFont.Tiny, Muted);
        }
        finally { Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap; GUI.color = color; }
    }

    private void DrawHost(Rect area)
    {
        var left = new Rect(area.x, area.y, area.width * 0.59f - 8, area.height);
        var right = new Rect(left.xMax + 16, area.y, area.width - left.width - 16, area.height);
        Panel(left);
        Panel(right);
        DrawHostConfig(right);
        Label(new Rect(left.x + 16, left.y + 12, left.width - 32, 28), T("TurboTick_WorldSource"), GameFont.Small, Color.white);
        var y = left.y + 48;
        if (Current.ProgramState == ProgramState.Playing)
        {
            if (Choice(new Rect(left.x + 12, y, left.width - 24, 66), T("TurboTick_CurrentColony"),
                T("TurboTick_CurrentColonyDesc"), source == WorldSource.Current)) source = WorldSource.Current;
            y += 78;
        }
        else
        {
            if (Choice(new Rect(left.x + 12, y, left.width - 24, 66), T("TurboTick_NewColony"),
                T("TurboTick_NewColonyDesc"), source == WorldSource.New)) source = WorldSource.New;
            y += 78;
        }
        Label(new Rect(left.x + 16, y, left.width - 128, 24), T("TurboTick_ExistingSaves"), GameFont.Tiny, Muted);
        if (Button(new Rect(left.xMax - 96, y - 3, 80, 25), T("TurboTick_Refresh"))) RefreshSaves();
        y += 30;
        search = Widgets.TextField(new Rect(left.x + 16, y, left.width - 32, 27), search);
        TooltipHandler.TipRegion(new Rect(left.x + 16, y, left.width - 32, 27), T("TurboTick_SearchSaves"));
        y += 35;
        var view = new Rect(left.x + 12, y, left.width - 24, Math.Max(20, left.yMax - y - 12));
        var filtered = saves.Where(file => file.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Length == 0)
        {
            Label(view.ContractedBy(8), T("TurboTick_NoSaves"), GameFont.Small, Muted);
            return;
        }
        var content = new Rect(0, 0, view.width - 18, filtered.Length * 60f);
        Widgets.BeginScrollView(view, ref scroll, content);
        try
        {
            for (var i = 0; i < filtered.Length; i++)
            {
                var file = filtered[i];
                var row = new Rect(0, i * 60, content.width, 54);
                var chosen = source == WorldSource.Save && selectedSave?.FullName == file.FullName;
                if (Choice(row, Path.GetFileNameWithoutExtension(file.Name),
                    file.LastWriteTime.ToString("g") + "  ·  " + (file.Length / 1024) + " KiB", chosen))
                { selectedSave = file; source = WorldSource.Save; }
            }
        }
        finally { Widgets.EndScrollView(); }
    }

    private void DrawHostConfig(Rect right)
    {
        var x = right.x + 16;
        var width = right.width - 32;
        Label(new Rect(x, right.y + 12, width, 28), T("TurboTick_DedicatedConnected"), GameFont.Small, Accent);
        Label(new Rect(x, right.y + 51, width, 54), RoomConnection.Endpoint, GameFont.Small, Color.white);
        if (Button(new Rect(x, right.y + 112, width, 30), T("TurboTick_CopyPort")))
            GUIUtility.systemCopyBuffer = RoomConnection.Endpoint;
        Label(new Rect(x, right.y + 156, width, Math.Max(48, right.height - 278)), T("TurboTick_HostSaveGuide"), GameFont.Small, Muted);
        var title = source == WorldSource.New ? T("TurboTick_CreateAndHost") : T("TurboTick_StartHosting");
        var enabled = MultiplayerHosting.ReadyToChoose && (source != WorldSource.Save || selectedSave != null);
        if (Button(new Rect(x, right.yMax - 100, width, 40), title, true, enabled))
        {
            // Close first: the native wizard/load dialog must own the window stack.
            Close();
            if (!MultiplayerHosting.Begin(selectedSave, source == WorldSource.New, source == WorldSource.Current)) Open();
        }
        if (Button(new Rect(x, right.yMax - 48, width, 30), T("TurboTick_Disconnect"))) RoomConnection.Leave();
    }

    private void DrawConnection(Rect area, bool asHost)
    {
        var left = new Rect(area.x, area.y, area.width * 0.51f - 8, area.height);
        var right = new Rect(left.xMax + 16, area.y, area.width - left.width - 16, area.height);
        Panel(left);
        Panel(right);
        var settings = TurboTickMod.Settings;
        var x = left.x + 20;
        var width = left.width - 40;
        Label(new Rect(x, left.y + 14, width, 28), T(asHost ? "TurboTick_HostConnectStep" : "TurboTick_DirectConnect"), GameFont.Small, Color.white);
        settings.PlayerName = Field(new Rect(x, left.y + 56, width, 57), T("TurboTick_Player"), settings.PlayerName);
        settings.ServerHost = Field(new Rect(x, left.y + 123, width, 57), T("TurboTick_Host"), settings.ServerHost);
        settings.ServerPort = Field(new Rect(x, left.y + 190, width, 57), T("TurboTick_Port"), settings.ServerPort);
        if (Button(new Rect(x, left.yMax - 58, width, 40), T(asHost ? "TurboTick_ConnectAsHost" : "TurboTick_JoinGame"), true))
        {
            if (!MultiplayerHosting.Validate(settings, out error)) return;
            settings.Write();
            enteredRoom = true;
            RoomConnection.Connect(settings, asHost);
        }
        Label(new Rect(right.x + 20, right.y + 16, right.width - 40, 30), T(asHost ? "TurboTick_HostGuideTitle" : "TurboTick_JoinGuideTitle"), GameFont.Medium, Color.white);
        Label(new Rect(right.x + 20, right.y + 65, right.width - 40, right.height - 80), T(asHost ? "TurboTick_HostGuide" : "TurboTick_JoinGuide"), GameFont.Small, Muted);
    }

    private void DrawRoom(Rect area)
    {
        Panel(area);
        var x = area.x + 24;
        var width = area.width - 48;
        var detailWidth = width * 0.57f;
        DrawRoster(new Rect(x + detailWidth + 20, area.y + 24, width - detailWidth - 20, area.height - 108));
        Label(new Rect(x, area.y + 24, detailWidth, 40), T(MultiplayerHosting.Pending ? "TurboTick_PreparingRoom" : "TurboTick_RoomTitle"), GameFont.Medium, Accent);
        Label(new Rect(x, area.y + 80, detailWidth, 54), RoomConnection.Status, GameFont.Small, Color.white);
        Label(new Rect(x, area.y + 146, detailWidth, 54), T("TurboTick_DedicatedRoomAddress", RoomConnection.Endpoint), GameFont.Small, Muted);
        if (Button(new Rect(x, area.y + 208, Math.Min(230, detailWidth), 32), T("TurboTick_CopyPort"), false, RoomConnection.Endpoint.Length > 0))
            GUIUtility.systemCopyBuffer = RoomConnection.Endpoint;
        Label(new Rect(x, area.y + 254, detailWidth, 54), T(RoomConnection.Busy && RoomConnection.HostRequested ? "TurboTick_HostConnecting"
            : MultiplayerHosting.Pending ? "TurboTick_HostPublishing"
            : RoomConnection.IsHost && RoomConnection.Client?.PresenceEpoch >= 0 ? "TurboTick_ServerWorldContinues"
            : "TurboTick_WaitForHost"), GameFont.Small, Muted);
        if (SharedColony.Session.Phase == Simulation.SharedPhase.Faulted)
            Label(new Rect(x, area.y + 318, detailWidth, 65), T("TurboTick_SharedFault", SharedColony.Session.LastError), GameFont.Small, new Color(1f, 0.65f, 0.5f));
        var buttonsY = area.yMax - 56;
        var disconnected = !RoomConnection.Busy && RoomConnection.Client?.IsConnected != true;
        if (Button(new Rect(x, buttonsY, width * 0.48f, 36), T(disconnected ? "TurboTick_Connect" : "TurboTick_BackToGame"), true,
            disconnected || Current.ProgramState == ProgramState.Playing && !MultiplayerHosting.Pending))
        {
            if (disconnected) RoomConnection.ReconnectFromFault();
            else Close();
        }
        if (Button(new Rect(x + width * 0.52f, buttonsY, width * 0.48f, 36), T("TurboTick_Disconnect")))
        {
            RoomConnection.Leave();
            enteredRoom = false;
            error = "";
        }
    }

    private void DrawRoster(Rect area)
    {
        var client = RoomConnection.Client;
        var players = client?.Players;
        Label(new Rect(area.x, area.y, area.width, 28), T("TurboTick_HudTitle", players?.Count ?? 0), GameFont.Small, Color.white);
        if (players == null || players.Count == 0) return;
        var viewport = new Rect(area.x, area.y + 37, area.width, area.height - 37);
        var content = new Rect(0, 0, viewport.width - 18, players.Count * 60);
        Widgets.BeginScrollView(viewport, ref rosterScroll, content);
        try
        {
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var row = new Rect(0, i * 60, content.width, 54);
                var color = PlayerPresence.ColorFor(player.ColorIndex);
                Widgets.DrawBoxSolid(row, new Color(1, 1, 1, 0.035f));
                Widgets.DrawBoxSolid(new Rect(row.x, row.y + 5, 3, row.height - 10), color);
                var name = PlayerPresence.DisplayName(player.Name) + (player.Id == client!.PlayerId ? " · " + T("TurboTick_HudYou") : "");
                Text.Font = GameFont.Small;
                GUI.color = color;
                Widgets.LabelEllipses(new Rect(row.x + 12, row.y + 3, row.width - 24, 24), name);
                var role = T(player.IsHost ? "TurboTick_HudHost" : player.CanEdit ? "TurboTick_HudController" : "TurboTick_HudReadOnly");
                var latency = player.LatencyMilliseconds < 0 ? "…" : T("TurboTick_HudLatency", player.LatencyMilliseconds);
                Label(new Rect(row.x + 12, row.y + 29, row.width - 24, 22), role + " · " + latency, GameFont.Tiny, Muted);
            }
        }
        finally { Widgets.EndScrollView(); GUI.color = Color.white; }
    }

    private void RefreshSaves()
    {
        try
        {
            saves.Clear();
            saves.AddRange(GenFilePaths.AllSavedGameFiles.Where(file =>
                !file.Name.StartsWith("TurboTickCheckpoint_", StringComparison.OrdinalIgnoreCase)
                && !IsSharedCache(file)).OrderByDescending(file => file.LastWriteTime));
        }
        catch (Exception failure) { error = failure.Message; }
    }

    private static bool IsSharedCache(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        return name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string Field(Rect rect, string label, string value)
    {
        Label(new Rect(rect.x, rect.y, rect.width, 24), label, GameFont.Tiny, Muted);
        return Widgets.TextField(new Rect(rect.x, rect.y + 26, rect.width, 28), value);
    }

    private static void Panel(Rect rect) => Widgets.DrawBoxSolid(rect, Card);

    private static void Label(Rect rect, string text, GameFont font, Color color)
    {
        Text.Font = font;
        GUI.color = color;
        Widgets.Label(rect, text);
        GUI.color = Color.white;
    }

    private static bool Choice(Rect rect, string title, string description, bool active)
    {
        Widgets.DrawBoxSolid(rect, active ? new Color(0.12f, 0.23f, 0.24f) : new Color(0.115f, 0.14f, 0.165f));
        if (active) Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3, rect.height), Accent);
        if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
        Text.WordWrap = false;
        Text.Font = GameFont.Small;
        GUI.color = active ? Accent : Color.white;
        Widgets.LabelEllipses(new Rect(rect.x + 13, rect.y + 5, rect.width - 26, 24), title);
        Text.Font = GameFont.Tiny;
        GUI.color = Muted;
        Widgets.LabelEllipses(new Rect(rect.x + 13, rect.y + 30, rect.width - 26, rect.height - 30), description);
        GUI.color = Color.white;
        Text.WordWrap = true;
        return Widgets.ButtonInvisible(rect);
    }

    private static bool Button(Rect rect, string text, bool active = false, bool enabled = true)
    {
        Widgets.DrawBoxSolid(rect, !enabled ? new Color(0.12f, 0.14f, 0.16f) : active
            ? new Color(0.14f, 0.34f, 0.33f) : new Color(0.16f, 0.19f, 0.23f));
        if (enabled && Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
        Text.Anchor = TextAnchor.MiddleCenter;
        Label(rect, text, GameFont.Small, !enabled ? Muted : active ? Accent : Color.white);
        Text.Anchor = TextAnchor.UpperLeft;
        return enabled && Widgets.ButtonInvisible(rect);
    }
}
