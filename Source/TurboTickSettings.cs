using UnityEngine;
using Verse;

namespace TurboTick;

public class TurboTickSettings : ModSettings
{
    // Connection/presentation preferences are local. Enabled/multiplier/cap are local defaults only;
    // once a room is requested every simulation setting must be confirmed by its server.
    public bool enabled = true;
    public float multiplier = 2f;
    public float maxRateCap = 60f;
    public bool showHud = true;
    /// <summary>Detected divergence: reconnect and catch up from the server's checkpoint automatically.</summary>
    public bool autoRecover = true;
    /// <summary>Transport loading sync - unverified in game, therefore off by default.</summary>
    public bool experimentalInteractions = false;
    public string ServerHost = "127.0.0.1";
    public string ServerPort = "30572";
    public string PlayerName = "Player";
    private float draftMultiplier = 2f;
    private float draftCap = 60f;
    private bool draftEnabled;
    private long draftRevision = -1;
    private bool wasRoom;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref enabled, "enabled", true);
        Scribe_Values.Look(ref multiplier, "multiplier", 2f);
        Scribe_Values.Look(ref maxRateCap, "maxRateCap", 60f);
        Scribe_Values.Look(ref showHud, "showHud", true);
        Scribe_Values.Look(ref autoRecover, "autoRecover", true);
        Scribe_Values.Look(ref experimentalInteractions, "experimentalInteractions", false);
        Scribe_Values.Look(ref ServerHost, "serverHost", "127.0.0.1");
        Scribe_Values.Look(ref ServerPort, "serverPort", "30572");
        Scribe_Values.Look(ref PlayerName, "playerName", "Player");
    }

    public void Draw(Rect area)
    {
        var listing = new Listing_Standard();
        listing.Begin(area);
        listing.Label(T("TurboTick_Prototype"), 48f);
        bool room = TurboState.Authority.SessionRequested;
        ServerHost = Field(listing, T("TurboTick_Host"), ServerHost);
        ServerPort = Field(listing, T("TurboTick_Port"), ServerPort);
        PlayerName = Field(listing, T("TurboTick_Player"), PlayerName);
        var buttons = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, 150f, 28f), T("TurboTick_Connect"), true, true, !RoomConnection.Busy))
            RoomConnection.Connect(this);
        if (Widgets.ButtonText(new Rect(buttons.x + 160f, buttons.y, 150f, 28f), T("TurboTick_Disconnect")))
            RoomConnection.Leave();
        listing.Label(RoomConnection.Status, 44f);
        listing.Label(T("TurboTick_Fingerprint", RoomConnection.Fingerprint), 38f);
        listing.Gap(6f);

        var policy = TurboState.Authority.Current;
        if (draftRevision != policy.Revision || wasRoom != room)
        {
            draftRevision = policy.Revision;
            wasRoom = room;
            draftMultiplier = policy.Multiplier;
            draftCap = policy.MaxRate;
            draftEnabled = policy.Enabled;
        }
        listing.CheckboxLabeled(T("TurboTick_Enable"), ref draftEnabled, T("TurboTick_EnableDesc"));
        listing.Label(T("TurboTick_Multiplier", draftMultiplier.ToString("0.0")), 24f);
        draftMultiplier = (float)Math.Round(listing.Slider(draftMultiplier, 1f, 20f), 1);
        listing.Label(T("TurboTick_MaxRateCap", draftCap.ToString("0")), 24f);
        draftCap = (float)Math.Round(listing.Slider(draftCap, 1f, 240f));
        var apply = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(apply.x, apply.y, 270f, 28f),
                room ? T("TurboTick_ApplyRoom") : T("TurboTick_ApplyLocal"), true, true, RoomConnection.CanEdit))
            TurboState.Request(draftEnabled, draftMultiplier, draftCap);
        listing.CheckboxLabeled(T("TurboTick_ShowHud"), ref showHud, T("TurboTick_ShowHudDesc"));
        listing.CheckboxLabeled(T("TurboTick_AutoRecover"), ref autoRecover, T("TurboTick_AutoRecoverDesc"));
        listing.CheckboxLabeled(T("TurboTick_Experimental"), ref experimentalInteractions, T("TurboTick_ExperimentalDesc"));
        listing.Gap(6f);
        var shared = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(shared.x, shared.y, 320f, 28f), T("TurboTick_StartShared"), true, true, TurboState.Authority.SessionConnected && RoomConnection.CanEdit))
            RoomConnection.StartSharedColony();
        listing.Gap(6f);
        var cache = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(cache.x, cache.y, 320f, 28f), T("TurboTick_CacheSnapshot"), true, true, TurboState.Authority.SessionConnected))
            RoomConnection.CacheSnapshot();
        listing.Label(RoomConnection.SnapshotStatus, 42f);
        listing.Gap(6f);
        listing.Label(T("TurboTick_Diagnostics"), 24f);
        listing.Label(Diagnostics.Build(), 150f);
        listing.End();
    }

    private static string Field(Listing_Standard listing, string label, string current)
    {
        var rect = listing.GetRect(28f);
        Widgets.Label(new Rect(rect.x, rect.y, 110f, 26f), label);
        return Widgets.TextField(new Rect(rect.x + 116f, rect.y, Math.Max(150f, rect.width - 116f), 26f), current);
    }

    internal static string T(string key, params object[] args)
    {
        string text = key.Translate().ToString();
        return args.Length == 0 ? text : string.Format(text, args);
    }
}
