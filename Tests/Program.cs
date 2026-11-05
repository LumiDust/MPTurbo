using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using TurboTick.Networking;
using TurboTick.Simulation;

var passed = 0;
var failed = 0;
var temp = Path.Combine(Path.GetTempPath(), "turbotick-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
string Dir(string name) { var p = Path.Combine(temp, name); Directory.CreateDirectory(p); return p; }
ClientHello Hello(string name = "A", string hash = "mod-a", string version = "1.7.4998") => new(name, version, hash);

async Task Test(string name, Func<Task> body)
{
    try { await body().WaitAsync(TimeSpan.FromSeconds(25)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Until(Func<bool> condition, int seconds = 5)
{
    using var c = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    while (!condition()) await Task.Delay(15, c.Token);
}
async Task Throws(Func<Task> body, string? code = null)
{
    try { await body(); }
    catch (Exception ex)
    {
        if (code != null && (ex is not NetworkException ne || ne.Code != code))
            throw new Exception("Expected " + code + ", got " + ex, ex);
        return;
    }
    throw new Exception("Expected operation to fail.");
}

await Test("single-player defaults, authoritative enable+value+cap, no local fallback", () =>
{
    var s = new SpeedAuthority();
    Check(s.Scale(3) == 6, "rate sync is on by default (2x of the current speed)");
    s.SetLocal(true, 2, 60);
    Check(s.Scale(3) == 6, "local multiplier");
    s.BeginSession();
    Check(s.Scale(3) == 0, "handshake in progress must hold");
    s.ApplyServer(new(true, 4, 12, 7));
    Check(s.Scale(6) == 12, "server cap must override local preferences");
    Check(s.Scale(6, true) == 6, "preserve forced normal semantics");
    Check(s.Scale(0) == 0, "preserve pause");
    Check(!s.ApplyServer(new(true, 2, 60, 6)), "ignore old revision");
    s.ConnectionLost();
    Check(s.Scale(3) == 0, "disconnect must not silently apply local settings");
    s.LeaveSession();
    Check(s.Scale(3) == 6, "explicit leave restores local settings");
    return Task.CompletedTask;
});

await Test("actual TPS uses completed ticks, respects pause and counter reset", () =>
{
    var t = new TickRateMeter();
    t.Sample(100, 0, 3);
    Check(!t.Behind && !t.HasStableSample, "no false lag alert before first measurement window");
    t.Sample(160, 1, 3);
    Check(t.ActualTps == 60 && t.TargetTps == 180 && t.Behind, "ticks/time measurement");
    t.Sample(160, 2, 0);
    Check(t.ActualTps == 0 && t.TargetTps == 0 && !t.Behind, "pause");
    t.Sample(0, 3, 1);
    Check(t.ActualTps == 0, "new save resets counters");
    t.Sample(60, 4, 1);
    t.Sample(61, 4.02, 3);
    Check(!t.Behind && !t.HasStableSample, "changing speed starts a fresh measurement window");
    t.Sample(241, 5.05, 3);
    Check(t.HasStableSample && !t.Behind, "stable higher-speed measurement");
    return Task.CompletedTask;
});

await Test("policy validates non-finite and bounds", async () =>
{
    await Throws(() => Task.FromResult(new SpeedPolicy(true, float.NaN)));
    await Throws(() => Task.FromResult(new SpeedPolicy(true, 21)));
    await Throws(() => Task.FromResult(new SpeedPolicy(true, 2, float.PositiveInfinity)));
});

await Test("two clients: one changes, everyone receives same authoritative revision", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("two")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CurrentPolicy?.Enabled == true && b.CurrentPolicy?.Enabled == true, "rate sync is enabled for every peer by default");
    await a.SetSpeedAsync(true, 3, 90);
    await Until(() => a.CurrentPolicy?.Revision == 1 && b.CurrentPolicy?.Revision == 1);
    Check(a.CurrentPolicy == b.CurrentPolicy && b.CurrentPolicy == server.CurrentPolicy, "policy content identical");
    Check(b.CurrentPolicy?.Enabled == true && b.CurrentPolicy.Multiplier == 3 && b.CurrentPolicy.MaxRate == 90, "all three settings synced");
    await b.SetSpeedAsync(false, 5, 80);
    await Until(() => a.CurrentPolicy?.Revision == 2);
    Check(a.CurrentPolicy == server.CurrentPolicy && !a.CurrentPolicy!.Enabled, "client B can also control");
});

await Test("concurrent writes are serialized; broadcasts never go backwards", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("parallel")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var revisions = new List<long>();
    b.PolicyChanged += p => { lock (revisions) revisions.Add(p.Revision); };
    await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? a : b).SetSpeedAsync(true, 1 + i, 60)));
    await Until(() => b.CurrentPolicy?.Revision == 12 && a.CurrentPolicy?.Revision == 12);
    Check(a.CurrentPolicy == b.CurrentPolicy, "all clients converge");
    lock (revisions) Check(revisions.SequenceEqual(revisions.Order()), "monotonic ordered broadcast");
});

