using System.Text.RegularExpressions;

namespace TurboTick.Networking;

/// <summary>Values shared by this standalone protocol, with no game assembly dependencies.</summary>
public static class NetworkProtocol
{
    public const int Version = 1;
    public const string ClientVersion = "0.3.0";
    public const int MaximumFrameLength = 4 * 1024 * 1024;
    public const int SnapshotChunkSize = 256 * 1024;
}

/// <summary>Server-authoritative simulation rate settings. Revision zero is the initial policy.
/// Rate sync is on by default; joining a room applies the server value to everyone.</summary>
public sealed record SpeedPolicy(bool Enabled = true, float Multiplier = 2f, float MaxRate = 60f, long Revision = 0)
{
    private float _multiplier = ValidateMultiplier(Multiplier);
    private float _maxRate = ValidateMaxRate(MaxRate);
    private long _revision = ValidateRevision(Revision);

    public float Multiplier { get => _multiplier; init => _multiplier = ValidateMultiplier(value); }
    public float MaxRate { get => _maxRate; init => _maxRate = ValidateMaxRate(value); }
    public long Revision { get => _revision; init => _revision = ValidateRevision(value); }

    private static float ValidateMultiplier(float value) => float.IsFinite(value) && value is >= 1f and <= 20f
        ? value : throw new ArgumentOutOfRangeException(nameof(Multiplier), "Multiplier must be finite and between 1 and 20.");
    private static float ValidateMaxRate(float value) => float.IsFinite(value) && value is >= 1f and <= 240f
        ? value : throw new ArgumentOutOfRangeException(nameof(MaxRate), "MaxRate must be finite and between 1 and 240.");
    private static long ValidateRevision(long value) => value >= 0
        ? value : throw new ArgumentOutOfRangeException(nameof(Revision), "Revision must be non-negative.");
}

public sealed record ModFingerprint(string PackageId, string ContentHash);
public sealed record ClientHello(string PlayerName, string GameVersion, string ModHash, string ClientVersion = "0.3.0");

/// <summary>Session identity published at start; the seed aligns random streams across peers.</summary>
public sealed record SessionInfo(long Epoch, int Tick, int Seed, string WorldId, int SnapshotTick = 0);
public sealed record ServerOptions(string Address = "127.0.0.1", int Port = 30572, string DataDirectory = "Saved",
    string GameVersion = "1.7", string ModHash = "development", bool EveryoneCanEdit = true);
public sealed record SnapshotManifest(string Id, long Length, int ChunkSize, IReadOnlyList<string> ChunkHashes);
public sealed record SnapshotChunk(string Id, int Index, byte[] Data, string Sha256);

/// <summary>One deterministic gameplay command. Sequence and tick are assigned by the server.</summary>
public sealed record GameCommand(long Sequence, int Tick, string Kind, byte[] Payload);

/// <summary>Builds the same manifest the server would compute, so a client can verify before upload.</summary>
public static class SnapshotBuilder
{
    public static SnapshotManifest Describe(ReadOnlySpan<byte> data)
    {
        var hashes = new List<string>();
        using var whole = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        whole.AppendData(data);
        for (var offset = 0; offset < data.Length; offset += NetworkProtocol.SnapshotChunkSize)
        {
            var length = Math.Min(NetworkProtocol.SnapshotChunkSize, data.Length - offset);
            hashes.Add(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data.Slice(offset, length))));
        }
        return new SnapshotManifest(Convert.ToHexStringLower(whole.GetHashAndReset()), data.Length,
            NetworkProtocol.SnapshotChunkSize, hashes);
    }
}

/// <summary>A structured server or transport error suitable for displaying to a player.</summary>
public sealed class NetworkException : IOException
{
    public string Code { get; }
    public NetworkException(string code, string message) : base(message) => Code = code;
    public NetworkException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;
}

internal static partial class Validation
{
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdPattern();

    internal static void ContentId(string id)
    {
        if (id is null || !ContentIdPattern().IsMatch(id))
            throw new ArgumentException("Snapshot id must be exactly 64 lowercase hexadecimal characters.", nameof(id));
    }

    internal static bool IsFullGameVersion(string? version) => version is not null &&
        System.Version.TryParse(version, out var parsed) && parsed.Major == 1 && parsed.Minor == 7 && parsed.Build >= 0;

    internal static void Hello(ClientHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (string.IsNullOrWhiteSpace(hello.PlayerName) || hello.PlayerName.Length > 80 || hello.PlayerName.Any(char.IsControl))
            throw new NetworkException("InvalidPlayerName", "Player name must contain 1 to 80 printable characters.");
        if (!IsFullGameVersion(hello.GameVersion))
            throw new NetworkException("GameVersionMismatch", "A complete RimWorld 1.7.x game version is required.");
        if (string.IsNullOrWhiteSpace(hello.ModHash) || hello.ModHash.Length > 512 || hello.ModHash.Any(char.IsControl))
            throw new NetworkException("InvalidModHash", "A non-empty simulation mod/configuration hash is required.");
        if (!string.Equals(hello.ClientVersion, NetworkProtocol.ClientVersion, StringComparison.Ordinal))
            throw new NetworkException("ClientVersionMismatch", $"Client version must be exactly {NetworkProtocol.ClientVersion}.");
    }

    internal static void Manifest(SnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ContentId(manifest.Id);
        if (manifest.Length < 0 || manifest.ChunkSize != NetworkProtocol.SnapshotChunkSize || manifest.ChunkHashes is null)
            throw new InvalidDataException("Invalid snapshot length, chunk size or chunk hashes.");
        var expected = manifest.Length / manifest.ChunkSize + (manifest.Length % manifest.ChunkSize == 0 ? 0 : 1);
        if (expected != manifest.ChunkHashes.Count)
            throw new InvalidDataException("Snapshot chunk count does not match its length.");
        foreach (var hash in manifest.ChunkHashes)
            ContentId(hash);
    }

    internal static int ChunkLength(SnapshotManifest manifest, int index)
    {
        if (index < 0 || index >= manifest.ChunkHashes.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Snapshot chunk index is out of bounds.");
        return (int)Math.Min(manifest.ChunkSize, manifest.Length - (long)index * manifest.ChunkSize);
    }
}
