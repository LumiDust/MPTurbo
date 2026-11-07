using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace TurboTick.Networking;

internal sealed record SpeedRequest(bool Enabled, float Multiplier, float MaxRate);

// One envelope keeps framing, request correlation, and error handling private to the networking module.
internal sealed record WireMessage
{
    public string Type { get; init; } = "";
    public int ProtocolVersion { get; init; } = NetworkProtocol.Version;
    public long RequestId { get; init; }
    public ClientHello? Hello { get; init; }
    public AdminChallenge? AdminChallenge { get; init; }
    public AdminProof? AdminProof { get; init; }
    public ServerAdminStatus? AdminStatus { get; init; }
    public ServerResetResult? AdminResetResult { get; init; }
    public long PlayerId { get; init; }
    public IReadOnlyList<PlayerInfo>? Players { get; init; }
    public PresenceUpdate? Presence { get; init; }
    public PlayerPresence? PlayerPresence { get; init; }
    public SpeedPolicy? Policy { get; init; }
    public bool? CanEdit { get; init; }
    public bool? IsHost { get; init; }
    public string? GameVersion { get; init; }
    public string? ModHash { get; init; }
    public string? ClientVersion { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public bool Fatal { get; init; }
    public SpeedRequest? Speed { get; init; }
    public int? TimeSpeed { get; init; }
    public SnapshotManifest? Manifest { get; init; }
    public SnapshotChunk? Chunk { get; init; }
    public string? SnapshotId { get; init; }
    public int ChunkIndex { get; init; }

    // Shared-colony session fields (additive; older peers never send or require them).
    public long Epoch { get; init; } = -1;
    public int Tick { get; init; }
    public int Seed { get; init; }
    public int SnapshotTick { get; init; }
    public bool Checkpoint { get; init; }
    public bool? CheckpointOwner { get; init; }
    public string[]? Categories { get; init; }
    public string? WorldId { get; init; }
    public string? UploadId { get; init; }
    public int ChunkCount { get; init; }
    public byte[]? WorldChunk { get; init; }
    public string? Sha256 { get; init; }
    public GameCommand? Command { get; init; }
    public List<GameCommand>? Commands { get; init; }
    public AuthoritativeFrame? Frame { get; init; }
    /// <summary>Present on Started: joining peers are display replicas, not simulators.</summary>
    public bool Authoritative { get; init; }
    /// <summary>Present on Started: the room itself runs one authoritative simulator, so replicas apply
    /// frames and no peer performs cross-peer hash comparison.</summary>
    public bool RoomAuthoritative { get; init; }

    /// <summary>Every peer simulates and the server only orders commands and paces ticks.</summary>
    public bool Lockstep { get; init; }

    internal static WireMessage Error(string code, string message, long requestId = 0, bool fatal = false) =>
        new() { Type = "Error", Code = code, Message = message, RequestId = requestId, Fatal = fatal };
}

internal static class JsonWire
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class FramedConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _closed;
    private long _lastReceived = Environment.TickCount64;
    internal long LastReceived => Interlocked.Read(ref _lastReceived);

    internal FramedConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    internal async Task<WireMessage?> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        var read = await _stream.ReadAsync(header, ct).ConfigureAwait(false);
        if (read == 0) return null;
        await _stream.ReadExactlyAsync(header.AsMemory(read), ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > NetworkProtocol.MaximumFrameLength)
            throw new NetworkException("InvalidFrameLength", $"Frame length must be between 1 and {NetworkProtocol.MaximumFrameLength} bytes.");
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        await _stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        WireMessage message;
        try
        {
            message = JsonSerializer.Deserialize<WireMessage>(bytes, JsonWire.Options)
                ?? throw new JsonException("Empty JSON message.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new NetworkException("InvalidMessage", "The received JSON message is invalid.", ex);
        }
        if (string.IsNullOrWhiteSpace(message.Type) || message.Type.Length > 64 || message.RequestId < 0)
            throw new NetworkException("InvalidMessage", "The message type or request id is invalid.");
        Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
        return message;
    }

    internal async Task WriteAsync(WireMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonWire.Options);
        if (bytes.Length is < 1 or > NetworkProtocol.MaximumFrameLength)
            throw new NetworkException("FrameTooLarge", "The outgoing frame exceeds the protocol limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _writeGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await _stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await _stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // A canceled/partial write cannot be retried on the same byte stream.
            Dispose();
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _client.Dispose();
        // The semaphore has no unmanaged resources; in-flight writers may still release it.
    }
}

internal static class AtomicFile
{
    internal static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonWire.Options, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonWire.Options, ct).ConfigureAwait(false);
    }
}