await Test("compatibility locks exact build, mod hash and protocol client version", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("compat")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await using var wrongMods = new DedicatedClient();
    await Throws(() => wrongMods.ConnectAsync("127.0.0.1", server.Port, Hello("B", "mod-b")));
    Check(!wrongMods.IsConnected, "mismatch must not appear connected");
    await using var wrongBuild = new DedicatedClient();
    await Throws(() => wrongBuild.ConnectAsync("127.0.0.1", server.Port, Hello("B", version: "1.7.4999")));
    await using var oldGame = new DedicatedClient();
    await Throws(() => oldGame.ConnectAsync("127.0.0.1", server.Port, Hello("B", version: "1.6.4850")));
    await using var wrongClient = new DedicatedClient();
    await Throws(() => wrongClient.ConnectAsync("127.0.0.1", server.Port, Hello("B") with { ClientVersion = "0.0.1" }));
    Check(a.IsConnected, "healthy peer survives failed handshakes");
});

await Test("controller-only permission is enforced by the server", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("permission"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    Check(a.CanEdit && !b.CanEdit, "server assigns roles");
    await Throws(() => b.SetSpeedAsync(true, 4, 60));
    Check(server.CurrentPolicy.Revision == 0, "blocked command changes nothing");
    await a.SetSpeedAsync(true, 4, 60);
    await Until(() => b.CurrentPolicy?.Revision == 1);
});

await Test("reconnect and server restart restore policy instead of local defaults", async () =>
{
    var path = Dir("persistence");
    var server = new DedicatedServer(new(Port: 0, DataDirectory: path));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await a.SetSpeedAsync(true, 4, 85);
    var previous = server.CurrentPolicy;
    await a.DisconnectAsync();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(a.CurrentPolicy == previous, "same instance reconnects with latest policy");
    await a.DisconnectAsync(); await server.DisposeAsync();
    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: path));
    await restarted.StartAsync();
    await a.ConnectAsync("127.0.0.1", restarted.Port, Hello());
    Check(a.CurrentPolicy == previous, "disk-backed policy survives restart");
    await a.SetSpeedAsync(false, 2, 60);
    Check(restarted.CurrentPolicy.Revision == previous.Revision + 1, "revision survives restart");
});

await Test("oversized malformed frame is bounded; other peers still connect", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("frames")));
    await server.StartAsync();
    using var rogue = new TcpClient(); await rogue.ConnectAsync("127.0.0.1", server.Port);
    var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
    await rogue.GetStream().WriteAsync(header);
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    Check(a.IsConnected, "malformed socket must not kill listener");
});

await Test("snapshot dedup, bounds and path validation", async () =>
{
    var store = new SnapshotStore(Dir("store"));
    var bytes = new byte[700_000]; new Random(7).NextBytes(bytes);
    var a = await store.PutAsync(new MemoryStream(bytes));
    var b = await store.PutAsync(new MemoryStream(bytes));
    Check(a.Id == b.Id && a.ChunkHashes.SequenceEqual(b.ChunkHashes) && a.ChunkHashes.Count == 3, "content addressed chunks");
    var chunk = await store.ReadChunkAsync(a.Id, 0);
    Check(chunk.Data.SequenceEqual(bytes.Take(a.ChunkSize)) && chunk.Sha256 == a.ChunkHashes[0], "chunk hash");
    await Throws(() => store.ReadChunkAsync("../outside", 0));
    await Throws(() => store.ReadChunkAsync(a.Id, -1));
    await Throws(() => store.ReadChunkAsync(a.Id, 3));
    var empty = await store.PutAsync(new MemoryStream());
    Check(empty.Length == 0 && empty.ChunkHashes.Count == 0, "empty snapshot defined");
});

