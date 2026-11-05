using System.Text;
using HarmonyLib;
using TurboTick.Networking;
using TurboTick.Simulation;
using Verse;

namespace TurboTick;

/// <summary>
/// Shared-colony gameplay: every designation (mine, deconstruct, hunt, harvest, cut, tame, …) made by
/// one player is relayed through the server and applied on the others. The world snapshot is published
/// by the controller and verified before play so all peers can confirm they run the same colony.
///
/// Limits of this milestone: only designations are replicated. Pawn orders, building blueprints and
/// inventory transfer are not yet synchronized, and tick pacing is advisory rather than a strict lockstep
/// barrier. Anything unverified is reported in the HUD instead of being assumed to work.
/// </summary>
public static class SharedColony
{
    public static SharedSession Session { get; } = new();
    private static bool applying;
    private static long nextLocalSequence;

    public static void Reset()
    {
        Session.Leave();
        applying = false;
    }

    public static void OnWorldStarted(long epoch, int tick, SnapshotManifest manifest)
    {
        Session.BeginLoading(epoch, manifest.Id);
        Session.Barrier.Publish(tick);
        Session.WorldLoaded();
        Session.Playing();
        Log.Message($"[TurboTick] 共享殖民地已开始：epoch={epoch} world={manifest.Id}");
    }

    public static void OnTick(int tick) => Session.Barrier.Publish(tick);

    public static void OnCommands(IReadOnlyList<GameCommand> commands)
    {
        foreach (var command in commands) Session.Commands.Accept(command);
    }

    /// <summary>Called from the game thread each frame; applies due designations in server order.</summary>
    public static void Update()
    {
        if (!Session.IsActive) return;
        var due = Session.Commands.TakeThrough(Session.Barrier.ServerTick);
        if (due.Count == 0) return;
        applying = true;
        try
        {
            foreach (var command in due) Apply(command);
        }
        finally { applying = false; }
    }

    /// <summary>Relays a locally created designation. Skips replay and non-playing sessions.</summary>
    internal static void Capture(Designation designation)
    {
        if (applying || !Session.AcceptsInput) return;
        if (!designation.target.IsValid || designation.designationManager?.map is not { } map) return;
        var payload = $"{designation.def.defName}|{map.Index}|{designation.target.Cell.x}|{designation.target.Cell.z}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var sequence = ++nextLocalSequence;
        _ = RoomConnection.SendCommand("designation", bytes, sequence);
    }

    private static void Apply(GameCommand command)
    {
        if (command.Kind != "designation") { Log.Warning("[TurboTick] 未知共享命令：" + command.Kind); return; }
        var text = Encoding.UTF8.GetString(command.Payload);
        var parts = text.Split('|');
        if (parts.Length != 4
            || !int.TryParse(parts[1], out var mapIndex) || !int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var z))
        { Log.Warning("[TurboTick] 共享命令格式错误：" + text); return; }
        var def = DefDatabase<DesignationDef>.GetNamedSilentFail(parts[0]);
        var map = Find.Maps.FirstOrDefault(candidate => candidate.Index == mapIndex);
        if (def == null || map == null) return;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return;
        var designation = new Designation(new LocalTargetInfo(cell), def);
        if (map.designationManager.DesignationAt(cell, def) == null)
            map.designationManager.AddDesignation(designation);
    }
}

[HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
internal static class DesignationCapturePatch
{
    private static void Postfix(Designation newDesignation) => SharedColony.Capture(newDesignation);
}
