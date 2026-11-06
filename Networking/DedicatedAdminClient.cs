using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TurboTick.Networking;

/// <summary>Password-authenticated management on the game TCP port, independent of player compatibility and roles.</summary>
public sealed class DedicatedAdminClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _dispose = new();
    private Session? _session;
    private long _nextRequest;
    private int _disposed;
    public bool IsConnected => Volatile.Read(ref _session) is { IsClosed: false };

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("The management password must contain 1 to 4096 characters.", nameof(password));
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, _dispose.Token);
        await _lifecycle.WaitAsync(attempt.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await DisconnectCoreAsync().ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            TcpClient? socket = new();
            FramedConnection? connection = null;
            try
            {
                await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                connection = new FramedConnection(socket);
                await connection.WriteAsync(new WireMessage { Type = "AdminHello", ClientVersion = NetworkProtocol.ClientVersion }, timeout.Token).ConfigureAwait(false);
                var challengeReply = await ReadHandshakeAsync(connection, timeout.Token).ConfigureAwait(false);
                if (challengeReply.Type != "AdminChallenge")
                    throw new NetworkException("InvalidAdminChallenge", "The server did not return a management challenge.");
                AdminAuthentication.ValidateChallenge(challengeReply.AdminChallenge);
                var challenge = challengeReply.AdminChallenge!;
                var key = AdminAuthentication.DeriveKey(password, challenge.Salt, challenge.Iterations);
                byte[] proof;
                try { proof = AdminAuthentication.CreateProof(key, challenge); }
                finally { CryptographicOperations.ZeroMemory(key); }
                try
                {
                    await connection.WriteAsync(new WireMessage { Type = "AdminProof", AdminProof = new AdminProof(challenge.Nonce, proof) }, timeout.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(proof); }
                var welcome = await ReadHandshakeAsync(connection, timeout.Token).ConfigureAwait(false);
                if (welcome.Type != "AdminWelcome")
                    throw new NetworkException("InvalidAdminWelcome", "The server did not confirm management authentication.");
                timeout.Token.ThrowIfCancellationRequested();
                var session = new Session(connection);
                Volatile.Write(ref _session, session);
                session.Completion = ReadLoopAsync(session);
                connection = null;
                socket = null;
            }
            catch (OperationCanceledException) when (!attempt.IsCancellationRequested)
            { throw new NetworkException("HandshakeTimedOut", "The management handshake timed out."); }
            finally { connection?.Dispose(); socket?.Dispose(); }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<ServerAdminStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(new WireMessage { Type = "AdminGetStatus" }, ct).ConfigureAwait(false);
        return reply.Type == "AdminStatus" && reply.AdminStatus is { } status ? status :
            throw new NetworkException("InvalidResponse", "The server did not return management status.");
    }

    /// <summary>Archive the existing room and reset it with the caller's complete validated compatibility fingerprint.
    /// After the request is sent, canceling the wait does not undo a server commit; query status before retrying.</summary>
    public async Task<ServerResetResult> ResetRoomAsync(ClientHello compatibility, CancellationToken ct = default)
    {
        Validation.Hello(compatibility);
        var reply = await RequestAsync(new WireMessage { Type = "AdminResetRoom", Hello = compatibility }, ct).ConfigureAwait(false);
        return reply.Type == "AdminResetResult" && reply.AdminResetResult is { BackupId.Length: > 0 } result ? result :
            throw new NetworkException("InvalidResponse", "The server did not confirm room reset.");
    }

    private async Task<WireMessage> RequestAsync(WireMessage request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = Volatile.Read(ref _session);
        if (session is null || session.IsClosed) throw new NetworkException("NotConnected", "Connect to management first.");
        var id = Interlocked.Increment(ref _nextRequest);
        if (id <= 0) throw new InvalidOperationException("The request id space is exhausted.");
        var response = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = response.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        session.Pending[id] = response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!ReferenceEquals(session, Volatile.Read(ref _session)) || session.IsClosed)
                throw new NetworkException("SessionChanged", "The original management connection changed.");
            await session.Connection.WriteAsync(request with { RequestId = id }, timeout.Token).ConfigureAwait(false);
            return await response.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new NetworkException(session.IsClosed ? "Disconnected" : "RequestTimedOut", "The management request did not complete."); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException || ex is IOException and not NetworkException)
        {
            session.Close();
            throw new NetworkException("TransportError", "The management connection closed.", ex);
        }
        finally { session.Pending.TryRemove(id, out _); }
    }

    private static async Task<WireMessage> ReadHandshakeAsync(FramedConnection connection, CancellationToken ct)
    {
        var message = await connection.ReadAsync(ct).ConfigureAwait(false) ??
            throw new NetworkException("HandshakeClosed", "The server closed the management handshake.");
        if (message.ProtocolVersion != NetworkProtocol.Version)
            throw new NetworkException("ProtocolMismatch", "The server management protocol differs.");
        if (message.Type == "Error") throw Error(message);
        return message;
    }

    private static async Task ReadLoopAsync(Session session)
    {
        try
        {
            while (!session.Token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var message = await session.Connection.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (message is null) break;
                if (message.ProtocolVersion != NetworkProtocol.Version)
                    throw new NetworkException("ProtocolMismatch", "The management protocol changed.");
                if (message.Type == "Ping")
                {
                    await session.Connection.WriteAsync(new WireMessage { Type = "Pong", RequestId = message.RequestId }, session.Token).ConfigureAwait(false);
                    continue;
                }
                if (message.Type is not ("AdminStatus" or "AdminResetResult" or "Error"))
                    throw new NetworkException("UnknownMessage", "The server sent an unsupported management response.");
                if (session.Pending.TryGetValue(message.RequestId, out var pending))
                {
                    if (message.Type == "Error") pending.TrySetException(Error(message));
                    else pending.TrySetResult(message);
                }
                if (message.Fatal) break;
            }
        }
        catch (Exception) { }
        finally { session.Close(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _dispose.CancelAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        session.Close();
        await session.Completion.ConfigureAwait(false);
        session.Lifetime.Dispose();
    }

    private static NetworkException Error(WireMessage message) =>
        new(message.Code ?? "ServerError", message.Message ?? "The server reported a management error.");

    private sealed class Session
    {
        internal readonly FramedConnection Connection;
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly CancellationToken Token;
        internal Session(FramedConnection connection) { Connection = connection; Token = Lifetime.Token; }
        internal readonly ConcurrentDictionary<long, TaskCompletionSource<WireMessage>> Pending = new();
        internal Task Completion = Task.CompletedTask;
        private int _closed;
        internal bool IsClosed => Volatile.Read(ref _closed) != 0;
        internal void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Lifetime.Cancel();
            Connection.Dispose();
            foreach (var pending in Pending.Values)
                pending.TrySetException(new NetworkException("Disconnected", "The management connection closed."));
        }
    }
}