await Test("snapshot cancel then resume; final digest matches; cache hit avoids transfer", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("download")));
    await server.StartAsync();
    var bytes = new byte[1_100_000]; new Random(17).NextBytes(bytes);
    var manifest = await server.Snapshots.PutAsync(new MemoryStream(bytes));
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    var received = await client.GetSnapshotManifestAsync();
    Check(received?.Id == manifest.Id, "manifest comes from independent server");
    var destination = Path.Combine(Dir("client-cache"), "save.snapshot");
    using var cancel = new CancellationTokenSource();
    await Throws(() => client.DownloadSnapshotAsync(manifest, destination, (done, total) =>
    { if (done >= manifest.ChunkSize) cancel.Cancel(); }, cancel.Token));
    Check(!File.Exists(destination), "partial must never masquerade as complete snapshot");
    await client.DownloadSnapshotAsync(manifest, destination);
    var saved = await File.ReadAllBytesAsync(destination);
    Check(saved.SequenceEqual(bytes), "resumed bytes identical");
    Check(Convert.ToHexStringLower(SHA256.HashData(saved)) == manifest.Id, "whole-file SHA256");
    // Make every remote chunk request fail. A completed valid cache must be reused without transfer.
    File.Delete(Path.Combine(Dir("download"), "Snapshots", "objects", manifest.Id + ".blob"));
    await client.DownloadSnapshotAsync(manifest, destination);
    Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(bytes), "verified cache hit");
});

await Test("corrupt server snapshot is a request error, not a room disconnect", async () =>
{
    var data = Dir("corrupt-server");
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await server.StartAsync();
    var manifest = await server.Snapshots.PutAsync(new MemoryStream(new byte[300_000]));
    var blob = Path.Combine(data, "Snapshots", "objects", manifest.Id + ".blob");
    await using (var file = new FileStream(blob, FileMode.Open, FileAccess.Write)) { file.WriteByte(123); }
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", server.Port, Hello());
    await Throws(() => client.DownloadSnapshotAsync(manifest, Path.Combine(Dir("corrupt-cache"), "save.snapshot")), "InvalidSnapshotChunk");
    Check(client.IsConnected, "a corrupt snapshot must not disconnect a healthy peer");
    await client.SetSpeedAsync(true, 3, 60);
    Check(client.CurrentPolicy?.Multiplier == 3, "settings channel remains usable");
});

