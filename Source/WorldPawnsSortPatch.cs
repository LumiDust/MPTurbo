using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace TurboTick;

/// <summary>WorldPawns.WorldPawnsTick populates tmpPawnsToTick from pawnsAlive (a HashSet of pawns).
/// HashSet enumeration order is non-deterministic across processes. Sorting tmpPawnsToTick by
/// thingIDNumber ensures every peer updates world pawns in canonical total order.</summary>
[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.WorldPawnsTick))]
internal static class WorldPawnsDeterministicTickPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var addRange = AccessTools.Method(typeof(List<Pawn>), nameof(List<Pawn>.AddRange));
        var sortMethod = AccessTools.Method(typeof(WorldPawnsDeterministicTickPatch), nameof(SortTmpPawns));
        var field = AccessTools.Field(typeof(WorldPawns), "tmpPawnsToTick");

        foreach (var instr in instructions)
        {
            yield return instr;
            if (instr.Calls(addRange))
            {
                yield return new CodeInstruction(OpCodes.Ldsfld, field);
                yield return new CodeInstruction(OpCodes.Call, sortMethod);
            }
        }
    }

    private static void SortTmpPawns(List<Pawn> pawns)
    {
        if (SharedColony.HasSharedWorld)
        {
            pawns.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
        }
    }
}
