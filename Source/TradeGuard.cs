using HarmonyLib;
using RimWorld;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Stops a non-owner from executing a trade while a shared colony runs. Executing it on one client only
/// would move silver and goods on that client alone, which is a desync the player sees as "my silver
/// vanished" — far worse than a clear "the host handles trading" notice.
/// </summary>
[HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
internal static class TradeGuardPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!SharedColony.Gating) return true;
        var settings = TurboTickMod.Settings;
        if (InteractionPolicy.CanRunColonyWideAction(
                sharedPlaying: true,
                isOwner: RoomConnection.IsCheckpointOwner,
                allowEveryone: settings is { everyoneMayTrade: true }))
            return true;
        __result = false;
        Messages.Message(InteractionPolicy.DeniedMessage("交易"), MessageTypeDefOf.RejectInput, false);
        return false;
    }
}