await Test("a queued edit from server A must never mutate server B after reconnect", async () =>
{
    await using var a = new DedicatedServer(new(Port: 0, DataDirectory: Dir("old-room")));
    await using var b = new DedicatedServer(new(Port: 0, DataDirectory: Dir("new-room")));
    await a.StartAsync(); await b.StartAsync();
    await using var client = new DedicatedClient();
    await client.ConnectAsync("127.0.0.1", a.Port, Hello());
    // Deterministic scheduler barrier: hold the existing edit queue like a slow in-flight operation.
    var gate = (SemaphoreSlim)typeof(DedicatedClient).GetField("_speedGate",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
    await gate.WaitAsync();
    Task pending;
    try
    {
        pending = client.SetSpeedAsync(true, 9, 100);
        await client.DisconnectAsync();
        await client.ConnectAsync("127.0.0.1", b.Port, Hello());
    }
    finally { gate.Release(); }
    await Throws(() => pending);
    Check(b.CurrentPolicy.Revision == 0 && b.CurrentPolicy.Multiplier == 2f, "stale edit leaked across rooms");
    await client.SetSpeedAsync(true, 3, 60);
    Check(b.CurrentPolicy.Multiplier == 3, "new-session edits still work");
});

await Test("queued notifications from server A cannot poison server B policy after reconnect", async () =>
{
    await using var a = new DedicatedServer(new(Port: 0, DataDirectory: Dir("event-old")));
    await using var b = new DedicatedServer(new(Port: 0, DataDirectory: Dir("event-new")));
    await a.StartAsync(); await b.StartAsync();
    await using var client = new DedicatedClient();
    using var blocked = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    int first = 0;
    client.StatusChanged += text =>
    {
        if (text == "Connecting" && Interlocked.CompareExchange(ref first, 1, 0) == 0)
        {
            blocked.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }
    };
    var authority = new SpeedAuthority();
    var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.PolicyChanged += policy =>
    {
        authority.ApplyServer(policy);
        if (policy == b.CurrentPolicy) delivered.TrySetResult();
    };
    try
    {
        await client.ConnectAsync("127.0.0.1", a.Port, Hello());
        await Until(() => blocked.IsSet);
        await client.SetSpeedAsync(true, 7, 100);
        await client.DisconnectAsync();
        await client.ConnectAsync("127.0.0.1", b.Port, Hello());
        authority.BeginSession();
        release.Set();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(authority.Current == b.CurrentPolicy, "old revision from room A poisoned room B subscriber state");
    }
    finally { release.Set(); }
});

await Test("command log applies in server order, rejects duplicates and ignores the future", () =>
{
    var log = new CommandLog();
    byte[] p = [1];
    Check(log.Accept(new(1, 10, "designation", p)), "accept #1");
    Check(log.Accept(new(2, 5, "designation", p)), "accept #2 with an earlier tick");
    Check(!log.Accept(new(2, 11, "designation", p)), "duplicate sequence");
    Check(log.TakeThrough(4).Count == 0, "nothing before its tick");
    var due = log.TakeThrough(10);
    Check(due.Count == 2 && due[0].Sequence == 2 && due[1].Sequence == 1, "ordered by tick, then sequence");
    Check(log.Accept(new(1, 12, "designation", p)) == false, "regressed sequence rejected");
    return Task.CompletedTask;
});

await Test("tick barrier never runs ahead of the server and ignores stale publishers", () =>
{
    var barrier = new TickBarrier();
    barrier.Publish(5); barrier.Publish(3);
    Check(barrier.ServerTick == 5 && barrier.Backlog == 5, "monotonic server tick");
    for (var i = 0; i < 5; i++) Check(barrier.TryAdvance(), "advance");
    Check(!barrier.TryAdvance() && barrier.Backlog == 0, "cannot pass the server");
    barrier.Publish(7);
    Check(barrier.Backlog == 2, "new ticks reopen the window");
    return Task.CompletedTask;
});

await Test("shared colony: controller publishes world, starts, and both clients receive commands", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("shared")));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    Check(controller.CanEdit && guest.CanEdit, "both peers may edit in the default room");
    var world = new byte[300_000]; new Random(3).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await controller.UploadWorldAsync(manifest, new MemoryStream(world));
    Check(server.WorldManifest?.Id == manifest.Id, "server stores the shared world");
    Check((await guest.GetWorldManifestAsync())?.Id == manifest.Id, "guests see the same world id");
    var started = new TaskCompletionSource<long>();
    guest.WorldStarted += (session, _) => started.TrySetResult(session.Epoch);
    await controller.StartGameAsync();
    Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)) >= 0, "guest receives start");
    var received = new TaskCompletionSource<List<GameCommand>>();
    guest.CommandsReceived += commands => received.TrySetResult(commands.ToList());
    await controller.SendCommandAsync("designation", "Mine|0|10|20"u8.ToArray());
    var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(batch.Count == 1 && batch[0].Sequence == 1 && batch[0].Kind == "designation", "command relayed in order");
    Check(batch[0].Tick >= 0 && batch[0].Payload.Length > 0, "server assigned tick and kept payload");
    Check(server.Tick >= 0 && server.GameRunning, "server drives a shared tick clock");
});

