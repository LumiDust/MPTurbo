using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Noise;

namespace TurboTick;

/// <summary>Native managers omit these history-bearing fields from ordinary saves. A shared checkpoint
/// must resume the same environment scan and lazy-noise state instead of silently starting them over.</summary>
public sealed class TurboEnvironmentState : MapComponent
{
    public TurboEnvironmentState(Map map) : base(map) { }
    private bool hasState;
    private int capturedTick = -1;
    private int width;
    private int height;
    private int steadyCycle;
    private bool hasSnowNoise;
    private int snowSeed;
    private bool snowInvert;
    private bool snowNormalized;
    private List<IntVec3>? gasDirections;

    private static class Adapter
    {
        internal static readonly AccessTools.FieldRef<SteadyEnvironmentEffects, int> Cycle =
            AccessTools.FieldRefAccess<SteadyEnvironmentEffects, int>("cycleIndex");
        internal static readonly AccessTools.FieldRef<SteadyEnvironmentEffects, ModuleBase> Snow =
            AccessTools.FieldRefAccess<SteadyEnvironmentEffects, ModuleBase>("snowNoise");
        internal static readonly AccessTools.FieldRef<GasGrid, List<IntVec3>> Directions =
            AccessTools.FieldRefAccess<GasGrid, List<IntVec3>>("cardinalDirections");
    }

    internal static void ValidateBindings()
    {
        _ = Adapter.Cycle; _ = Adapter.Snow; _ = Adapter.Directions;
    }

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            hasState = SharedSnapshotStorage.Writing;
            if (hasState)
            {
                capturedTick = Find.TickManager.TicksGame;
                width = map.Size.x; height = map.Size.z;
                steadyCycle = Adapter.Cycle(map.steadyEnvironmentEffects);
                var snow = Adapter.Snow(map.steadyEnvironmentEffects);
                hasSnowNoise = snow is not null;
                if (snow is Perlin perlin)
                {
                    // A different mod's custom noise needs an explicit adapter, not a silent approximation.
                    if (perlin.Frequency != (double)0.04f || perlin.Lacunarity != 2d
                        || perlin.Persistence != 0.5d || perlin.OctaveCount != 5 || perlin.Quality != QualityMode.Medium)
                        throw new InvalidDataException("Shared snapshot has unsupported snow noise parameters.");
                    snowSeed = perlin.Seed;
                    snowInvert = perlin.Invert;
                    snowNormalized = perlin.Normalized;
                }
                else if (hasSnowNoise) throw new InvalidDataException("Shared snapshot has unsupported snow noise type.");
                gasDirections = [.. Adapter.Directions(map.gasGrid)];
            }
        }
        Scribe_Values.Look(ref hasState, "turboEnvironmentState", false);
        Scribe_Values.Look(ref capturedTick, "capturedTick", -1);
        Scribe_Values.Look(ref width, "width", 0);
        Scribe_Values.Look(ref height, "height", 0);
        Scribe_Values.Look(ref steadyCycle, "steadyCycle", 0);
        Scribe_Values.Look(ref hasSnowNoise, "hasSnowNoise", false);
        Scribe_Values.Look(ref snowSeed, "snowSeed", 0);
        Scribe_Values.Look(ref snowInvert, "snowInvert", false);
        Scribe_Values.Look(ref snowNormalized, "snowNormalized", false);
        Scribe_Collections.Look(ref gasDirections, "gasDirections", LookMode.Value);
    }

    private void Validate(int gameTick)
    {
        if (!hasState) throw new InvalidDataException("Shared snapshot lacks environment history; publish a new snapshot.");
        if (width != map.Size.x || height != map.Size.z || capturedTick != gameTick)
            throw new InvalidDataException("Shared environment snapshot dimensions or tick do not match.");
        // Area itself is a valid native sentinel; the next scan wraps it to zero before reading a cell.
        var area = checked(width * height);
        if (steadyCycle < 0 || steadyCycle > area)
            throw new InvalidDataException("Shared environment scan cursor is outside the map.");
        if (gasDirections is not { Count: 4 } || gasDirections.Distinct().Count() != 4
            || gasDirections.Any(direction => !GenAdj.CardinalDirections.Contains(direction)))
            throw new InvalidDataException("Shared gas direction permutation is invalid.");
    }

    private void Restore()
    {
        Adapter.Cycle(map.steadyEnvironmentEffects) = steadyCycle;
        Adapter.Snow(map.steadyEnvironmentEffects) = hasSnowNoise
            ? new Perlin((double)0.04f, 2d, 0.5d, 5, snowSeed, QualityMode.Medium)
                { Invert = snowInvert, Normalized = snowNormalized } : null!;
        var directions = Adapter.Directions(map.gasGrid);
        directions.Clear();
        directions.AddRange(gasDirections!);
    }

    internal static void RestoreAll(IEnumerable<Map> maps, int gameTick)
    {
        var components = maps.Select(value => value.GetComponent<TurboEnvironmentState>()).ToArray();
        // Validate the complete snapshot before mutating any manager; load callbacks cannot half-restore.
        foreach (var component in components) component.Validate(gameTick);
        foreach (var component in components) component.Restore();
    }
}
