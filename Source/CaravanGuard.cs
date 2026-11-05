using HarmonyLib;
using RimWorld;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Caravan forming removes pawns and goods from the map. Replaying it is not supported, so while a shared
/// colony runs only the host may send one out — otherwise the other client keeps pawns that no longer exist.
/// </summary>
[HarmonyPatch(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan")]
internal static class CaravanGuardPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!SharedColony.Gating) return true;
        if (InteractionPolicy.CanRunColonyWideAction(
                sharedPlaying: true,
                isOwner: RoomConnection.IsCheckpointOwner,
                allowEveryone: TurboTickMod.Settings is { everyoneMayTrade: true }))
            return true;
        __result = false;
        Messages.Message(InteractionPolicy.DeniedMessage("组建远行队"), MessageTypeDefOf.RejectInput, false);
        return false;
    }
}