await Test("controller-only room rejects world publishing by a guest", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("shared-owner"), EveryoneCanEdit: false));
    await server.StartAsync();
    await using var controller = new DedicatedClient();
    await using var guest = new DedicatedClient();
    await controller.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    Check(controller.CanEdit && !guest.CanEdit, "only the assigned controller may publish");
    var world = new byte[2048]; new Random(5).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await Throws(() => guest.UploadWorldAsync(manifest, new MemoryStream(world)), "ControllerOnly");
    await controller.UploadWorldAsync(manifest, new MemoryStream(world));
    Check(server.WorldManifest?.Id == manifest.Id, "controller publishes the shared world");
});

await Test("action codec round-trips every player action and rejects malformed payloads", () =>
{
    var design = TurboTick.Simulation.CommandCodec.DesignationCommand("Mine", 0, 12, 34, 7);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDesignation(design.Payload, out var def, out var map, out var x, out var z)
        && def == "Mine" && map == 0 && x == 12 && z == 34, "designation");
    var blueprint = TurboTick.Simulation.CommandCodec.BlueprintCommand("Wall", "WoodLog", 1, 5, 6, 2, 8);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeBlueprint(blueprint.Payload, out var b, out var stuff, out var bm, out var bx, out var bz, out var rot)
        && b == "Wall" && stuff == "WoodLog" && bm == 1 && bx == 5 && bz == 6 && rot == 2, "blueprint");
    var draft = TurboTick.Simulation.CommandCodec.DraftCommand(42, true, 9);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeDraft(draft.Payload, out var pawn, out var on) && pawn == 42 && on, "draft");
    var order = TurboTick.Simulation.CommandCodec.OrderCommand(42, "HaulToCell", 1000, -1, -1, 77, 10, 11);
    Check(TurboTick.Simulation.CommandCodec.TryDecodeOrder(order.Payload, out var op, out var job, out var ac, out var at, out var bc, out var bt, out var count)
        && op == 42 && job == "HaulToCell" && ac == 1000 && at == -1 && bt == 77 && count == 10, "order");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeDesignation("broken"u8.ToArray(), out _, out _, out _, out _), "reject short payload");
    Check(!TurboTick.Simulation.CommandCodec.TryDecodeDraft("1|2"u8.ToArray(), out _, out _), "reject invalid flag");
    return Task.CompletedTask;
});

await Test("state hash is order independent and changes with simulated state", () =>
{
    string[] a = ["t:1:0:5:5:1:100", "p:2:0:7:8:0.75:Haul"];
    string[] b = ["p:2:0:7:8:0.75:Haul", "t:1:0:5:5:1:100"];
    Check(TurboTick.Simulation.StateHash.Compute(a) == TurboTick.Simulation.StateHash.Compute(b), "order independent");
    Check(TurboTick.Simulation.StateHash.Compute(a) != TurboTick.Simulation.StateHash.Compute(["t:1:0:5:5:1:99", "p:2:0:7:8:0.75:Haul"]), "detects change");
    Check(TurboTick.Simulation.StateHash.Compute(["p:2:0:7:8:0.75:Haul"]).Length == 64, "sha256 hex");
    return Task.CompletedTask;
});

await Test("server reports a desync when two peers disagree on the same tick", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("hash")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var world = new byte[4096]; new Random(9).NextBytes(world);
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    var desync = new TaskCompletionSource<int>();
    b.DesyncDetected += (tick, _) => desync.TrySetResult(tick);
    var same = new string('a', 64);
    await a.ReportHashAsync(60, same);
    await b.ReportHashAsync(60, same);
    Check(!desync.Task.IsCompleted, "matching hashes are silent");
    await b.ReportHashAsync(120, new string('b', 64));
    await a.ReportHashAsync(120, new string('c', 64));
    Check(await desync.Task.WaitAsync(TimeSpan.FromSeconds(5)) == 120, "mismatch reported with the tick");
});

await Test("a peer joining mid-session receives the epoch, tick and full command history", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("catchup")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    var world = new byte[8192]; new Random(11).NextBytes(world);
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await using var late = new DedicatedClient();
    var started = new TaskCompletionSource<int>();
    var replay = new List<GameCommand>();
    late.WorldStarted += (session, _) => started.TrySetResult(session.Tick);
    late.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    Check(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)) >= 0, "late peer receives the running epoch");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "designation", "history replayed to the late peer");
});

