using Verse;

namespace TurboTick;

/// <summary>Vanilla load recreates moving pawns' requests but not their current paths. Its first
/// PathFinderTick only schedules those jobs, and PatherTick returns without paying movement cost.
/// Finish that load-time pipeline before releasing a shared snapshot; do not simulate a pawn tick.</summary>
internal static class SharedPathWarmup
{
    internal static void CompleteLoadedRequests(IEnumerable<Map> maps)
    {
        var mapList = maps.ToArray();
        var pending = mapList.SelectMany(map => map.mapPawns.AllPawnsSpawned)
            .Select(pawn => pawn.pather?.curPathRequest)
            .Where(request => request is { ResultIsReady: false, Cancelled: false }).ToArray();
        if (pending.Length == 0) { SimulationDiagnostics.PathWarmup(0, 0, 0); return; }
        var gameTick = Find.TickManager.TicksGame;
        using var random = SimulationRandom.Enter();
        var state = SimulationRandom.Capture();
        foreach (var map in mapList)
        {
            // First pass schedules the restored queue; the second completes native jobs and resolves
            // their results. Both use the same saved tick. No TryGetPath/claim occurs outside PatherTick.
            map.pathFinder.PathFinderTick();
            map.pathFinder.PathFinderTick();
        }
        var incomplete = pending.Count(request => request is { ResultIsReady: false, Cancelled: false });
        if (Find.TickManager.TicksGame != gameTick || SimulationRandom.Capture() != state)
            throw new InvalidDataException("Loading path requests changed shared simulation time or RNG.");
        if (incomplete != 0)
            throw new InvalidDataException($"Loading left {incomplete} restored paths unresolved.");
        SimulationDiagnostics.PathWarmup(pending.Length,
            pending.Count(request => request?.ResultIsReady == true),
            pending.Count(request => request?.Cancelled == true));
    }
}
