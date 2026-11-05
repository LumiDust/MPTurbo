using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboTick.Networking;

var root = Path.Combine(Path.GetTempPath(), "TurboTick-network-verify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    await Check("policy constructor and with-expression reject invalid numerics", async () =>
    {
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, 20.01f })
            await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(new SpeedPolicy(Multiplier: bad)));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(new SpeedPolicy() with { MaxRate = float.NaN }));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(new SpeedPolicy(Revision: -1)));
    });

    await using var server = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "server")));
    await server.StartAsync();
    var port = server.Port;
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", port, Hello());
    var heartbeatVerification = VerifyHeartbeat(port);
    await Check("development hash and full version lock reject incompatible peers", async () =>
    {
        Assert(server.LockedGameVersion == "1.7.4910" && server.LockedModHash == "test-hash", "Compatibility locks are missing.");
        await using var other = new DedicatedClient();
        var mismatch = await Throws<NetworkException>(() => other.ConnectAsync("127.0.0.1", port, Hello() with { ModHash = "different" }));
        Assert(mismatch.Code == "ModHashMismatch" && !other.IsConnected, "Handshake reported TCP connection as joined.");
        await other.ConnectAsync("127.0.0.1", port, Hello());
        Assert(other.CurrentPolicy == server.CurrentPolicy, "Reused client did not receive initial policy.");
    });

    await Check("framing rejects bad lengths, malformed JSON and incompatible protocol with structured errors", async () =>
    {
        foreach (var length in new[] { 0, -1, NetworkProtocol.MaximumFrameLength + 1 })
        {
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, port);
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
            await raw.GetStream().WriteAsync(prefix);
            var error = await ReadFrame(raw.GetStream());
            Assert(error.GetProperty("type").GetString() == "Error" && error.GetProperty("code").GetString() == "InvalidFrameLength", "Invalid frame was not rejected structurally.");
            await Eof(raw.GetStream());
        }
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, port);
            await WriteFrame(raw.GetStream(), "{bad json");
            var error = await ReadFrame(raw.GetStream());
            Assert(error.GetProperty("code").GetString() == "InvalidMessage", "Malformed JSON lacked structured error.");
        }
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, port);
            await WriteFrame(raw.GetStream(), JsonSerializer.Serialize(new { type = "Hello", protocolVersion = 9, hello = Hello() }));
            var error = await ReadFrame(raw.GetStream());
            Assert(error.GetProperty("code").GetString() == "ProtocolMismatch", "Protocol mismatch was not checked.");
        }
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, port);
            await WriteFrame(raw.GetStream(), JsonSerializer.Serialize(new { type = "Hello", protocolVersion = 1, hello = Hello() with { ClientVersion = "9.0.0" } }));
            var error = await ReadFrame(raw.GetStream());
            Assert(error.GetProperty("code").GetString() == "ClientVersionMismatch", "Client version mismatch was not checked.");
        }
    });

    await Check("snapshot store validates empty streams, ids and chunk bounds", async () =>
    {
        var store = new SnapshotStore(Path.Combine(root, "empty"));
        var empty = await store.PutAsync(new MemoryStream());
        Assert(empty.Length == 0 && empty.ChunkHashes.Count == 0 && empty.Id == Convert.ToHexStringLower(SHA256.HashData([])), "Empty snapshot is invalid.");
        Assert((await store.GetLatestAsync())?.Id == empty.Id, "Latest snapshot did not persist.");
        await Throws<ArgumentException>(() => store.ReadChunkAsync("../escape", 0));
        await Throws<ArgumentOutOfRangeException>(() => store.ReadChunkAsync(empty.Id, 0));
    });

    await Check("cancel and resume verifies existing chunks and repairs corrupt partial data", async () =>
    {
        var bytes = RandomNumberGenerator.GetBytes(NetworkProtocol.SnapshotChunkSize * 6 + 119);
        var manifest = await server.Snapshots.PutAsync(new MemoryStream(bytes));
        var networkManifest = await client.GetSnapshotManifestAsync();
        Assert(networkManifest?.Id == manifest.Id, "Server manifest differs.");
        var destination = Path.Combine(root, "snapshot.rws");
        using (var cancel = new CancellationTokenSource())
        {
            await Throws<OperationCanceledException>(() => client.DownloadSnapshotAsync(manifest, destination,
                (done, _) => { if (done >= manifest.ChunkSize * 2L) cancel.Cancel(); }, cancel.Token));
        }
        Assert(File.Exists(destination + ".part") && !File.Exists(destination), "Canceled download did not retain partial data.");
        // Damage the second partial chunk so it must be fetched again.
        await using (var part = new FileStream(destination + ".part", FileMode.Open, FileAccess.Write))
        { part.Position = manifest.ChunkSize; await part.WriteAsync(new byte[] { (byte)(bytes[manifest.ChunkSize] ^ 255) }); }
        // Damage the server's first chunk: successful resume proves that the verified first partial chunk was reused.
        var blob = Path.Combine(root, "server", "Snapshots", "objects", manifest.Id + ".blob");
        await using (var source = new FileStream(blob, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        { await source.WriteAsync(new byte[] { (byte)(bytes[0] ^ 255) }); }
        await using var other = new DedicatedClient();
        await other.ConnectAsync("127.0.0.1", port, Hello());
        var download = client.DownloadSnapshotAsync(manifest, destination);
        await other.SetSpeedAsync(true, 3.5f, 47f);
        await download;
        Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(bytes), "Resumed snapshot bytes differ.");
        Assert(!File.Exists(destination + ".part") && !File.Exists(destination + ".part.json"), "Successful download left temporary files.");
        var corruptError = await Throws<NetworkException>(() => client.DownloadSnapshotAsync(manifest, Path.Combine(root, "corrupt-source.rws")));
        Assert(corruptError.Code == "InvalidSnapshotChunk" && client.IsConnected, "Corrupt source terminated a valid room connection.");
        await client.SetSpeedAsync(true, 3.5f, 47f);
        File.Delete(blob);
        await client.DownloadSnapshotAsync(manifest, destination);
        Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(bytes), "Complete destination was not a verified local cache hit.");
        Assert(client.CurrentPolicy?.Multiplier == 3.5f && client.CurrentPolicy.MaxRate == 47f && client.CurrentPolicy.Enabled,
            "Snapshot transfer blocked policy updates.");
        // A newly published manifest must not trust a previous snapshot's .part sidecar.
        var nextBytes = RandomNumberGenerator.GetBytes(manifest.ChunkSize + 17);
        var next = await server.Snapshots.PutAsync(new MemoryStream(nextBytes));
        await File.WriteAllBytesAsync(destination + ".part", bytes);
        await File.WriteAllTextAsync(destination + ".part.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await client.DownloadSnapshotAsync(next, destination);
        Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(nextBytes), "Stale partial content contaminated a new manifest.");
        File.Delete(destination);
        await File.WriteAllBytesAsync(destination + ".part", new byte[13]);
        await File.WriteAllTextAsync(destination + ".part.json", "{invalid JSON");
        await client.DownloadSnapshotAsync(next, destination);
        Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(nextBytes), "Malformed sidecar was not replaced and repaired.");
    });

    await Check("client handshake cancellation permits reconnecting the same instance", async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var connection = client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Hello(), cancel.Token);
            using var silentPeer = await accept;
            await Throws<OperationCanceledException>(() => connection);
            Assert(!client.IsConnected, "Canceled handshake remained connected.");
        }
        finally { listener.Stop(); }
        await client.ConnectAsync("127.0.0.1", port, Hello());
        await client.SetSpeedAsync(false, 4f, 58f);
        Assert(client.CurrentPolicy == server.CurrentPolicy, "Reconnection did not receive or acknowledge new state.");
    });

    await Check("dispose promptly cancels an unfinished default-timeout handshake", async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var stopping = new DedicatedClient();
            var accepted = listener.AcceptTcpClientAsync();
            var connection = stopping.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Hello());
            using var silent = await accepted;
            await stopping.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await Throws<OperationCanceledException>(() => connection);
            Assert(!stopping.IsConnected, "Disposed handshake remained connected.");
        }
        finally { listener.Stop(); }
    });

    await Check("event exceptions are isolated and synchronous event disposal avoids self-wait", async () =>
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventClient = new DedicatedClient();
        eventClient.PolicyChanged += _ => throw new InvalidOperationException("Deliberate subscriber exception.");
        eventClient.PolicyChanged += _ =>
        {
            eventClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            complete.TrySetResult();
        };
        await eventClient.ConnectAsync("127.0.0.1", port, Hello());
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!eventClient.IsConnected, "Disposing from event did not close client.");
    });

    await Check("a snapshot request without any server reply ends on disconnect", async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeServer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await ReadFrame(socket.GetStream());
            await WriteFrame(socket.GetStream(), JsonSerializer.Serialize(new
            {
                type = "Welcome", protocolVersion = 1, clientVersion = NetworkProtocol.ClientVersion,
                gameVersion = "1.7.4910", modHash = "test-hash", canEdit = true, policy = new SpeedPolicy()
            }));
            await ReadFrame(socket.GetStream());
            requestSeen.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        });
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Hello());
            var pending = client.GetSnapshotManifestAsync();
            await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync();
            await Throws<NetworkException>(() => pending);
        }
        finally { stop.Cancel(); listener.Stop(); await fakeServer; }
    });

    await Check("concurrent requests and disconnect complete every pending future", async () =>
    {
        for (var round = 0; round < 20; round++)
        {
            await client.ConnectAsync("127.0.0.1", port, Hello());
            var requests = Enumerable.Range(0, 20).Select(_ => Observe(client.GetSnapshotManifestAsync())).ToArray();
            await client.DisconnectAsync();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert(client.CurrentPolicy is not null, "Disconnect discarded last known policy.");
    });

    await Check("queued edits stay pinned to the original connection during reconnect", async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeServer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await ReadFrame(socket.GetStream());
            await WriteFrame(socket.GetStream(), JsonSerializer.Serialize(new
            {
                type = "Welcome", protocolVersion = 1, clientVersion = NetworkProtocol.ClientVersion,
                gameVersion = "1.7.4910", modHash = "test-hash", canEdit = true, policy = new SpeedPolicy()
            }));
            await ReadFrame(socket.GetStream());
            requestSeen.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        });
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Hello());
            var inFlight = Observe(client.SetSpeedAsync(true, 9f, 60f));
            await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = Enumerable.Range(0, 100).Select(_ => Observe(client.SetSpeedAsync(true, 10f, 60f))).ToArray();
            var before = server.CurrentPolicy;
            await client.ConnectAsync("127.0.0.1", port, Hello());
            await Task.WhenAll(queued.Append(inFlight));
            Assert(server.CurrentPolicy == before, "An old connection's queued edit leaked into a new connection.");
        }
        finally { stop.Cancel(); listener.Stop(); await fakeServer; }
    });

    await Check("a multi-chunk download does not continue across a changed connection", async () =>
    {
        var bytes = RandomNumberGenerator.GetBytes(NetworkProtocol.SnapshotChunkSize * 3 + 3);
        var manifest = await server.Snapshots.PutAsync(new MemoryStream(bytes));
        await using var replacement = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "replacement")));
        await replacement.Snapshots.PutAsync(new MemoryStream(bytes));
        await replacement.StartAsync();
        await client.ConnectAsync("127.0.0.1", port, Hello());
        var changed = false;
        await Throws<OperationCanceledException>(() => client.DownloadSnapshotAsync(manifest, Path.Combine(root, "changed-session.rws"),
            (done, _) =>
            {
                if (done < manifest.ChunkSize || changed) return;
                changed = true;
                client.ConnectAsync("127.0.0.1", replacement.Port, Hello()).GetAwaiter().GetResult();
            }));
        Assert(changed && client.IsConnected && replacement.CurrentPolicy.Revision == 0, "Session-pinned download changed the new connection.");
        await client.SetSpeedAsync(true, 2f, 60f);
        Assert(replacement.CurrentPolicy.Revision == 1, "The new connection was damaged by canceling the previous download.");
    });

    await Check("blocked policy and controller notifications never cross into another connection", async () =>
    {
        await using var roomA = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "events-a"), EveryoneCanEdit: false));
        await using var roomB = new DedicatedServer(new ServerOptions(Port: 0, DataDirectory: Path.Combine(root, "events-b"), EveryoneCanEdit: false));
        await roomA.StartAsync();
        await roomB.StartAsync();
        await using var controllerA = new DedicatedClient();
        await using var controllerB = new DedicatedClient();
        await controllerA.ConnectAsync("127.0.0.1", roomA.Port, Hello());
        await controllerB.ConnectAsync("127.0.0.1", roomB.Port, Hello());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var changing = new DedicatedClient();
        var blocked = 0;
        var released = 0;
        var policies = new System.Collections.Concurrent.ConcurrentQueue<SpeedPolicy>();
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        changing.StatusChanged += status =>
        {
            if (status != "Connecting" || Interlocked.Exchange(ref blocked, 1) != 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Notification barrier was not released.");
        };
        changing.StatusChanged += status => { if (Volatile.Read(ref released) != 0) statuses.Enqueue(status); };
        changing.PolicyChanged += policies.Enqueue;
        try
        {
            var joiningA = changing.ConnectAsync("127.0.0.1", roomA.Port, Hello());
            Assert(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Notification dispatcher did not enter the barrier.");
            await joiningA;
            await controllerA.SetSpeedAsync(true, 7f, 60f);
            await Until(() => changing.CurrentPolicy?.Revision == 1);
            await controllerA.DisconnectAsync();
            await Until(() => changing.CanEdit); // Queue an A-specific controller role notification too.
            await changing.DisconnectAsync();
            await changing.ConnectAsync("127.0.0.1", roomB.Port, Hello());
            Assert(changing.CurrentPolicy == new SpeedPolicy() && !changing.CanEdit, "Room B did not establish its own initial state.");
            Volatile.Write(ref released, 1);
            release.Set();
            await Until(() => policies.Count != 0 && statuses.Contains("Connected (read-only)"));
            Assert(policies.All(policy => policy == new SpeedPolicy()), "A stale room A policy reached a room B observer.");
            Assert(statuses.All(status => status is "Connecting" or "Connected (read-only)"), "An old disconnect or controller role reached a new connection observer.");
        }
        finally { release.Set(); }
    });

    await Check("controller reassignment is server-owned and persisted state survives restart", async () =>
    {
        var directory = Path.Combine(root, "controller");
        var options = new ServerOptions(Port: 0, DataDirectory: directory, EveryoneCanEdit: false);
        long revision;
        await using (var authority = new DedicatedServer(options))
        {
            await authority.StartAsync();
            await using var first = new DedicatedClient();
            await using var second = new DedicatedClient();
            await first.ConnectAsync("127.0.0.1", authority.Port, Hello());
            await second.ConnectAsync("127.0.0.1", authority.Port, Hello());
            Assert(first.CanEdit && !second.CanEdit, "Initial controller role is incorrect.");
            var error = await Throws<NetworkException>(() => second.SetSpeedAsync(true, 8f, 48f));
            Assert(error.Code == "ControllerOnly" && second.IsConnected, "Role error closed the peer or lost structured error.");
            await first.SetSpeedAsync(true, 6f, 65f);
            revision = authority.CurrentPolicy.Revision;
            await first.DisconnectAsync();
            await Until(() => second.CanEdit);
            await second.SetSpeedAsync(false, 7f, 50f);
            Assert(authority.CurrentPolicy.Revision == revision + 1, "Promoted controller did not advance revision.");
            revision++;
        }
        await using var restarted = new DedicatedServer(options);
        await restarted.StartAsync();
        await using var restored = new DedicatedClient();
        await restored.ConnectAsync("127.0.0.1", restarted.Port, Hello());
        Assert(restored.CurrentPolicy == new SpeedPolicy(false, 7f, 50f, revision), "Restart lost complete policy or revision.");
        Assert(restarted.LockedGameVersion == "1.7.4910" && restarted.LockedModHash == "test-hash", "Restart lost compatibility locks.");
    });

    await Check("last-message heartbeat timeout closes a joined silent peer", () => heartbeatVerification);

    await Check("shutdown closes incomplete reads and saturated snapshot writes", async () =>
    {
        using var slow = new TcpClient { ReceiveBufferSize = 1024 };
        await slow.ConnectAsync(IPAddress.Loopback, port);
        await WriteFrame(slow.GetStream(), JsonSerializer.Serialize(new { type = "Hello", protocolVersion = 1, hello = Hello() }));
        await ReadFrame(slow.GetStream());
        var latest = (await server.Snapshots.GetLatestAsync())!;
        for (var index = 1; index <= 64; index++)
            await WriteFrame(slow.GetStream(), JsonSerializer.Serialize(new
            { type = "GetSnapshotChunk", protocolVersion = 1, requestId = index, snapshotId = latest.Id, chunkIndex = 0 }));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await slow.GetStream().ReadExactlyAsync(new byte[1], deadline.Token);
        await Task.Delay(100);
        await client.ConnectAsync("127.0.0.1", port, Hello());
        await client.SetSpeedAsync(true, 9f, 80f).WaitAsync(TimeSpan.FromSeconds(3));
        var peers = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var raw = new TcpClient();
                await raw.ConnectAsync(IPAddress.Loopback, port);
                await raw.GetStream().WriteAsync(new byte[] { 5 });
                peers.Add(raw);
            }
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    });

    Console.WriteLine($"PASS: {passed} independent networking verification groups.");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

