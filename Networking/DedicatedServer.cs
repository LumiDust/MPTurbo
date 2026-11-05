using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace TurboTick.Networking;

/// <summary>A standalone authority for compatibility, roles, rate policies, and snapshot downloads.</summary>
public sealed class DedicatedServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly string _statePath;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Peer> _peers = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _run;
    private CancellationTokenRegistration _shutdownRegistration;
    private Task _acceptTask = Task.CompletedTask;
    private SpeedPolicy _policy = new();
    private string? _lockedGameVersion;
    private string? _lockedModHash;
    private long? _controllerId;
    private long _nextPeer;
    private int _port;
    private int _disposed;

    private sealed record SavedState(SpeedPolicy Policy, string? GameVersion, string? ModHash);

    public int Port => Volatile.Read(ref _port);
    public SpeedPolicy CurrentPolicy => Volatile.Read(ref _policy);
    public string? LockedGameVersion => Volatile.Read(ref _lockedGameVersion);
    public string? LockedModHash => Volatile.Read(ref _lockedModHash);
    public SnapshotStore Snapshots { get; }

    public DedicatedServer(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Address);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModHash);
        if (options.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 0 and 65535.");
        if (options.GameVersion != "1.7" && !Validation.IsFullGameVersion(options.GameVersion))
            throw new ArgumentException("GameVersion must be 1.7 or a complete 1.7.x version.", nameof(options));
        if (options.ModHash.Length > 512 || options.ModHash.Any(char.IsControl))
            throw new ArgumentException("ModHash must contain at most 512 printable characters.", nameof(options));
        _options = options with { DataDirectory = Path.GetFullPath(options.DataDirectory) };
        Directory.CreateDirectory(_options.DataDirectory);
        _statePath = Path.Combine(_options.DataDirectory, "server-state.json");
        Snapshots = new SnapshotStore(Path.Combine(_options.DataDirectory, "Snapshots"));
        _port = options.Port;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_run is not null) throw new InvalidOperationException("The server is already started.");
            await LoadStateAsync(ct).ConfigureAwait(false);
            var addresses = IPAddress.TryParse(_options.Address, out var address)
                ? new[] { address } : await Dns.GetHostAddressesAsync(_options.Address, ct).ConfigureAwait(false);
            address = addresses.FirstOrDefault() ?? throw new ArgumentException("The bind host resolved to no addresses.");
            _listener = new TcpListener(address, _options.Port);
            _listener.Start();
            Volatile.Write(ref _port, ((IPEndPoint)_listener.LocalEndpoint).Port);
            _run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _shutdownRegistration = _run.Token.Register(() =>
            {
                _listener?.Stop();
                foreach (var peer in _peers.Values) peer.Close();
            });
            _acceptTask = AcceptLoopAsync(_listener, _run.Token);
        }
        catch
        {
            _listener?.Stop();
            _listener = null;
            _shutdownRegistration.Dispose();
            _run?.Dispose();
            _run = null;
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_run is null) return;
            await _run.CancelAsync().ConfigureAwait(false);
            _listener?.Stop();
            foreach (var peer in _peers.Values) peer.Close();
            await _acceptTask.ConfigureAwait(false);
            await Task.WhenAll(_handlers.Values.ToArray()).ConfigureAwait(false);
            _shutdownRegistration.Dispose();
            _run.Dispose();
            _run = null;
            _listener = null;
            _controllerId = null;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await StopAsync().ConfigureAwait(false);
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        SavedState? saved = null;
        if (File.Exists(_statePath))
            saved = await AtomicFile.ReadJsonAsync<SavedState>(_statePath, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The persisted server state is empty.");
        var policy = saved?.Policy ?? new SpeedPolicy();
        // Reconstructing also validates a persisted policy before any socket is opened.
        policy = new SpeedPolicy(policy.Enabled, policy.Multiplier, policy.MaxRate, policy.Revision);
        var version = _options.GameVersion == "1.7" ? saved?.GameVersion : _options.GameVersion;
        var modHash = _options.ModHash == "development" ? saved?.ModHash : _options.ModHash;
        if (version is not null && !Validation.IsFullGameVersion(version))
            throw new InvalidDataException("The persisted compatibility game version is invalid.");
        if (modHash is not null && (string.IsNullOrWhiteSpace(modHash) || modHash.Length > 512 || modHash.Any(char.IsControl)))
            throw new InvalidDataException("The persisted compatibility mod hash is invalid.");
        Volatile.Write(ref _policy, policy);
        Volatile.Write(ref _lockedGameVersion, version);
        Volatile.Write(ref _lockedModHash, modHash);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _nextPeer);
                var peer = new Peer(id, socket, ct);
                _peers[id] = peer;
                var handler = RunPeerAsync(peer);
                _handlers[id] = handler;
                _ = handler.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    _handlers.TryRemove(id, out _);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (SocketException)
        {
            // Listener failure shuts down established sockets too; StopAsync still observes every task.
            foreach (var peer in _peers.Values) peer.Close();
        }
    }

    private async Task RunPeerAsync(Peer peer)
    {
        var writer = WriteLoopAsync(peer);
        var heartbeat = HeartbeatLoopAsync(peer);
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = await peer.Connection.ReadAsync(handshake.Token).ConfigureAwait(false);
            if (hello is null) return;
            if (hello.Type != "Hello" || hello.Hello is null)
                throw new NetworkException("HelloRequired", "The first message must be a compatibility Hello.");
            await JoinAsync(peer, hello, handshake.Token).ConfigureAwait(false);
            while (!peer.Token.IsCancellationRequested)
            {
                var message = await peer.Connection.ReadAsync(peer.Token).ConfigureAwait(false);
                if (message is null) break;
                if (message.ProtocolVersion != NetworkProtocol.Version)
                    throw new NetworkException("ProtocolMismatch", $"Protocol version must be {NetworkProtocol.Version}.");
                await DispatchAsync(peer, message).ConfigureAwait(false);
            }
        }
        catch (NetworkException ex) { await SendFatalAsync(peer, ex.Code, ex.Message).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        catch (Exception) { await SendFatalAsync(peer, "ServerError", "The server could not process this connection.").ConfigureAwait(false); }
        finally
        {
            peer.Close();
            await Task.WhenAll(writer, heartbeat).ConfigureAwait(false);
            await LeaveAsync(peer).ConfigureAwait(false);
            _peers.TryRemove(peer.Id, out _);
            peer.DisposeToken();
        }
    }

    private async Task JoinAsync(Peer peer, WireMessage message, CancellationToken ct)
    {
        if (message.ProtocolVersion != NetworkProtocol.Version)
            throw new NetworkException("ProtocolMismatch", $"Protocol version must be {NetworkProtocol.Version}.");
        var hello = message.Hello!;
        Validation.Hello(hello);
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var version = _lockedGameVersion ?? hello.GameVersion;
            var modHash = _lockedModHash ?? hello.ModHash;
            if (!string.Equals(version, hello.GameVersion, StringComparison.Ordinal))
                throw new NetworkException("GameVersionMismatch", $"Game version mismatch: server requires exactly {version}.");
            if (!string.Equals(modHash, hello.ModHash, StringComparison.Ordinal))
                throw new NetworkException("ModHashMismatch", "Simulation assemblies, XML, mod order or configuration differ from the server.");
            if (_lockedGameVersion is null || _lockedModHash is null)
            {
                await SaveStateAsync(_policy, version, modHash, ct).ConfigureAwait(false);
                Volatile.Write(ref _lockedGameVersion, version);
                Volatile.Write(ref _lockedModHash, modHash);
            }
            peer.CanEdit = _options.EveryoneCanEdit || _controllerId is null;
            if (!_options.EveryoneCanEdit && _controllerId is null) _controllerId = peer.Id;
            peer.Joined = true;
            peer.Queue(new WireMessage
            {
                Type = "Welcome", Policy = _policy, CanEdit = peer.CanEdit,
                GameVersion = version, ModHash = modHash, ClientVersion = NetworkProtocol.ClientVersion
            });
        }
        finally { _stateGate.Release(); }
    }

    private async Task DispatchAsync(Peer peer, WireMessage message)
    {
        switch (message.Type)
        {
            case "Ping": peer.Queue(new WireMessage { Type = "Pong", RequestId = message.RequestId }); return;
            case "Pong": return;
            case "SetSpeed":
                RequireRequestId(message);
                await SetSpeedAsync(peer, message).ConfigureAwait(false);
                return;
            case "GetSnapshotManifest":
                RequireRequestId(message);
                try
                {
                    var manifest = await Snapshots.GetLatestAsync(peer.Token).ConfigureAwait(false);
                    peer.Queue(new WireMessage { Type = "SnapshotManifest", RequestId = message.RequestId, Manifest = manifest });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { peer.Queue(WireMessage.Error("SnapshotUnavailable", "The server snapshot is missing or invalid.", message.RequestId)); }
                return;
            case "GetSnapshotChunk":
                RequireRequestId(message);
                try
                {
                    var chunk = await Snapshots.ReadChunkAsync(message.SnapshotId!, message.ChunkIndex, peer.Token).ConfigureAwait(false);
                    peer.Queue(new WireMessage { Type = "SnapshotChunk", RequestId = message.RequestId, Chunk = chunk });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { peer.Queue(WireMessage.Error("InvalidSnapshotChunk", "The requested snapshot id or chunk is missing, out of bounds, or corrupt.", message.RequestId)); }
                return;
            default: throw new NetworkException("UnknownMessage", "This message is not supported. Players may download snapshots but may not upload them.");
        }
    }

    private async Task SetSpeedAsync(Peer peer, WireMessage message)
    {
        SpeedPolicy request;
        try
        {
            if (message.Speed is null) throw new ArgumentException("A speed policy is required.");
            request = new SpeedPolicy(message.Speed.Enabled, message.Speed.Multiplier, message.Speed.MaxRate);
        }
        catch (ArgumentException ex)
        {
            peer.Queue(WireMessage.Error("InvalidPolicy", ex.Message, message.RequestId));
            return;
        }
        await _stateGate.WaitAsync(peer.Token).ConfigureAwait(false);
        try
        {
            if (!peer.CanEdit)
            {
                peer.Queue(WireMessage.Error("ControllerOnly", "Only the server-assigned controller may change the speed policy.", message.RequestId));
                return;
            }
            if (_policy.Revision == long.MaxValue)
            {
                peer.Queue(WireMessage.Error("RevisionExhausted", "The policy revision has reached its maximum value.", message.RequestId));
                return;
            }
            var policy = request with { Revision = _policy.Revision + 1 };
            try
            {
                await SaveStateAsync(policy, _lockedGameVersion, _lockedModHash, _run!.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                peer.Queue(WireMessage.Error("PersistenceFailed", "The server could not persist this policy; the previous revision remains active.", message.RequestId));
                return;
            }
            Volatile.Write(ref _policy, policy);
            foreach (var recipient in _peers.Values)
                if (recipient.Joined)
                    recipient.Queue(new WireMessage { Type = "Policy", Policy = policy, RequestId = recipient.Id == peer.Id ? message.RequestId : 0 });
        }
        finally { _stateGate.Release(); }
    }

    private async Task LeaveAsync(Peer peer)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            peer.Joined = false;
            if (_options.EveryoneCanEdit || _controllerId != peer.Id) return;
            var successor = _peers.Values.Where(candidate => candidate.Joined && !candidate.Token.IsCancellationRequested)
                .OrderBy(candidate => candidate.Id).FirstOrDefault();
            _controllerId = successor?.Id;
            if (successor is not null)
            {
                successor.CanEdit = true;
                successor.Queue(new WireMessage { Type = "Role", CanEdit = true });
            }
        }
        finally { _stateGate.Release(); }
    }

    private Task SaveStateAsync(SpeedPolicy policy, string? version, string? modHash, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(_statePath, new SavedState(policy, version, modHash), ct);

    private static void RequireRequestId(WireMessage message)
    {
        if (message.RequestId == 0) throw new NetworkException("InvalidRequestId", "Requests require a positive request id.");
    }

    private static async Task SendFatalAsync(Peer peer, string code, string message)
    {
        try { await peer.Connection.WriteAsync(WireMessage.Error(code, message, fatal: true), peer.Token).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static async Task WriteLoopAsync(Peer peer)
    {
        try
        {
            await foreach (var message in peer.Outbound.Reader.ReadAllAsync(peer.Token).ConfigureAwait(false))
                await peer.Connection.WriteAsync(message, peer.Token).ConfigureAwait(false);
        }
        catch (Exception) { peer.Close(); }
    }

    private static async Task HeartbeatLoopAsync(Peer peer)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(peer.Token).ConfigureAwait(false))
            {
                if (!peer.Joined) continue;
                if (Environment.TickCount64 - peer.Connection.LastReceived > 20_000) { peer.Close(); return; }
                peer.Queue(new WireMessage { Type = "Ping" });
            }
        }
        catch (Exception) { peer.Close(); }
    }

    private sealed class Peer
    {
        private readonly CancellationTokenSource _lifetime;
        private int _closed;
        internal readonly long Id;
        internal readonly FramedConnection Connection;
        internal readonly Channel<WireMessage> Outbound = Channel.CreateBounded<WireMessage>(new BoundedChannelOptions(128)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        internal readonly CancellationToken Token;
        internal volatile bool Joined;
        internal bool CanEdit;

        internal Peer(long id, TcpClient socket, CancellationToken serverToken)
        {
            Id = id;
            Connection = new FramedConnection(socket);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            Token = _lifetime.Token;
        }

        internal void Queue(WireMessage message)
        {
            if (!Outbound.Writer.TryWrite(message)) Close();
        }

        internal void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _lifetime.Cancel();
            Outbound.Writer.TryComplete();
            Connection.Dispose();
        }

        internal void DisposeToken() => _lifetime.Dispose();
    }
}
