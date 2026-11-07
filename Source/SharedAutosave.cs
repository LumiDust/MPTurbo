using HarmonyLib;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Vanilla autosaves once per in-game day: 60000 ticks is the interval, and DoAutosave runs
/// through LongEventHandler, whose ForcePause property stops the whole game while it saves. On a shared
/// colony that freezes the authoritative timeline for every connected replica, which the players see as
/// movement stalling for a moment every few hundred ticks. A room has its own checkpoint mechanism, so
/// vanilla autosave is skipped while a session is active.</summary>
[HarmonyPatch(typeof(Autosaver), nameof(Autosaver.AutosaverTick))]
internal static class SharedAutosaveSkip
{
    private static bool Prefix(Autosaver __instance)
    {
        if (!TurboState.Authority.SessionRequested) return true;
        // Keep the internal counter advancing so leaving the room does not immediately trigger a save.
        Traverse.Create(__instance).Field("ticksSinceSave").SetValue(0);
        return false;
    }
}
