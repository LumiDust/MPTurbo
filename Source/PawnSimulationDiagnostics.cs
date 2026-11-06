using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Opt-in snapshots of the first pawn/path/job stages after a checkpoint. All getters are
/// observational; this never claims/resolves a PathRequest or advances a PawnPath cursor.</summary>
internal static class PawnSimulationDiagnostics
{
    private static readonly AccessTools.FieldRef<PathRequest, int> Created =
        AccessTools.FieldRefAccess<PathRequest, int>("tickCreated");
    private static readonly AccessTools.FieldRef<PathRequest, int> Deadline =
        AccessTools.FieldRefAccess<PathRequest, int>("tickDeadline");
    private static readonly AccessTools.FieldRef<Thing, int> TickDelta =
        AccessTools.FieldRefAccess<Thing, int>("tickDelta");

    internal static object Describe(Pawn pawn)
    {
        var pather = pawn.pather;
        var path = pather?.curPath;
        var request = pather?.curPathRequest;
        var driver = pawn.jobs?.curDriver;
        string? pathHash = path is null ? null : Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join(";", path.NodesReversed.Select(cell => $"{cell.x},{cell.z}")))));
        return new { id = pawn.thingIDNumber, x = pawn.Position.x, z = pawn.Position.z,
            job = pawn.CurJobDef?.defName, targetA = pawn.CurJob?.targetA.ToString(),
            toil = driver?.CurToilIndex, toilTicks = driver?.ticksLeftThisToil,
            tickDelta = TickDelta(pawn), moving = pather?.Moving, destination = pather?.Destination.ToString(),
            next = pather is null ? null : $"{pather.nextCell.x},{pather.nextCell.z}",
            costLeft = pather?.nextCellCostLeft, costTotal = pather?.nextCellCostTotal,
            stale = pather?.curPathJobIsStale, lastMoved = pather?.LastMovedTick,
            pathNodes = path?.NodesLeftCount, pathUsed = path?.NodesConsumedCount, pathHash,
            request = request is null ? null : new { created = Created(request), start = request.TickStart,
                deadline = Deadline(request), ready = request.ResultIsReady, cancelled = request.Cancelled,
                found = request.Found, from = request.Start.ToString(), destination = request.Target.ToString() } };
    }

    internal static void Record(string stage, Pawn pawn)
    {
        if (SimulationDiagnostics.RecordingStages)
            SimulationDiagnostics.Stage(stage, pawn.Map?.uniqueID ?? -1, Describe(pawn));
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class PawnTickTracePatch
{
    private static void Prefix(Thing __instance)
    {
        if (__instance is Pawn pawn) PawnSimulationDiagnostics.Record("PawnTick:enter", pawn);
    }
    private static void Postfix(Thing __instance)
    {
        if (__instance is Pawn pawn) PawnSimulationDiagnostics.Record("PawnTick:leave", pawn);
    }
}

[HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
internal static class PawnPathTracePatch
{
    private static void Prefix(Pawn ___pawn) => PawnSimulationDiagnostics.Record("PatherTick:enter", ___pawn);
    private static void Postfix(Pawn ___pawn) => PawnSimulationDiagnostics.Record("PatherTick:leave", ___pawn);
}

[HarmonyPatch]
internal static class PawnJobTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick));
        yield return AccessTools.Method(typeof(JobDriver), nameof(JobDriver.DriverTick));
        yield return AccessTools.Method(typeof(JobDriver), nameof(JobDriver.DriverTickInterval));
    }
    private static void Prefix(Pawn ___pawn, MethodBase __originalMethod) =>
        PawnSimulationDiagnostics.Record(__originalMethod.Name + ":enter", ___pawn);
    private static void Postfix(Pawn ___pawn, MethodBase __originalMethod) =>
        PawnSimulationDiagnostics.Record(__originalMethod.Name + ":leave", ___pawn);
}

[HarmonyPatch(typeof(Rand), "get_Value")]
internal static class RandValueTracePatch
{
    private static void Prefix()
    {
        if (SimulationDiagnostics.RecordingStages)
        {
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
            var path = frames is not null
                ? string.Join("->", frames.Take(4).Select(f => $"{f.GetMethod()?.DeclaringType?.Name}.{f.GetMethod()?.Name}"))
                : "unknown";
            SimulationDiagnostics.Stage("Rand:Value:" + path, -1);
        }
    }
}

[HarmonyPatch(typeof(Rand), "get_Int")]
internal static class RandIntTracePatch
{
    private static void Prefix()
    {
        if (SimulationDiagnostics.RecordingStages)
        {
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
            var path = frames is not null
                ? string.Join("->", frames.Take(4).Select(f => $"{f.GetMethod()?.DeclaringType?.Name}.{f.GetMethod()?.Name}"))
                : "unknown";
            SimulationDiagnostics.Stage("Rand:Int:" + path, -1);
        }
    }
}
