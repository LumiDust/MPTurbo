using System.Text.Json;
using Verse;

namespace TurboTick;

/// <summary>Opt-in real-game invariant journal. Independent profiles have independent files, unlike Waylith's
/// shared Player.log. Enable with -turbotick-trace-ticks; ordinary play does no journal I/O.</summary>
internal static class SimulationDiagnostics
{
    private static readonly bool enabled = GenCommandLine.CommandLineArgPassed("turbotick-trace-ticks");
    private static int faults;
    private static int removals;
    private static int detailedThrough = -1;
    internal static bool Enabled => enabled;
    internal static bool RecordingStages => enabled && SharedColony.Gating && SimulationRandom.Active
        && SharedColony.Session.Barrier.AppliedTick < detailedThrough;
    internal readonly record struct Observation(bool Active, int GameTick, int AppliedTick, long Epoch);

    internal static Observation Before(TickManager manager) => enabled && SharedColony.Gating
        ? new(true, manager.TicksGame, SharedColony.Session.Barrier.AppliedTick, SharedColony.Session.Epoch)
        : default;

    internal static void After(TickManager manager, Observation before)
    {
        if (!before.Active || before.Epoch != SharedColony.Session.Epoch) return;
        var gameDelta = manager.TicksGame - before.GameTick;
        var logicalDelta = SharedColony.Session.Barrier.AppliedTick - before.AppliedTick;
        if (gameDelta != logicalDelta)
        {
            if (++faults <= 12) Write(new { kind = "tick-drift", gameDelta, logicalDelta,
                gameTick = manager.TicksGame, logicalTick = SharedColony.Session.Barrier.AppliedTick,
                serverTick = SharedColony.Session.Barrier.ServerTick, epoch = before.Epoch });
        }
        else if (SharedColony.Session.Barrier.AppliedTick % 60 == 0)
            Write(new { kind = "tick-sample", gameDelta, logicalDelta,
                gameTick = manager.TicksGame, logicalTick = SharedColony.Session.Barrier.AppliedTick,
                serverTick = SharedColony.Session.Barrier.ServerTick, epoch = before.Epoch });
        if (SharedColony.Session.Barrier.AppliedTick <= detailedThrough)
            Boundary("early-tick", SharedColony.Session.WorldId, arm: false);
    }

    internal static void Hash(int tick, string hash, IReadOnlyList<string> categories)
    {
        if (!enabled) return;
        Write(new { kind = "hash", logicalTick = tick, gameTick = Find.TickManager?.TicksGame,
            epoch = SharedColony.Session.Epoch, randomState = SimulationRandom.Capture().ToString("x16"), hash, categories });
    }

    internal static void Event(string kind)
    {
        if (!enabled) return;
        Write(new { kind, phase = SharedColony.Session.Phase.ToString(), scribe = Scribe.mode.ToString(),
            gameTick = Find.TickManager?.TicksGame, logicalTick = SharedColony.Session.Barrier.AppliedTick,
            randomState = SimulationRandom.Capture().ToString("x16"), epoch = SharedColony.Session.Epoch });
        if (kind == "world-ready") Boundary("world-ready-baseline", SharedColony.Session.WorldId);
    }

    internal static void Boundary(string kind, string? worldId, bool arm = true)
    {
        if (!enabled) return;
        if (arm) detailedThrough = SharedColony.Session.Barrier.AppliedTick + 450;
        var rows = new List<object>();
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings.OrderBy(t => t.thingIDNumber))
            {
                if (thing.def.category is ThingCategory.Mote or ThingCategory.Ethereal) continue;
                var pawn = thing as Pawn;
                rows.Add(new { id = thing.thingIDNumber, def = thing.def.defName,
                    category = thing.def.category.ToString(), map = map.Index, x = thing.Position.x, z = thing.Position.z,
                    count = thing.stackCount, hp = thing.HitPoints, job = pawn?.CurJobDef?.defName,
                    health = pawn?.health?.summaryHealth?.SummaryHealthPercent });
            }
        }
        var (all, categories) = SharedColony.DescribeColony();
        Write(new { kind, worldId, epoch = SharedColony.Session.Epoch,
            logicalTick = SharedColony.Session.Barrier.AppliedTick, gameTick = Find.TickManager?.TicksGame,
            randomState = SimulationRandom.Capture().ToString("x16"),
            hash = TurboTick.Simulation.StateHash.Compute(all), categories, entities = rows });
    }

    internal static void PathWarmup(int pending, int ready, int cancelled)
    {
        if (!enabled) return;
        Write(new { kind = "path-warmup", pending, ready, cancelled, epoch = SharedColony.Session.Epoch,
            logicalTick = SharedColony.Session.Barrier.AppliedTick, gameTick = Find.TickManager?.TicksGame,
            randomState = SimulationRandom.Capture().ToString("x16") });
    }

    internal static void Stage(string stage, int mapId, object? extra = null)
    {
        if (!RecordingStages) return;
        Write(new { kind = "simulation-stage", stage, mapId, epoch = SharedColony.Session.Epoch,
            logicalTick = SharedColony.Session.Barrier.AppliedTick + 1, gameTick = Find.TickManager?.TicksGame,
            randomState = SimulationRandom.Capture().ToString("x16"), extra });
    }

    internal static void Removal(Thing thing)
    {
        if (!enabled || !SharedColony.LoadPending || WorldLoadScope.CurrentSave is null
            || thing.def.defName != "Filth_RubbleRock" || removals++ >= 8) return;
        var callers = new System.Diagnostics.StackTrace(1, false).GetFrames().Take(12)
            .Select(frame => frame.GetMethod()).Where(method => method is not null)
            .Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray();
        Write(new { kind = "load-filth-removal", id = thing.thingIDNumber,
            x = thing.Position.x, z = thing.Position.z, source = WorldLoadScope.CurrentSave, callers });
    }

    private static void Write(object row)
    {
        try
        {
            var directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTick");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "simulation-trace.jsonl"),
                JsonSerializer.Serialize(new { utc = DateTime.UtcNow, pid = Environment.ProcessId, data = row }) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log.Warning("[TurboTick] Simulation trace: " + error.Message); }
    }
}
