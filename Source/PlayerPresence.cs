using System.Net.Sockets;
using RimWorld.Planet;
using TurboTick.Networking;
using TurboTick.Simulation;
using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;
using RemotePresence = TurboTick.Networking.PlayerPresence;

namespace TurboTick;

/// <summary>Wall-clock presentation only. Never runs from a simulation tick, captures an input command,
/// writes a save, or uses Verse.Rand. All game/GUI access stays on the main thread.</summary>
internal static class PlayerPresence
{
    private sealed class Cursor
    {
        internal Vector3 From;
        internal Vector3 To;
        internal long Received;
        internal int MapId;
        internal long Epoch;
        internal bool Visible;
        internal Vector3 Position(long now) => Vector3.Lerp(From, To, Math.Clamp((now - Received) / 100f, 0f, 1f));
    }

    private static readonly Dictionary<long, Cursor> cursors = [];
    private static DedicatedClient? owner;
    private static Task<bool>? pending;
    private static PresenceUpdate? pendingState;
    private static PresenceUpdate? sentState;
    private static long lastSample = long.MinValue / 2;
    private static long lastSent = long.MinValue / 2;
    private static long observedEpoch = long.MinValue;
    internal static PresenceUpdate? LocalState { get; private set; }

    internal static void Update()
    {
        var client = RoomConnection.Client;
        if (!ReferenceEquals(owner, client)) { Reset(); owner = client; }
        if (client?.IsConnected != true)
        {
            cursors.Clear();
            LocalState = null;
            return;
        }
        var now = Environment.TickCount64;
        if (observedEpoch != client.PresenceEpoch)
        {
            cursors.Clear(); sentState = null; LocalState = null;
            lastSample = lastSent = long.MinValue / 2;
            observedEpoch = client.PresenceEpoch;
        }
        if (pending is { IsCompleted: true })
        {
            if (pending.GetAwaiter().GetResult()) { sentState = pendingState; lastSent = now; }
            pending = null;
            pendingState = null;
        }
        var players = client.Players;
        var presences = client.Presences;
        foreach (var id in cursors.Keys.ToArray())
            if (!players.Any(p => p.Id == id) || !presences.Any(p => p.PlayerId == id)) cursors.Remove(id);
        foreach (var presence in presences)
        {
            if (presence.PlayerId == client.PlayerId || presence.State.Epoch != client.PresenceEpoch) continue;
            if (!cursors.TryGetValue(presence.PlayerId, out var cursor))
                cursors[presence.PlayerId] = cursor = new Cursor();
            if (presence.UpdatedAtMilliseconds == cursor.Received) continue;
            var state = presence.State;
            var target = new Vector3(state.X, 0, state.Z);
            cursor.From = cursor.Visible && state.Visible && cursor.MapId == state.MapId &&
                now - cursor.Received < PresenceRules.StaleMilliseconds ? cursor.Position(now) : target;
            cursor.To = target;
            cursor.MapId = state.MapId;
            cursor.Epoch = state.Epoch;
            cursor.Visible = state.Visible;
            cursor.Received = presence.UpdatedAtMilliseconds;
        }
        if (now - lastSample < PresenceRules.SendIntervalMilliseconds) return;
        lastSample = now;
        var sample = Capture(client.PresenceEpoch);
        LocalState = sample;
        if (pending is not null || (sample == sentState && now - lastSent < PresenceRules.KeepAliveMilliseconds)) return;
        pendingState = sample;
        pending = SendObservedAsync(client, sample);
    }

