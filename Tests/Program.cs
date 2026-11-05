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
async Task Until(Func<bool> condition)
{
    using var c = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
    guest.WorldStarted += (epoch, _, _) => started.TrySetResult(epoch);
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

await Test("1.7 package: own assemblies only, zero reference-mod dependencies, complete translations", () =>
{
    PackageValidation.Validate();
    return Task.CompletedTask;
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
try { Directory.Delete(temp, true); } catch { /* preserve failure artifacts if OS still holds a handle */ }
Environment.ExitCode = failed == 0 ? 0 : 1;
