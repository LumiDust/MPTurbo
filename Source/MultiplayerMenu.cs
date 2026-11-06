using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

// Scope the option insertion to the vanilla main menu, so unrelated option lists stay intact.
[HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
internal static class MultiplayerMenuScope
{
    internal static bool Drawing;
    private static void Prefix(ref Rect __0, out bool __state)
    {
        __state = Drawing;
        Drawing = true;
        __0.height += 45f;
    }
    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        Drawing = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
internal static class MultiplayerMenuOption
{
    private static void Prefix(List<ListableOption> __1)
    {
        if (!MultiplayerMenuScope.Drawing) return;
        var label = T("TurboTick_Multiplayer");
        if (__1.Any(option => option.label == label)) return;
        int index;
        if (Current.ProgramState == ProgramState.Entry)
        {
            // The drawer also renders a second option list for web links. Only the primary play list
            // contains NewColony; inserting at zero when it is absent creates a second Multiplayer button.
            index = __1.FindIndex(option => option.label == "NewColony".Translate().ToString());
            if (index < 0) return;
            index++;
        }
        else
        {
            if (!__1.Any(option => option.label == "ReviewScenario".Translate().ToString()
                || option.label == "QuitToMainMenu".Translate().ToString()
                || option.label == "SaveAndQuitToMainMenu".Translate().ToString())) return;
            index = 0;
        }
        __1.Insert(index, new ListableOption(label, MultiplayerWindow.Open));
    }
}

// GameComponents do not exist on the title screen: joining and downloading must still progress there.
[HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.UIRootUpdate))]
internal static class MultiplayerEntryUpdate
{
    private static void Postfix()
    {
        if (HeadlessRuntime.Active) return;
        RoomConnection.Pump();
        MultiplayerHosting.Update();
    }
}

[HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
internal static class MultiplayerReturnToMenu
{
    private static void Prefix()
    {
        if (!HeadlessRuntime.Active) RoomConnection.Leave();
    }
}

[HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
internal static class MultiplayerShutdown
{
    private static void Prefix()
    {
        if (HeadlessRuntime.Active) HeadlessRuntime.Shutdown();
        else RoomConnection.Leave();
    }
}
