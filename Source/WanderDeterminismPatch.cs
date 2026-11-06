using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Wander destination searches traverse region lists whose ordering can vary across peers.
/// Seeding wander root selection with the pawn ID and current tick ensures consistent, deterministic
/// destination choice without consuming iterations from the shared simulation RNG stream.</summary>
[HarmonyPatch(typeof(WanderUtility), nameof(WanderUtility.GetHerdWanderRoot))]
internal static class HerdWanderRootDeterministicPatch
{
    private static void Prefix(Pawn pawn, out bool __state)
    {
        if (SharedColony.HasSharedWorld && pawn is not null)
        {
            Rand.PushState(Gen.HashCombineInt(pawn.thingIDNumber, Find.TickManager?.TicksGame ?? 0));
            __state = true;
        }
        else
        {
            __state = false;
        }
    }

    private static void Postfix(bool __state)
    {
        if (__state)
        {
            Rand.PopState();
        }
    }
}

[HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.RandomWanderDestFor))]
internal static class RandomWanderDestDeterministicPatch
{
    private static void Prefix(Pawn pawn, out bool __state)
    {
        if (SharedColony.HasSharedWorld && pawn is not null)
        {
            Rand.PushState(Gen.HashCombineInt(pawn.thingIDNumber, Find.TickManager?.TicksGame ?? 0));
            __state = true;
        }
        else
        {
            __state = false;
        }
    }

    private static void Postfix(bool __state)
    {
        if (__state)
        {
            Rand.PopState();
        }
    }
}
