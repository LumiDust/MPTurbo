using HarmonyLib;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>RimWorld 1.7 client. The bundled server/protocol are independent from other multiplayer mods.</summary>
public class TurboTickMod : Mod
{
    public static TurboTickSettings Settings = null!;
    public static Harmony Harmony = null!;

    public TurboTickMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<TurboTickSettings>();
        TurboState.InitializeLocal(Settings);
        LongEventHandler.ExecuteWhenFinished(Bootstrap.Init);
    }

    public override string SettingsCategory() => "TurboTick";
    public override void DoSettingsWindowContents(Rect inRect)
    {
        RoomConnection.Pump();
        Settings.Draw(inRect);
    }
}

[StaticConstructorOnStartup]
public static class Bootstrap
{
    private static bool done;
    static Bootstrap() => Init();
    public static void Init()
    {
        if (done) return;
        done = true;
        try
        {
            TurboTickMod.Harmony = new Harmony("yg.turbotick");
            TickRatePatch.Install(TurboTickMod.Harmony);
            // GUI/update come from GameComponent; no redundant fallback patches.
        }
        catch (Exception error) { Log.Error("[TurboTick] 初始化失败：" + error); }
    }
}
