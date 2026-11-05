using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TurboTick.Networking;

/// <summary>Reusable TCP client. Policy changes are published only after the server accepts them.</summary>
public sealed class DedicatedClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _speedGate = new(1, 1);
    private readonly object _stateSync = new();
    private long _notificationGeneration;
    private readonly Channel<Action> _notifications = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    { SingleReader = true, SingleWriter = false });
    private readonly AsyncLocal<bool> _inNotification = new();
    private readonly Task _notificationTask;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private CancellationTokenSource? _connectionAttempt;
    private Session? _session;
    private SpeedPolicy? _currentPolicy;
    private string _status = "Disconnected";
    private int _connected;
    private int _canEdit;
    private int _disposed;
    private long _nextRequest;

    public event Action<SpeedPolicy>? PolicyChanged;
    public event Action<string>? StatusChanged;
    public SpeedPolicy? CurrentPolicy => Volatile.Read(ref _currentPolicy);
    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public bool CanEdit => Volatile.Read(ref _canEdit) != 0;
    public string Status => Volatile.Read(ref _status);

    public DedicatedClient() => _notificationTask = Task.Run(NotificationLoopAsync);

    /// <summary>Returns only after the complete compatibility handshake and authoritative initial policy.</summary>
    public async Task ConnectAsync(string host, int port, ClientHello hello, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Validation.Hello(hello);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await DisconnectCoreAsync().ConfigureAwait(false);
            long generation;
            lock (_stateSync)
            {
                generation = ++_notificationGeneration;
                SetStatus("Connecting", generation);
            }
            TcpClient? socket = new();
            FramedConnection? connection = null;
            using var attempt = new CancellationTokenSource();
            Volatile.Write(ref _connectionAttempt, attempt);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Token, _disposeCancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                connection = new FramedConnection(socket);
                await connection.WriteAsync(new WireMessage { Type = "Hello", Hello = hello }, timeout.Token).ConfigureAwait(false);
                var welcome = await connection.ReadAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new NetworkException("HandshakeClosed", "The server closed the connection before the handshake completed.");
                if (welcome.Type == "Error") throw ServerError(welcome);
                if (welcome.Type != "Welcome" || welcome.Policy is null || welcome.CanEdit is null)
                    throw new NetworkException("InvalidWelcome", "The server did not return a valid Welcome and speed policy.");
                if (welcome.ProtocolVersion != NetworkProtocol.Version || welcome.ClientVersion != NetworkProtocol.ClientVersion)
                    throw new NetworkException("ProtocolMismatch", "The server protocol or client version differs from this client.");
                if (!string.Equals(welcome.GameVersion, hello.GameVersion, StringComparison.Ordinal))
                    throw new NetworkException("GameVersionMismatch", "The server Welcome contains a different game version.");
                if (!string.Equals(welcome.ModHash, hello.ModHash, StringComparison.Ordinal))
                    throw new NetworkException("ModHashMismatch", "The server Welcome contains a different simulation mod hash.");
                timeout.Token.ThrowIfCancellationRequested();
                var session = new Session(this, connection, welcome.Policy.Revision, generation);
                lock (_stateSync)
                {
                    Volatile.Write(ref _session, session);
                    Volatile.Write(ref _currentPolicy, welcome.Policy);
                    Volatile.Write(ref _canEdit, welcome.CanEdit.Value ? 1 : 0);
                    Volatile.Write(ref _connected, 1);
                    PublishPolicy(welcome.Policy, session);
                    SetStatus(welcome.CanEdit.Value ? "Connected (controller)" : "Connected (read-only)", generation);
                }
                session.Reader = Task.Run(() => ReadLoopAsync(session));
                session.Heartbeat = Task.Run(() => HeartbeatLoopAsync(session));
                session.Completion = Task.WhenAll(session.Reader, session.Heartbeat);
                connection = null;
                socket = null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !attempt.IsCancellationRequested && !_disposeCancellation.IsCancellationRequested)
            {
                var error = new NetworkException("HandshakeTimedOut", "The server handshake timed out.");
                SetStatus(error.Message, generation);
                throw error;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Connection canceled", generation);
                throw;
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, generation);
                throw;
            }
            finally
            {
                Interlocked.CompareExchange(ref _connectionAttempt, null, attempt);
                connection?.Dispose();
                socket?.Dispose();
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Serializes local edits and awaits the persisted, broadcast server revision.</summary>
    public async Task SetSpeedAsync(bool enabled, float multiplier, float maxRate, CancellationToken ct = default)
    {
        _ = new SpeedPolicy(enabled, multiplier, maxRate);
        var session = ConnectedSession();
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        try { await _speedGate.WaitAsync(queued.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new NetworkException("SessionChanged", "The original server connection closed while this edit was queued."); }
        try
        {
            var reply = await RequestAsync(new WireMessage
            { Type = "SetSpeed", Speed = new SpeedRequest(enabled, multiplier, maxRate) }, ct, session).ConfigureAwait(false);
            if (reply.Type != "Policy" || reply.Policy is null)
                throw new NetworkException("InvalidResponse", "The server did not acknowledge the policy change.");
        }
        finally { _speedGate.Release(); }
    }

    public async Task<SnapshotManifest?> GetSnapshotManifestAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(new WireMessage { Type = "GetSnapshotManifest" }, ct).ConfigureAwait(false);
        if (reply.Type != "SnapshotManifest")
            throw new NetworkException("InvalidResponse", "The server did not return a snapshot manifest.");
        if (reply.Manifest is not null) Validation.Manifest(reply.Manifest);
        return reply.Manifest;
    }

    /// <summary>Verifies every reusable .part chunk, downloads missing chunks, then verifies the complete SHA256.</summary>
    public async Task DownloadSnapshotAsync(SnapshotManifest manifest, string destination, Action<long, long>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var downloadSession = Volatile.Read(ref _session);
        Validation.Manifest(manifest);
        // Freeze caller-owned lists while an asynchronous download is running.
        manifest = manifest with { ChunkHashes = manifest.ChunkHashes.ToArray() };
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(destination))
        {
            await using var cached = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (cached.Length == manifest.Length && string.Equals(
                Convert.ToHexStringLower(await SHA256.HashDataAsync(cached, ct).ConfigureAwait(false)), manifest.Id, StringComparison.Ordinal))
            {
                progress?.Invoke(manifest.Length, manifest.Length);
                ct.ThrowIfCancellationRequested();
                return;
            }
        }
        if (downloadSession is null)
            throw new NetworkException("NotConnected", "Connect to the server before downloading missing snapshot chunks.");
        if (!ReferenceEquals(Volatile.Read(ref _session), downloadSession) || downloadSession.IsClosed || !IsConnected)
            throw new NetworkException("SessionChanged", "The snapshot download's original server connection has closed or changed.");
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadSession.Token);
        ct = downloadCancellation.Token;
        var partial = destination + ".part";
        var metadata = partial + ".json";
        var resetPartial = false;
        if (File.Exists(metadata))
        {
            try
            {
                var previous = await AtomicFile.ReadJsonAsync<SnapshotManifest>(metadata, ct).ConfigureAwait(false);
                resetPartial = previous is null || previous.Id != manifest.Id || previous.Length != manifest.Length ||
                    previous.ChunkSize != manifest.ChunkSize || previous.ChunkHashes is null ||
                    !previous.ChunkHashes.SequenceEqual(manifest.ChunkHashes, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or System.Text.Json.JsonException)
            { resetPartial = true; }
        }
        await AtomicFile.WriteJsonAsync(metadata, manifest, ct).ConfigureAwait(false);
        await using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            if (resetPartial) file.SetLength(0);
            var existingLength = file.Length;
            file.SetLength(manifest.Length);
            var buffer = new byte[manifest.ChunkSize];
            long done = 0;
            progress?.Invoke(0, manifest.Length);
            for (var index = 0; index < manifest.ChunkHashes.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var expectedLength = Validation.ChunkLength(manifest, index);
                var offset = (long)index * manifest.ChunkSize;
                var reusable = false;
                if (offset + expectedLength <= existingLength)
                {
                    file.Position = offset;
                    await file.ReadExactlyAsync(buffer.AsMemory(0, expectedLength), ct).ConfigureAwait(false);
                    reusable = string.Equals(Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, expectedLength))),
                        manifest.ChunkHashes[index], StringComparison.Ordinal);
                }
                if (!reusable)
                {
                    var reply = await RequestAsync(new WireMessage
                    { Type = "GetSnapshotChunk", SnapshotId = manifest.Id, ChunkIndex = index }, ct, downloadSession).ConfigureAwait(false);
                    var chunk = reply.Chunk;
                    if (reply.Type != "SnapshotChunk" || chunk is null || chunk.Id != manifest.Id || chunk.Index != index ||
                        chunk.Data is null || chunk.Data.Length != expectedLength)
                        throw new NetworkException("InvalidSnapshotChunk", "The server returned an unexpected snapshot chunk.");
                    var actualHash = Convert.ToHexStringLower(SHA256.HashData(chunk.Data));
                    if (!string.Equals(actualHash, chunk.Sha256, StringComparison.Ordinal) ||
                        !string.Equals(actualHash, manifest.ChunkHashes[index], StringComparison.Ordinal))
                        throw new NetworkException("SnapshotHashMismatch", "The downloaded snapshot chunk failed SHA256 verification.");
                    file.Position = offset;
                    await file.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
                }
                done += expectedLength;
                progress?.Invoke(done, manifest.Length);
            }
            ct.ThrowIfCancellationRequested();
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            file.Position = 0;
            var wholeHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!string.Equals(wholeHash, manifest.Id, StringComparison.Ordinal))
                throw new NetworkException("SnapshotHashMismatch", "The complete snapshot failed SHA256 verification.");
        }
        ct.ThrowIfCancellationRequested();
        File.Move(partial, destination, overwrite: true);
        File.Delete(metadata);
    }

    public async Task DisconnectAsync()
    {
        CancelConnectionAttempt();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        _notifications.Writer.TryComplete();
        _disposeCancellation.Dispose();
        // Event handlers run on their own ordered dispatcher and may synchronously dispose this client.
        if (!_inNotification.Value) await _notificationTask.ConfigureAwait(false);
    }

    private void CancelConnectionAttempt()
    {
        try { Volatile.Read(ref _connectionAttempt)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task DisconnectCoreAsync()
    {
        Session? session;
        lock (_stateSync)
        {
            var generation = ++_notificationGeneration;
            session = Interlocked.Exchange(ref _session, null);
            Volatile.Write(ref _connected, 0);
            Volatile.Write(ref _canEdit, 0);
            SetStatus("Disconnected", generation);
        }
        if (session is not null)
        {
            session.Close("Disconnected");
            await session.Completion.ConfigureAwait(false);
            session.DisposeToken();
        }
    }

    private Session ConnectedSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = Volatile.Read(ref _session);
        if (session is null || session.IsClosed || !IsConnected)
            throw new NetworkException("NotConnected", "Connect to the server and complete the handshake first.");
        return session;
    }

    private async Task<WireMessage> RequestAsync(WireMessage request, CancellationToken ct, Session? expectedSession = null)
    {
        var session = expectedSession ?? ConnectedSession();
        if (!ReferenceEquals(Volatile.Read(ref _session), session) || session.IsClosed || !IsConnected)
            throw new NetworkException("SessionChanged", "This request belongs to a server connection that has closed or changed.");
        var id = Interlocked.Increment(ref _nextRequest);
        if (id < 1) throw new InvalidOperationException("The request id space has been exhausted.");
        var response = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A caller canceling its wait must not leave an unobserved fault during a simultaneous disconnect.
        _ = response.Task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!session.Pending.TryAdd(id, response)) throw new InvalidOperationException("Duplicate request id.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await session.Connection.WriteAsync(request with { RequestId = id }, timeout.Token).ConfigureAwait(false);
            return await response.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NetworkException(session.IsClosed ? "Disconnected" : "RequestTimedOut",
                session.IsClosed ? "The server connection closed before the request completed." : "The server request timed out.");
        }
        catch (Exception ex) when ((ex is IOException or SocketException or ObjectDisposedException) && ex is not NetworkException)
        {
            session.Close("The server connection closed.");
            throw new NetworkException("TransportError", "The server connection closed before the request completed.", ex);
        }
        finally { session.Pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync(Session session)
    {
        var reason = "The server closed the connection.";
        try
        {
            while (!session.Token.IsCancellationRequested)
            {
                var message = await session.Connection.ReadAsync(session.Token).ConfigureAwait(false);
                if (message is null) break;
                if (message.ProtocolVersion != NetworkProtocol.Version)
                    throw new NetworkException("ProtocolMismatch", "The received protocol version is incompatible.");
                switch (message.Type)
                {
                    case "Ping":
                        await session.Connection.WriteAsync(new WireMessage { Type = "Pong", RequestId = message.RequestId }, session.Token).ConfigureAwait(false);
                        break;
                    case "Pong": break;
                    case "Policy":
                        if (message.Policy is null || message.Policy.Revision < session.LastRevision)
                            throw new NetworkException("InvalidPolicy", "The server sent an invalid or decreasing policy revision.");
                        session.LastRevision = message.Policy.Revision;
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_session, session) && !session.IsClosed)
                            {
                                Volatile.Write(ref _currentPolicy, message.Policy);
                                PublishPolicy(message.Policy, session);
                            }
                        }
                        CompleteResponse(session, message);
                        break;
                    case "Role":
                        if (message.CanEdit is null) throw new NetworkException("InvalidRole", "The server role update is incomplete.");
                        lock (_stateSync)
                        {
                            if (ReferenceEquals(_session, session) && !session.IsClosed)
                            {
                                Volatile.Write(ref _canEdit, message.CanEdit.Value ? 1 : 0);
                                SetStatus(message.CanEdit.Value ? "Connected (controller)" : "Connected (read-only)", session.Generation);
                            }
                        }
                        break;
                    case "SnapshotManifest":
                    case "SnapshotChunk":
                        CompleteResponse(session, message);
                        break;
                    case "Error":
                        var error = ServerError(message);
                        if (session.Pending.TryGetValue(message.RequestId, out var pending)) pending.TrySetException(error);
                        if (message.Fatal) { reason = error.Message; return; }
                        break;
                    default: throw new NetworkException("UnknownMessage", "The server sent an unsupported message.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; }
        finally { session.Close(reason); }
    }

    private static void CompleteResponse(Session session, WireMessage message)
    {
        if (message.RequestId > 0 && session.Pending.TryGetValue(message.RequestId, out var pending))
            pending.TrySetResult(message);
    }

    private static async Task HeartbeatLoopAsync(Session session)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(session.Token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - session.Connection.LastReceived > 20_000)
                { session.Close("The server heartbeat timed out."); return; }
                await session.Connection.WriteAsync(new WireMessage { Type = "Ping" }, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { session.Close(ex.Message); }
    }

    private void OnSessionClosed(Session session, string reason)
    {
        lock (_stateSync)
        {
            if (!ReferenceEquals(_session, session)) return;
            Volatile.Write(ref _connected, 0);
            Volatile.Write(ref _canEdit, 0);
            SetStatus(reason, session.Generation);
        }
    }

    private void PublishPolicy(SpeedPolicy policy, Session session)
    {
        var handlers = PolicyChanged;
        if (handlers is null) return;
        _notifications.Writer.TryWrite(() =>
        {
            foreach (Action<SpeedPolicy> handler in handlers.GetInvocationList())
            {
                // A delayed observer must never apply an earlier room's revision to a new room.
                if (session.Generation != Volatile.Read(ref _notificationGeneration) || session.IsClosed ||
                    !ReferenceEquals(Volatile.Read(ref _session), session)) return;
                try { handler(policy); } catch (Exception) { }
            }
        });
    }

    private void SetStatus(string status, long generation)
    {
        lock (_stateSync)
        {
            if (generation != _notificationGeneration || Interlocked.Exchange(ref _status, status) == status) return;
            var handlers = StatusChanged;
            if (handlers is null) return;
            _notifications.Writer.TryWrite(() =>
            {
                foreach (Action<string> handler in handlers.GetInvocationList())
                {
                    // Applies to connection attempts, disconnects, failures, and controller role changes.
                    if (generation != Volatile.Read(ref _notificationGeneration)) return;
                    try { handler(status); } catch (Exception) { }
                }
            });
        }
    }

    private async Task NotificationLoopAsync()
    {
        _inNotification.Value = true;
        await foreach (var notification in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
            try { notification(); } catch (Exception) { }
    }

    private static NetworkException ServerError(WireMessage message) =>
        new(message.Code ?? "ServerError", message.Message ?? "The server reported an unspecified error.");

    private sealed class Session
    {
        private readonly DedicatedClient _owner;
        private readonly CancellationTokenSource _lifetime = new();
        private int _closed;
        internal readonly FramedConnection Connection;
        internal readonly ConcurrentDictionary<long, TaskCompletionSource<WireMessage>> Pending = new();
        internal Task Reader = Task.CompletedTask;
        internal Task Heartbeat = Task.CompletedTask;
        internal Task Completion = Task.CompletedTask;
        internal long LastRevision;
        internal readonly long Generation;
        internal readonly CancellationToken Token;
        internal bool IsClosed => Volatile.Read(ref _closed) != 0;

        internal Session(DedicatedClient owner, FramedConnection connection, long revision, long generation)
        { _owner = owner; Connection = connection; LastRevision = revision; Generation = generation; Token = _lifetime.Token; }

        internal void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _lifetime.Cancel();
            Connection.Dispose();
            foreach (var pending in Pending.Values)
                pending.TrySetException(new NetworkException("Disconnected", reason));
            _owner.OnSessionClosed(this, reason);
        }

        internal void DisposeToken() => _lifetime.Dispose();
    }
}
