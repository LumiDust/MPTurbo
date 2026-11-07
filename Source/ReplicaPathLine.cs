using HarmonyLib;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>A display replica never runs the pather, so its curPath and Destination are whatever the save
/// carried and the destination stays at the default cell. Vanilla then draws a path line from every moving
/// pawn to the origin, which reads as "everybody is walking to the map corner". The line is meaningless
/// without a local path, so it is skipped. Debug path rendering is left alone.</summary>
[HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherDraw))]
internal static class ReplicaPathLineSkip
{
    private static bool Prefix() => !AuthoritativeReplica.Frozen;
}