    private static async Task<bool> SendObservedAsync(DedicatedClient client, PresenceUpdate state)
    {
        try { return await client.SendPresenceAsync(state).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { return false; } // The connection reader owns transport errors; presentation never faults the simulation.
    }

    private static PresenceUpdate Capture(long epoch)
    {
        var phase = SharedColony.Session.Phase;
        var activity = phase == SharedPhase.Playing ? PlayerActivity.Playing
            : phase is SharedPhase.LoadingWorld or SharedPhase.Synchronizing ? PlayerActivity.Synchronizing
            : phase == SharedPhase.Faulted ? PlayerActivity.Faulted : PlayerActivity.Lobby;
        if (activity != PlayerActivity.Playing || SharedColony.Session.Epoch != epoch ||
            Current.ProgramState != ProgramState.Playing || Find.CurrentMap is not { } map ||
            WorldRendererUtility.WorldSelected)
            return new PresenceUpdate(epoch, Activity: activity);
        var mouse = UI.MousePositionOnUIInverted;
        var visible = TurboTickMod.Settings.showPlayerCursors && Application.isFocused &&
            mouse.x >= 0 && mouse.x < UI.screenWidth && mouse.y >= 0 && mouse.y < UI.screenHeight - 40 &&
            !TurboHud.Contains(mouse) && !Find.WindowStack.MouseObscuredNow &&
            !Find.WindowStack.AnyWindowAbsorbingAllInput;
        var point = visible ? UI.MouseMapPosition() : Vector3.zero;
        visible &= float.IsFinite(point.x) && float.IsFinite(point.z) &&
            point.x >= 0 && point.z >= 0 && point.x < map.Size.x && point.z < map.Size.z;
        // A hidden sample deliberately erases the previous coordinates rather than leaking a stale cursor.
        return new PresenceUpdate(epoch, map.uniqueID, visible ? Quantize(point.x) : 0,
            visible ? Quantize(point.z) : 0, visible, activity);
    }

    private static float Quantize(float value) => MathF.Floor(value * 32f) / 32f;

    internal static void Draw()
    {
        var client = RoomConnection.Client;
        if (client?.IsConnected != true || !TurboTickMod.Settings.showPlayerCursors ||
            Current.ProgramState != ProgramState.Playing || SharedColony.Session.Phase != SharedPhase.Playing ||
            SharedColony.Session.Epoch != client.PresenceEpoch || Find.CurrentMap is not { } map ||
            WorldRendererUtility.WorldSelected || Find.WindowStack.AnyWindowAbsorbingAllInput ||
            Event.current.type != EventType.Repaint) return;
        var oldFont = Text.Font;
        var oldAnchor = Text.Anchor;
        var oldWrap = Text.WordWrap;
        var oldColor = GUI.color;
        try
        {
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = false;
            GUI.color = Color.white;
            var now = Environment.TickCount64;
            foreach (var player in client.Players)
            {
                if (player.Id == client.PlayerId || !cursors.TryGetValue(player.Id, out var cursor) ||
                    !cursor.Visible || cursor.Epoch != client.PresenceEpoch || cursor.MapId != map.uniqueID ||
                    now - cursor.Received > PresenceRules.StaleMilliseconds) continue;
                var point = cursor.Position(now);
                if (point.x < 0 || point.z < 0 || point.x >= map.Size.x || point.z >= map.Size.z) continue;
                var screen = UI.MapToUIPosition(point);
                if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y) || screen.x < 0 || screen.x > UI.screenWidth - 20 ||
                    screen.y < 0 || screen.y > UI.screenHeight - 40 || TurboHud.Contains(screen)) continue;
                var color = ColorFor(player.ColorIndex);
                var a = screen;
                var b = screen + new Vector2(4, 17);
                var c = screen + new Vector2(9, 11);
                var d = screen + new Vector2(17, 10);
                // A crisp outlined arrow needs no external texture or engine resource.
                Stroke(a, b, color); Stroke(b, c, color); Stroke(c, d, color); Stroke(d, a, color);
                var name = DisplayName(player.Name);
                var size = Text.CalcSize(name);
                var tag = new Rect(Math.Clamp(screen.x + 14, 2, Math.Max(2, UI.screenWidth - Math.Min(180, size.x + 12) - 2)),
                    Math.Clamp(screen.y + 17, 2, Math.Max(2, UI.screenHeight - 60)), Math.Min(180, size.x + 12), 22);
                Widgets.DrawBoxSolidWithOutline(tag, new Color(0.055f, 0.075f, 0.095f, 0.9f), color, 1);
                GUI.color = color;
                Widgets.LabelEllipses(new Rect(tag.x + 6, tag.y + 2, tag.width - 12, 18), name);
                GUI.color = Color.white;
            }
        }
        finally { Text.Font = oldFont; Text.Anchor = oldAnchor; Text.WordWrap = oldWrap; GUI.color = oldColor; }
    }

    private static void Stroke(Vector2 a, Vector2 b, Color color)
    {
        Widgets.DrawLine(a, b, new Color(0.015f, 0.02f, 0.025f, 0.95f), 4f);
        Widgets.DrawLine(a, b, color, 2f);
    }

    internal static string DisplayName(string name) => name.Replace('<', '‹').Replace('>', '›');

    internal static Color ColorFor(int index) => (index % PresenceRules.ColorCount) switch
    {
        0 => new Color(0.35f, 0.85f, 1f), 1 => new Color(1f, 0.68f, 0.35f),
        2 => new Color(0.65f, 0.95f, 0.45f), 3 => new Color(0.9f, 0.6f, 1f),
        4 => new Color(1f, 0.5f, 0.63f), 5 => new Color(0.35f, 0.94f, 0.78f),
        6 => new Color(1f, 0.89f, 0.42f), 7 => new Color(0.63f, 0.73f, 1f),
        8 => new Color(0.97f, 0.69f, 0.84f), 9 => new Color(0.65f, 0.91f, 0.95f),
        10 => new Color(0.86f, 0.85f, 0.62f), _ => new Color(0.77f, 0.8f, 0.88f)
    };

    internal static string Status(PlayerInfo player, RemotePresence? presence)
    {
        var state = player.Id == RoomConnection.Client?.PlayerId ? LocalState : presence?.State;
        if (state is null || (player.Id != RoomConnection.Client?.PlayerId &&
            Environment.TickCount64 - presence!.UpdatedAtMilliseconds > PresenceRules.StaleMilliseconds)) return T("TurboTick_HudStale");
        return state.Activity switch
        {
            PlayerActivity.Lobby => T("TurboTick_HudLobby"),
            PlayerActivity.Synchronizing => T("TurboTick_HudSyncing"),
            PlayerActivity.Faulted => T("TurboTick_HudFaulted"),
            _ when state.MapId < 0 => T("TurboTick_HudAway"),
            _ when state.MapId != Find.CurrentMap?.uniqueID => T("TurboTick_HudOtherMap"),
            _ when !state.Visible => T("TurboTick_HudCursorHidden"),
            _ => T("TurboTick_HudOnMap")
        };
    }

    internal static void Reset()
    {
        cursors.Clear(); owner = null; pending = null; pendingState = null; sentState = null; LocalState = null;
        lastSample = lastSent = long.MinValue / 2;
        observedEpoch = long.MinValue;
    }
}
