using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Shared-colony action layer. Every player-issued action passes through a small number of choke points,
/// so we capture there instead of patching each gadget:
///   DesignationManager.AddDesignation        - mine / deconstruct / hunt / harvest / cut / tame
///   GenConstruct.PlaceBlueprintForBuild      - building and furniture orders
///   Pawn_DraftController.set_Drafted         - draft toggle
///   Pawn_JobTracker.TryTakeOrderedJob        - right-click move / haul / attack / interact orders
/// Captured actions are encoded, ordered by the server, and replayed on every peer at the same tick.
/// </summary>
public static class SharedActions
{
    internal static bool Applying;

    public static void ResetTickWork()
    {
        Applying = false;
    }

    // ---------- capture ----------

    public static void CaptureDesignation(Designation designation)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return;
        if (!designation.target.IsValid || designation.designationManager?.map is not { } map) return;
        Publish(CommandCodec.DesignationCommand(designation.def.defName, map.Index,
            designation.target.Cell.x, designation.target.Cell.z, SharedColony.NextSequence()));
    }

    public static void CaptureBlueprint(BuildableDef buildable, ThingDef stuff, IntVec3 cell, Map map, Rot4 rot)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || buildable == null || map == null) return;
        Publish(CommandCodec.BlueprintCommand(buildable.defName, stuff?.defName ?? "",
            map.Index, cell.x, cell.z, rot.AsInt, SharedColony.NextSequence()));
    }

    public static void CaptureDraft(Pawn pawn, bool drafted)
    {
        if (Applying || !SharedColony.Session.AcceptsInput || pawn == null) return;
        Publish(CommandCodec.DraftCommand(pawn.thingIDNumber, drafted, SharedColony.NextSequence()));
    }

    public static void CaptureOrder(Pawn pawn, bool result)
    {
        if (!result || Applying || !SharedColony.Session.AcceptsInput || pawn?.jobs?.curJob is not { } job) return;
        if (job.def == null) return;
        var a = job.targetA;
        var b = job.targetB;
        Publish(CommandCodec.OrderCommand(pawn.thingIDNumber, job.def.defName,
            a.IsValid ? a.Cell.x * 1000 + a.Cell.z : -1, a.HasThing ? a.Thing.thingIDNumber : -1,
            b.IsValid ? b.Cell.x * 1000 + b.Cell.z : -1, b.HasThing ? b.Thing.thingIDNumber : -1,
            job.count, SharedColony.NextSequence()));
    }

    /// <summary>
    /// Transport loading. Experimental: the apply path rebuilds a TransferableOneWay and reuses the same
    /// method the UI calls, but it has not been exercised in game yet, so it stays behind a switch.
    /// </summary>
    public static void CaptureTransport(CompTransporter transporter, TransferableOneWay transferable, int count)
    {
        if (Applying || !SharedColony.Session.AcceptsInput) return;
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return;
        if (transporter?.parent is not { } pod || transferable?.AnyThing is not { } thing || count <= 0) return;
        Publish(CommandCodec.TransportCommand(pod.thingIDNumber, thing.thingIDNumber, count, SharedColony.NextSequence()));
    }

    private static void ApplyTransport(byte[] payload)
    {
        if (TurboTickMod.Settings is { experimentalInteractions: false }) return;
        if (!CommandCodec.TryDecodeTransport(payload, out var transporterId, out var thingId, out var count)) return;
        var pod = Find.Maps.SelectMany(map => map.listerThings.AllThings)
            .FirstOrDefault(thing => thing.thingIDNumber == transporterId);
        var transporter = pod?.TryGetComp<CompTransporter>();
        var cargo = Find.Maps.SelectMany(map => map.listerThings.AllThings)
            .FirstOrDefault(thing => thing.thingIDNumber == thingId);
        if (transporter == null || cargo == null) return;
        var transferable = new TransferableOneWay();
        transferable.things.Add(cargo);
        transporter.AddToTheToLoadList(transferable, count);
    }

    private static void Publish(GameCommand command) => SharedColony.Send(command);

    // ---------- apply ----------

    public static void Apply(GameCommand command)
    {
        Applying = true;
        try
        {
            switch (command.Kind)
            {
                case CommandCodec.Designation:
                    ApplyDesignation(command.Payload);
                    break;
                case CommandCodec.Blueprint:
                    ApplyBlueprint(command.Payload);
                    break;
                case CommandCodec.Draft:
                    ApplyDraft(command.Payload);
                    break;
                case CommandCodec.Order:
                    ApplyOrder(command.Payload);
                    break;
                case CommandCodec.Transport:
                    ApplyTransport(command.Payload);
                    break;
                default:
                    Log.Warning("[TurboTick] 未知共享命令：" + command.Kind);
                    break;
            }
        }
        finally { Applying = false; }
    }

    private static void ApplyDesignation(byte[] payload)
    {
        if (!CommandCodec.TryDecodeDesignation(payload, out var defName, out var mapIndex, out var x, out var z)) return;
        var def = DefDatabase<DesignationDef>.GetNamedSilentFail(defName);
        var map = Find.Maps.FirstOrDefault(m => m.Index == mapIndex);
        if (def == null || map == null) return;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map) || map.designationManager.DesignationAt(cell, def) != null) return;
        map.designationManager.AddDesignation(new Designation(new LocalTargetInfo(cell), def));
    }

    private static void ApplyBlueprint(byte[] payload)
    {
        if (!CommandCodec.TryDecodeBlueprint(payload, out var buildableName, out var stuffName, out var mapIndex, out var x, out var z, out var rot)) return;
        var buildable = DefDatabase<ThingDef>.GetNamedSilentFail(buildableName);
        var stuff = string.IsNullOrEmpty(stuffName) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuffName);
        var map = Find.Maps.FirstOrDefault(m => m.Index == mapIndex);
        if (buildable == null || map == null) return;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return;
        GenConstruct.PlaceBlueprintForBuild(buildable, cell, map, new Rot4(rot), Faction.OfPlayer, stuff);
    }

    private static void ApplyDraft(byte[] payload)
    {
        if (!CommandCodec.TryDecodeDraft(payload, out var pawnId, out var drafted)) return;
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (pawn?.drafter == null || pawn.drafter.Drafted == drafted) return;
        pawn.drafter.Drafted = drafted;
    }

    private static void ApplyOrder(byte[] payload)
    {
        if (!CommandCodec.TryDecodeOrder(payload, out var pawnId, out var jobName, out var aCell, out var aThing, out var bCell, out var bThing, out var count)) return;
        var def = DefDatabase<JobDef>.GetNamedSilentFail(jobName);
        var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == pawnId);
        if (def == null || pawn?.jobs == null || pawn.Dead) return;
        var a = ResolveTarget(aCell, aThing);
        var b = ResolveTarget(bCell, bThing);
        if (!a.IsValid) return;
        var job = b.IsValid ? JobMaker.MakeJob(def, a, b) : JobMaker.MakeJob(def, a);
        if (count > 0) job.count = count;
        pawn.jobs.TryTakeOrderedJob(job);
    }

    private static LocalTargetInfo ResolveTarget(int packedCell, int thingId)
    {
        if (thingId > 0)
        {
            var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(t => t.thingIDNumber == thingId);
            return thing != null ? new LocalTargetInfo(thing) : LocalTargetInfo.Invalid;
        }
        if (packedCell < 0) return LocalTargetInfo.Invalid;
        var x = packedCell / 1000;
        var z = packedCell % 1000;
        var map = Find.CurrentMap;
        return map != null && new IntVec3(x, 0, z).InBounds(map) ? new LocalTargetInfo(new IntVec3(x, 0, z)) : LocalTargetInfo.Invalid;
    }
}

[HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
internal static class DesignationCapturePatch
{
    private static void Postfix(Designation newDesignation) => SharedActions.CaptureDesignation(newDesignation);
}

[HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild))]
internal static class BlueprintCapturePatch
{
    private static void Postfix(BuildableDef buildable, IntVec3 loc, Map map, Rot4 rotation, ThingDef stuff) =>
        SharedActions.CaptureBlueprint(buildable, stuff, loc, map, rotation);
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
internal static class DraftCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_DraftController, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_DraftController, Pawn>("pawn");

    private static void Postfix(Pawn_DraftController __instance, bool value) =>
        SharedActions.CaptureDraft(PawnRef(__instance), value);
}

[HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.AddToTheToLoadList))]
internal static class TransportCapturePatch
{
    private static void Postfix(CompTransporter __instance, TransferableOneWay transferable, int count) =>
        SharedActions.CaptureTransport(__instance, transferable, count);
}

[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
internal static class OrderCapturePatch
{
    private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef =
        AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

    private static void Postfix(Pawn_JobTracker __instance, bool __result)
    {
        if (__result) SharedActions.CaptureOrder(PawnRef(__instance), true);
    }
}
