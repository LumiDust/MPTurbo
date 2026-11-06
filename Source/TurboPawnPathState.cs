using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>Serializes an exact snapshot of moving and idle pather state during shared saves.
/// Vanilla RimWorld discards curPath on save, forcing every moving pawn to re-path asynchronously on load.
/// That causes a 1-tick delay, resetting movement cost and altering cell entry / filth RNG timing.</summary>
public sealed class SavedPatherState : IExposable
{
    public int pawnId;
    public bool moving;
    public LocalTargetInfo destination;
    public PathEndMode peMode;
    public IntVec3 lastPathed;
    public bool hasPath;
    public List<IntVec3> nodes = [];
    public int curNodeIndex;
    public float totalCost;
    public bool usedRegionHeuristics;

    public void ExposeData()
    {
        Scribe_Values.Look(ref pawnId, "pawnId");
        Scribe_Values.Look(ref moving, "moving");
        Scribe_TargetInfo.Look(ref destination, "dest");
        Scribe_Values.Look(ref peMode, "peMode");
        Scribe_Values.Look(ref lastPathed, "lastPathed");
        Scribe_Values.Look(ref hasPath, "hasPath");
        if (hasPath)
        {
            Scribe_Collections.Look(ref nodes, "nodes", LookMode.Value);
            Scribe_Values.Look(ref curNodeIndex, "nodeIndex");
            Scribe_Values.Look(ref totalCost, "totalCost");
            Scribe_Values.Look(ref usedRegionHeuristics, "usedRegion");
        }
    }
}

public sealed class TurboPawnPathState : MapComponent
{
    public TurboPawnPathState(Map map) : base(map) { }

    private bool hasState;
    private List<SavedPatherState>? savedList;
    private Dictionary<int, SavedPatherState>? savedLookup;

    private Dictionary<int, SavedPatherState> Lookup =>
        savedLookup ??= (savedList?.GroupBy(s => s.pawnId).ToDictionary(g => g.Key, g => g.First()) ?? []);

    private static class Access
    {
        internal static readonly AccessTools.FieldRef<PawnPath, List<IntVec3>> Nodes =
            AccessTools.FieldRefAccess<PawnPath, List<IntVec3>>("nodes");
        internal static readonly AccessTools.FieldRef<PawnPath, int> CurNodeIndex =
            AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");
        internal static readonly AccessTools.FieldRef<PawnPath, float> TotalCost =
            AccessTools.FieldRefAccess<PawnPath, float>("totalCostInt");
        internal static readonly AccessTools.FieldRef<PawnPath, bool> UsedRegion =
            AccessTools.FieldRefAccess<PawnPath, bool>("usedRegionHeuristics");
        internal static readonly AccessTools.FieldRef<PawnPath, bool> InUse =
            AccessTools.FieldRefAccess<PawnPath, bool>("inUse");
        internal static readonly AccessTools.FieldRef<PawnPath, PawnPathPool> Pool =
            AccessTools.FieldRefAccess<PawnPath, PawnPathPool>("pool");

        internal static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef =
            AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");
        internal static readonly AccessTools.FieldRef<Pawn_PathFollower, bool> Moving =
            AccessTools.FieldRefAccess<Pawn_PathFollower, bool>("moving");
        internal static readonly AccessTools.FieldRef<Pawn_PathFollower, LocalTargetInfo> Dest =
            AccessTools.FieldRefAccess<Pawn_PathFollower, LocalTargetInfo>("destination");
        internal static readonly AccessTools.FieldRef<Pawn_PathFollower, PathEndMode> PeMode =
            AccessTools.FieldRefAccess<Pawn_PathFollower, PathEndMode>("peMode");
    }

    internal static void ValidateBindings()
    {
        _ = Access.Nodes; _ = Access.CurNodeIndex; _ = Access.TotalCost;
        _ = Access.UsedRegion; _ = Access.InUse; _ = Access.Pool;
        _ = Access.PawnRef; _ = Access.Moving; _ = Access.Dest; _ = Access.PeMode;
    }

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            hasState = SharedSnapshotStorage.Writing;
            if (hasState)
            {
                savedList = [];
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.pather is not { } pather) continue;
                    var record = new SavedPatherState
                    {
                        pawnId = pawn.thingIDNumber,
                        moving = Access.Moving(pather),
                        destination = Access.Dest(pather),
                        peMode = Access.PeMode(pather),
                        lastPathed = pather.lastPathedTargetPosition
                    };
                    if (pather.curPath is { Found: true } path && Access.InUse(path))
                    {
                        record.hasPath = true;
                        record.nodes = [.. path.NodesReversed];
                        record.curNodeIndex = Access.CurNodeIndex(path);
                        record.totalCost = Access.TotalCost(path);
                        record.usedRegionHeuristics = Access.UsedRegion(path);
                    }
                    savedList.Add(record);
                }
            }
        }

        Scribe_Values.Look(ref hasState, "turboHasPawnPathState", false);
        if (hasState || Scribe.mode != LoadSaveMode.Saving)
        {
            Scribe_Collections.Look(ref savedList, "turboPathers", LookMode.Deep);
        }
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            savedLookup = null; // Forces re-indexing after all cross-references resolve
        }
    }

    public bool TryRestorePather(Pawn_PathFollower pather, Pawn pawn)
    {
        if (!hasState || !Lookup.TryGetValue(pawn.thingIDNumber, out var saved))
            return false;

        Access.Dest(pather) = saved.destination;
        Access.PeMode(pather) = saved.peMode;
        pather.lastPathedTargetPosition = saved.lastPathed;

        if (saved.hasPath && saved.nodes is { Count: > 0 } && saved.curNodeIndex >= 0 && saved.curNodeIndex < saved.nodes.Count)
        {
            var pool = map.pawnPathPool;
            var path = pool.GetPath();
            var nodes = Access.Nodes(path);
            nodes.Clear();
            nodes.AddRange(saved.nodes);
            Access.CurNodeIndex(path) = saved.curNodeIndex;
            Access.TotalCost(path) = saved.totalCost;
            Access.UsedRegion(path) = saved.usedRegionHeuristics;
            Access.InUse(path) = true;
            Access.Pool(path) = pool;

            pather.curPath = path;
            pather.curPathJobIsStale = false;
            pather.curPathRequest = null;
            return true;
        }

        if (!saved.moving)
        {
            Access.Moving(pather) = false;
            pather.curPath = null;
            pather.curPathRequest = null;
            return true;
        }

        return false;
    }
}

[HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.TryResumePathingAfterLoading))]
internal static class PawnPathResumePatch
{
    private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

    private static bool Prefix(Pawn_PathFollower __instance)
    {
        if (SharedColony.IsExpectedWorldLoad(WorldLoadScope.CurrentSave))
        {
            var pawn = PawnRef(__instance);
            if (pawn?.Map?.GetComponent<TurboPawnPathState>() is { } state)
            {
                if (state.TryRestorePather(__instance, pawn))
                {
                    return false;
                }
            }
        }
        return true;
    }
}