await Test("session seed reaches every peer and survives a server restart with its history", async () =>
{
    var data = Dir("session-persist");
    var server = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    var seeds = new List<int>();
    host.WorldStarted += (session, _) => seeds.Add(session.Seed);
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    var world = new byte[4096]; new Random(13).NextBytes(world);
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await Until(() => seeds.Count == 1);
    await host.SendCommandAsync("designation", "Mine|0|2|3"u8.ToArray());
    await host.SendCommandAsync("draft", "5|1"u8.ToArray());
    await Until(() => server.HistoryCount == 2);
    var epoch = server.Epoch;
    var seed = seeds[0];
    await host.DisconnectAsync();
    await server.StopAsync();
    await server.DisposeAsync();

    await using var restarted = new DedicatedServer(new(Port: 0, DataDirectory: data));
    await restarted.StartAsync();
    Check(restarted.GameRunning && restarted.Epoch == epoch, "epoch resumes after restart");
    Check(restarted.Session?.Seed == seed, "seed resumes after restart");
    Check(restarted.HistoryCount == 2, "command history resumes after restart");
    await using var rejoin = new DedicatedClient();
    var replay = new List<GameCommand>();
    rejoin.WorldStarted += (_, _) => { };
    rejoin.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await rejoin.ConnectAsync("127.0.0.1", restarted.Port, Hello("rejoin"));
    await Until(() => { lock (replay) return replay.Count == 2; });
    Check(replay.Select(c => c.Kind).SequenceEqual(new[] { "designation", "draft" }), "history replays in order");
});

await Test("two peers replaying the same ordered commands derive the same state hash", () =>
{
    static List<string> Run(IEnumerable<GameCommand> commands)
    {
        // Minimal deterministic model: commands place ids at positions; ordering must define the result.
        var state = new SortedDictionary<int, string>();
        var log = new CommandLog();
        foreach (var command in commands) log.Accept(command);
        foreach (var command in log.TakeThrough(1000))
        {
            var parts = System.Text.Encoding.UTF8.GetString(command.Payload).Split('|');
            state[int.Parse(parts[0])] = $"{parts[1]}|{parts[2]}";
        }
        return state.Select(pair => $"{pair.Key}:{pair.Value}").ToList();
    }
    var commands = new[]
    {
        new GameCommand(1, 5, "designation", "3|Mine|0"u8.ToArray()),
        new GameCommand(2, 2, "designation", "1|Haul|0"u8.ToArray()),
        new GameCommand(3, 9, "designation", "2|Cut|0"u8.ToArray())
    };
    var first = Run(commands);

    // Second peer receives the same sequence but drains the log tick by tick instead of all at once.
    var incremental = new CommandLog();
    foreach (var command in commands) incremental.Accept(command);
    var second = new SortedDictionary<int, string>();
    for (var tick = 0; tick <= 9; tick++)
        foreach (var command in incremental.TakeThrough(tick))
        {
            var parts = System.Text.Encoding.UTF8.GetString(command.Payload).Split('|');
            second[int.Parse(parts[0])] = $"{parts[1]}|{parts[2]}";
        }
    Check(TurboTick.Simulation.StateHash.Compute(first) ==
          TurboTick.Simulation.StateHash.Compute(second.Select(pair => $"{pair.Key}:{pair.Value}").ToList()),
        "incremental and bulk draining converge");
    return Task.CompletedTask;
});

await Test("after a desync the controller can restart the colony with a fresh epoch", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("recover")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(21).NextBytes(world);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(world);
    await host.UploadWorldAsync(manifest, new MemoryStream(world));
    var epochs = new List<long>();
    guest.WorldStarted += (session, _) => { lock (epochs) epochs.Add(session.Epoch); };
    await host.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 1; });
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    // Restart resets history and issues a new epoch; the old commands must not leak into it.
    await host.StartGameAsync();
    await Until(() => { lock (epochs) return epochs.Count == 2; });
    Check(epochs[1] > epochs[0], "restart issues a new epoch");
    Check(server.HistoryCount == 0, "old commands are cleared");
});

