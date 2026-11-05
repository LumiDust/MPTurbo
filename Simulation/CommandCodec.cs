using System.Text;
using TurboTick.Networking;

namespace TurboTick.Simulation;

/// <summary>
/// Wire format for player actions. One codec, four choke points in the game, so every action a single
/// player can issue travels the same path: encode → server order → decode and replay at a fixed tick.
/// </summary>
public static class CommandCodec
{
    public const string Designation = "designation";
    public const string Blueprint = "blueprint";
    public const string Draft = "draft";
    public const string Order = "order";

    public static GameCommand DesignationCommand(string defName, int map, int x, int z, long sequence) =>
        new(sequence, 0, Designation, Encoding.UTF8.GetBytes($"{defName}|{map}|{x}|{z}"));

    public static GameCommand BlueprintCommand(string buildable, string stuff, int map, int x, int z, int rot, long sequence) =>
        new(sequence, 0, Blueprint, Encoding.UTF8.GetBytes($"{buildable}|{stuff}|{map}|{x}|{z}|{rot}"));

    public static GameCommand DraftCommand(int pawnId, bool drafted, long sequence) =>
        new(sequence, 0, Draft, Encoding.UTF8.GetBytes($"{pawnId}|{(drafted ? 1 : 0)}"));

    public static GameCommand OrderCommand(int pawnId, string jobDef, int targetACell, int targetAThing, int targetBCell, int targetBThing, int count, long sequence) =>
        new(sequence, 0, Order, Encoding.UTF8.GetBytes($"{pawnId}|{jobDef}|{targetACell}|{targetAThing}|{targetBCell}|{targetBThing}|{count}"));

    public static bool TryDecodeDesignation(byte[] payload, out string defName, out int map, out int x, out int z)
    {
        defName = "";
        map = x = z = 0;
        if (!TryDecode(payload, 4, out var p)) return false;
        defName = p[0];
        return defName.Length > 0 && int.TryParse(p[1], out map) && int.TryParse(p[2], out x) && int.TryParse(p[3], out z);
    }

    public static bool TryDecodeBlueprint(byte[] payload, out string buildable, out string stuff, out int map, out int x, out int z, out int rot)
    {
        buildable = stuff = "";
        map = x = z = rot = 0;
        if (!TryDecode(payload, 6, out var p)) return false;
        buildable = p[0];
        stuff = p[1];
        return buildable.Length > 0 && int.TryParse(p[2], out map) && int.TryParse(p[3], out x)
            && int.TryParse(p[4], out z) && int.TryParse(p[5], out rot);
    }

    public static bool TryDecodeDraft(byte[] payload, out int pawnId, out bool drafted)
    {
        pawnId = 0;
        drafted = false;
        if (!TryDecode(payload, 2, out var p) || !int.TryParse(p[0], out pawnId) || !int.TryParse(p[1], out var flag)) return false;
        if (flag is not (0 or 1)) return false;
        drafted = flag == 1;
        return true;
    }

    public static bool TryDecodeOrder(byte[] payload, out int pawnId, out string jobDef, out int aCell, out int aThing, out int bCell, out int bThing, out int count)
    {
        pawnId = aCell = aThing = bCell = bThing = count = 0;
        jobDef = "";
        if (!TryDecode(payload, 7, out var p) || !int.TryParse(p[0], out pawnId)) return false;
        jobDef = p[1];
        return jobDef.Length > 0 && int.TryParse(p[2], out aCell) && int.TryParse(p[3], out aThing)
            && int.TryParse(p[4], out bCell) && int.TryParse(p[5], out bThing) && int.TryParse(p[6], out count);
    }

    private static bool TryDecode(byte[] payload, int fields, out string[] parts)
    {
        parts = [];
        if (payload is null || payload.Length == 0 || payload.Length > 4096) return false;
        var text = Encoding.UTF8.GetString(payload);
        parts = text.Split('|');
        return parts.Length == fields;
    }
}