async Task Check(string title, Func<Task> body)
{
    await body().WaitAsync(TimeSpan.FromSeconds(30));
    passed++;
    Console.WriteLine("PASS " + title);
}
static ClientHello Hello() => new("Verifier", "1.7.4910", "test-hash");
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T exception) { return exception; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task Observe(Task task)
{
    try { await task; }
    catch (Exception ex) when (ex is NetworkException or OperationCanceledException or ObjectDisposedException) { }
}
static async Task Until(Func<bool> predicate)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) await Task.Delay(10, deadline.Token);
}
static async Task VerifyHeartbeat(int port)
{
    using var peer = new TcpClient();
    await peer.ConnectAsync(IPAddress.Loopback, port);
    await WriteFrame(peer.GetStream(), JsonSerializer.Serialize(new { type = "Hello", protocolVersion = 1, hello = Hello() }));
    await ReadFrame(peer.GetStream());
    var started = Environment.TickCount64;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(28));
    while (true)
    {
        var header = new byte[4];
        var count = await peer.GetStream().ReadAsync(header, timeout.Token);
        if (count == 0) break;
        await peer.GetStream().ReadExactlyAsync(header.AsMemory(count), timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert(length > 0 && length < NetworkProtocol.MaximumFrameLength, "Invalid heartbeat framing.");
        await peer.GetStream().ReadExactlyAsync(new byte[length], timeout.Token);
        // Read pings without replying, so only the server's last-message timeout can close this peer.
    }
    Assert(Environment.TickCount64 - started >= 18_000, "The joined peer closed before the heartbeat timeout.");
}
static async Task WriteFrame(NetworkStream stream, string json)
{
    var body = Encoding.UTF8.GetBytes(json);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
    await stream.WriteAsync(header);
    await stream.WriteAsync(body);
}
static async Task<JsonElement> ReadFrame(NetworkStream stream)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var header = new byte[4];
    await stream.ReadExactlyAsync(header, timeout.Token);
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    Assert(length > 0 && length <= NetworkProtocol.MaximumFrameLength, "Invalid response framing.");
    var body = new byte[length];
    await stream.ReadExactlyAsync(body, timeout.Token);
    return JsonSerializer.Deserialize<JsonElement>(body);
}
static async Task Eof(NetworkStream stream)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var count = await stream.ReadAsync(new byte[1], timeout.Token);
    Assert(count == 0, "Server did not close the invalid connection.");
}