await Test("a checkpoint trims history so a late join replays only the delta", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("checkpoint")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    Check(host.IsCheckpointOwner, "first peer owns checkpoints");
    var world = new byte[4096]; new Random(31).NextBytes(world);
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    for (var i = 0; i < 3; i++)
        await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i}|{i}"));
    await Until(() => server.HistoryCount == 3);
    await Until(() => server.Tick >= 4);
    var tick = server.Tick;
    var checkpoint = new byte[8192]; new Random(32).NextBytes(checkpoint);
    var manifest = TurboTick.Networking.SnapshotBuilder.Describe(checkpoint);
    await host.CheckpointWorldAsync(manifest, new MemoryStream(checkpoint), tick);
    Check(server.CheckpointTick == tick, "checkpoint tick recorded");
    Check(server.HistoryCount == 0, "commands before the checkpoint are dropped");
    await host.SendCommandAsync("draft", "7|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await using var late = new DedicatedClient();
    var started = new TaskCompletionSource<SessionInfo>();
    var replay = new List<GameCommand>();
    late.WorldStarted += (session, _) => started.TrySetResult(session);
    late.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await late.ConnectAsync("127.0.0.1", server.Port, Hello("late"));
    var info = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(info.SnapshotTick == tick, "late peer starts from the checkpoint tick");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "draft", "only the delta replays");
});

await Test("server rejects unknown command kinds and throttles a flooding client", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("guard")));
    await server.StartAsync();
    await using var host = new DedicatedClient();
    var errors = new List<string>();
    host.StatusChanged += _ => { };
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    var world = new byte[2048]; new Random(41).NextBytes(world);
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("invented", "x"u8.ToArray());
    await Until(() => server.HistoryCount == 0);
    Check(server.HistoryCount == 0, "unknown kind never reaches history");
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    Check(server.HistoryCount == 1, "known kind accepted");
    var accepted = 1;
    for (var i = 0; i < 500; i++)
        await host.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{i % 40}|{i % 40}"));
    await Task.Delay(1500);
    accepted = server.HistoryCount;
    Check(accepted is > 1 and < 501, $"flood is bounded, stored {accepted}");
    await host.SendCommandAsync("designation", "Mine|0|9|9"u8.ToArray());
    await Until(() => server.HistoryCount > accepted);
    Check(server.HistoryCount > accepted, "client recovers after the window");
});

await Test("desync detail names the category that diverged", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("cat")));
    await server.StartAsync();
    await using var a = new DedicatedClient(); await using var b = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    await b.ConnectAsync("127.0.0.1", server.Port, Hello("B"));
    var world = new byte[2048]; new Random(51).NextBytes(world);
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    var detail = new TaskCompletionSource<string>();
    b.DesyncDetected += (_, text) => detail.TrySetResult(text);
    var pawns = new string('1', 64);
    var things = new string('2', 64);
    var designations = new string('3', 64);
    await a.ReportHashAsync(60, new string('a', 64), [pawns, things, designations]);
    var otherDesignations = new string('4', 64);
    await b.ReportHashAsync(60, new string('b', 64), [pawns, things, otherDesignations]);
    Check(await detail.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "designations", "only the differing category is named");
});

await Test("a dropped peer reconnects to the checkpoint, replays the delta and can act again", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("recover-session")));
    await server.StartAsync();
    await using var host = new DedicatedClient(); await using var guest = new DedicatedClient();
    await host.ConnectAsync("127.0.0.1", server.Port, Hello("host"));
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var world = new byte[4096]; new Random(61).NextBytes(world);
    await host.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await host.StartGameAsync();
    await host.SendCommandAsync("designation", "Mine|0|1|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);
    await Until(() => server.Tick >= 4);
    var checkpoint = new byte[4096]; new Random(62).NextBytes(checkpoint);
    await host.CheckpointWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(checkpoint), new MemoryStream(checkpoint), server.Tick);
    await host.SendCommandAsync("draft", "9|1"u8.ToArray());
    await Until(() => server.HistoryCount == 1);

    await guest.DisconnectAsync();
    var replay = new List<GameCommand>();
    var info = new TaskCompletionSource<SessionInfo>();
    guest.WorldStarted += (session, _) => info.TrySetResult(session);
    guest.CommandsReceived += commands => { lock (replay) replay.AddRange(commands); };
    await guest.ConnectAsync("127.0.0.1", server.Port, Hello("guest"));
    var resumed = await info.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(resumed.SnapshotTick == server.CheckpointTick, "resumes from the checkpoint");
    await Until(() => { lock (replay) return replay.Count == 1; });
    Check(replay[0].Kind == "draft", "only the post-checkpoint delta replays");
    await guest.SendCommandAsync("designation", "Mine|0|5|5"u8.ToArray());
    await Until(() => server.HistoryCount == 2);
    Check(server.HistoryCount == 2, "reconnected peer can act again");
});

