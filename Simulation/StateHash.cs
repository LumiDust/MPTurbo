using System.Security.Cryptography;
using System.Text;

namespace TurboTick.Simulation;

/// <summary>
/// Canonical hash of the simulated colony. Two peers that ran the same commands from the same snapshot
/// must produce the same value; a mismatch is the only reliable signal that the worlds have diverged.
/// Entries are sorted so iteration order cannot create a false positive.
/// </summary>
public static class StateHash
{
    public static string Compute(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ordered = entries.Where(entry => entry.Length > 0).OrderBy(entry => entry, StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in ordered)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry));
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Canonical description of one pawn's simulated state.</summary>
    public static string Pawn(int id, int map, int x, int z, float health, string job) =>
        $"p:{id}:{map}:{x}:{z}:{health:0.##}:{job}";

    /// <summary>Canonical description of one thing's simulated state.</summary>
    public static string Thing(int id, int map, int x, int z, int stack, int hitPoints) =>
        $"t:{id}:{map}:{x}:{z}:{stack}:{hitPoints}";
}