await Test("five peers submitting concurrently yield one total order with no loss, and history stays bounded", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("stress")));
    await server.StartAsync();
    var clients = new List<DedicatedClient>();
    try
    {
        for (var i = 0; i < 5; i++)
        {
            var client = new DedicatedClient();
            clients.Add(client);
            await client.ConnectAsync("127.0.0.1", server.Port, Hello("p" + i));
        }
        var world = new byte[8192]; new Random(71).NextBytes(world);
        await clients[0].UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
        var received = new List<GameCommand>();
        clients[1].CommandsReceived += commands => { lock (received) received.AddRange(commands); };
        await clients[0].StartGameAsync();
        await Task.WhenAll(clients.Select((client, index) => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
                await client.SendCommandAsync("designation", System.Text.Encoding.UTF8.GetBytes($"Mine|0|{index}|{i}"));
        })));
        await Until(() => server.HistoryCount == 500, 20);
        Check(server.HistoryCount == 500, "all 500 commands stored");
        // Broadcasts trail the server-side commit; wait for delivery before judging the order.
        await Until(() => { lock (received) return received.Count == 500; }, 20);
        List<GameCommand> ordered;
        lock (received) ordered = [.. received];
        Check(ordered.Count == 500, "every command delivered");
        Check(ordered.Select(c => c.Sequence).Distinct().Count() == 500, "no duplicate sequence");
        Check(ordered.Select(c => c.Sequence).SequenceEqual(ordered.Select(c => c.Sequence).Order()), "single total order");
        Check(ordered.All(c => c.Tick >= 0), "every command has a tick");
        // Commands are scheduled a couple of ticks ahead; let them fall into the past before checking.
        await Task.Delay(250);
        await clients[0].CheckpointWorldAsync(
            TurboTick.Networking.SnapshotBuilder.Describe(new byte[4096]), new MemoryStream(new byte[4096]), server.Tick);
        Check(server.HistoryCount == 0, "checkpoint bounds history");
    }
    finally
    {
        foreach (var client in clients) await client.DisposeAsync();
    }
});

await Test("comparison hashes stay inside a bounded window during a long session", async () =>
{
    await using var server = new DedicatedServer(new(Port: 0, DataDirectory: Dir("window")));
    await server.StartAsync();
    await using var a = new DedicatedClient();
    await a.ConnectAsync("127.0.0.1", server.Port, Hello());
    var world = new byte[2048]; new Random(81).NextBytes(world);
    await a.UploadWorldAsync(TurboTick.Networking.SnapshotBuilder.Describe(world), new MemoryStream(world));
    await a.StartGameAsync();
    for (var tick = 60; tick <= 6000; tick += 60) await a.ReportHashAsync(tick, new string('a', 64));
    await Until(() => server.TrackedHashTicks > 0);
    Check(server.TrackedHashTicks <= 601, $"hash window bounded, tracked {server.TrackedHashTicks}");
    Check(server.TrackedHashTicks >= 5, "recent history is still available for comparison");
});

await Test("1.7 package: own assemblies only, zero reference-mod dependencies, complete translations", () =>
{
    PackageValidation.Validate();
    return Task.CompletedTask;
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
try { Directory.Delete(temp, true); } catch { /* preserve failure artifacts if OS still holds a handle */ }
Environment.ExitCode = failed == 0 ? 0 : 1;
