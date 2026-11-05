using System.Collections.Concurrent;
using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Own network implementation; socket callbacks only enqueue work for the game thread.</summary>
internal static class RoomConnection
{
    private static readonly ConcurrentQueue<Action> gameActions = new();
    private static readonly List<Task> operations = [];
    private static DedicatedClient? client;
    private static CancellationTokenSource? lifetime;
    private static int generation;
    public static string Status { get; private set; } = "未连接";
    public static string Fingerprint { get; private set; } = "尚未生成";
    public static string SnapshotStatus { get; private set; } = "尚未下载；此阶段只缓存快照，不自动载入游戏";
    public static bool Busy { get; private set; }
    public static bool CanEdit => !TurboState.Authority.SessionRequested
        || (TurboState.Authority.SessionConnected && client?.CanEdit == true);

    public static void Connect(TurboTickSettings settings)
    {
        // Must run on the game thread before starting the background hash/connection operation.
        ManifestInputs inputs;
        try { inputs = CompatibilityManifest.Capture(); }
        catch (Exception error) { Status = error.Message; return; }
        var host = settings.ServerHost.Trim();
        if (!int.TryParse(settings.ServerPort, out var port) || port is < 1 or > 65535)
        {
            Status = "端口须在 1..65535";
            return;
        }
        var name = settings.PlayerName.Trim();
        if (name.Length is < 1 or > 32) { Status = "玩家名称须为 1..32 字符"; return; }
        Leave();
        var id = ++generation;
        var cancel = lifetime = new CancellationTokenSource();
        var socket = client = new DedicatedClient();
        TurboState.Authority.BeginSession();
        Status = "计算兼容性指纹…（程序集、Def/Patch、模组顺序与配置）";
        Busy = true;
        var exportFile = Path.Combine(GenFilePaths.ConfigFolderPath, "TurboTick", "compatibility-manifest.json");
        socket.PolicyChanged += policy => Post(id, () =>
        {
            TurboState.Authority.ApplyServer(policy);
            Status = "房间已连接 · 服务端设置修订 " + policy.Revision;
        });
        socket.StatusChanged += text => Post(id, () =>
        {
            Status = text;
            if (!socket.IsConnected) TurboState.Authority.ConnectionLost();
        });
        socket.WorldStarted += (session, manifest) => Post(id, () =>
        {
            if (manifest.Id == publishedWorldId)
            {
                // The controller just uploaded this save; its running game already is that world.
                SharedColony.OnWorldStarted(session, manifest);
                SharedColony.OnWorldLoaded();
                Status = "共享殖民地已开始（本机为来源存档）";
                return;
            }
            Status = "共享殖民地已开始，正在同步存档…";
            SharedColony.OnWorldStarted(session, manifest);
            DownloadAndLoad(manifest, id);
        });
        socket.Ticked += tick => Post(id, () => SharedColony.OnTick(tick));
        socket.CommandsReceived += commands => Post(id, () => SharedColony.OnCommands(commands));
        socket.DesyncDetected += (tick, detail) => Post(id, () => SharedColony.OnDesync(tick, detail));
        Track(Task.Run(async () =>
        {
            try
            {
                var hello = CompatibilityManifest.Build(inputs, name, exportFile, cancel.Token);
                Post(id, () => { Fingerprint = hello.ModHash; Status = "指纹已生成，连接服务器…"; });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(host, port, hello, timeout.Token).ConfigureAwait(false);
                Post(id, () =>
                {
                    if (socket.CurrentPolicy != null) TurboState.Authority.ApplyServer(socket.CurrentPolicy);
                    Status = "房间已连接 · 倍率由专用服务器管理（共享殖民地同步开发中）";
                });
            }
            catch (Exception error)
            {
                Post(id, () => { TurboState.Authority.ConnectionLost(); Status = "连接未完成：" + error.Message; });
            }
            finally { Post(id, () => Busy = false); }
        }));
    }

    public static void Request(bool enabled, float multiplier, float maxRate)
    {
        if (!TurboState.Authority.SessionRequested)
        {
            try
            {
                TurboState.Authority.SetLocal(enabled, multiplier, maxRate);
                var s = TurboTickMod.Settings;
                s.enabled = enabled;
                s.multiplier = multiplier;
                s.maxRateCap = maxRate;
            }
            catch (Exception error) { Status = error.Message; }
            return;
        }
        if (client?.IsConnected != true || !CanEdit) { Status = "等待服务端连接/控制权限；本地设置保持不变"; return; }
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        Track(Task.Run(async () =>
        {
            try { await socket.SetSpeedAsync(enabled, multiplier, maxRate, token).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => Status = "设置未确认：" + error.Message); }
        }));
    }

    /// <summary>Controller action: publish the current save as the shared world, then start the session.</summary>
    public static void StartSharedColony()
    {
        if (client?.IsConnected != true) { Status = "先连接服务器"; return; }
        if (!CanEdit) { Status = "只有服务器控制者可以发布并开始共享殖民地"; return; }
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        Status = "正在保存当前殖民地…";
        string path;
        try { path = WorldFiles.SaveCurrentGame(); }
        catch (Exception error) { Status = "保存失败：" + error.Message; return; }
        var bytes = File.ReadAllBytes(path);
        var manifest = WorldFiles.Describe(bytes);
        Status = "正在上传存档（" + bytes.Length / 1024 + " KiB）…";
        Track(Task.Run(async () =>
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                await socket.UploadWorldAsync(manifest, stream, token).ConfigureAwait(false);
                publishedWorldId = manifest.Id;
                await socket.StartGameAsync(token).ConfigureAwait(false);
                Post(id, () => Status = "已发布并开始共享殖民地");
            }
            catch (Exception error) { Post(id, () => Status = "启动共享殖民地失败：" + error.Message); }
        }));
    }

    private static void DownloadAndLoad(SnapshotManifest manifest, int id)
    {
        var socket = client;
        var token = lifetime!.Token;
        var path = WorldFiles.DownloadPath(manifest.Id);
        Track(Task.Run(async () =>
        {
            try
            {
                await socket!.DownloadSnapshotAsync(manifest, path, null, token).ConfigureAwait(false);
                Post(id, () =>
                {
                    if (id != generation) return;
                    try
                    {
                        GameDataSaveLoader.LoadGame(path);
                        SharedColony.OnWorldLoaded();
                        Status = "共享殖民地已载入";
                    }
                    catch (Exception error) { SharedColony.OnWorldLoadFailed(error.Message); Status = "载入共享存档失败：" + error.Message; }
                });
            }
            catch (Exception error) { Post(id, () => Status = "下载共享存档失败：" + error.Message); }
        }));
    }

    public static Task ReportHash(int tick, string sha256, IReadOnlyList<string>? categories = null)
    {
        if (client?.IsConnected != true) return Task.CompletedTask;
        var socket = client;
        var token = lifetime!.Token;
        var id = generation;
        return Task.Run(async () =>
        {
            try { await socket.ReportHashAsync(tick, sha256, categories, token).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => Status = "状态校验上报失败：" + error.Message); }
        });
    }

    public static bool IsCheckpointOwner => client?.IsCheckpointOwner == true;

    /// <summary>Desync recovery: drop the session and join again, which reloads the checkpoint and replays the delta.</summary>
    public static void ReconnectFromFault()
    {
        var settings = TurboTickMod.Settings;
        if (settings == null || string.IsNullOrWhiteSpace(settings.ServerHost)) return;
        SharedColony.Session.Leave();
        Connect(settings);
    }
    private static int checkpointing;
    private static string? publishedWorldId;

    /// <summary>Checkpoint owner: saves the colony at the current tick and publishes it so late joins replay less.</summary>
    public static void PublishCheckpoint(int tick)
    {
        if (client?.IsConnected != true || !IsCheckpointOwner) return;
        if (Interlocked.Exchange(ref checkpointing, 1) != 0) return;
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        string path;
        try { path = WorldFiles.SaveCurrentGame(); }
        catch (Exception error) { Interlocked.Exchange(ref checkpointing, 0); Status = "检查点保存失败：" + error.Message; return; }
        var bytes = File.ReadAllBytes(path);
        var manifest = WorldFiles.Describe(bytes);
        Track(Task.Run(async () =>
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                await socket.CheckpointWorldAsync(manifest, stream, tick, token).ConfigureAwait(false);
            }
            catch (Exception error) { Post(id, () => Status = "检查点上传失败：" + error.Message); }
            finally { Interlocked.Exchange(ref checkpointing, 0); }
        }));
    }

    public static Task SendCommand(string kind, byte[] payload, long sequence)
    {
        if (client?.IsConnected != true || !TurboState.Authority.SessionConnected) return Task.CompletedTask;
        var socket = client;
        var token = lifetime!.Token;
        var id = generation;
        return Task.Run(async () =>
        {
            try { await socket.SendCommandAsync(kind, payload, token).ConfigureAwait(false); }
            catch (Exception error) { Post(id, () => Status = "命令发送失败：" + error.Message); }
        });
    }

    public static void CacheSnapshot()
    {
        if (client?.IsConnected != true) { SnapshotStatus = "先连接服务器"; return; }
        var socket = client;
        var id = generation;
        var token = lifetime!.Token;
        var cache = Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTickCache");
        SnapshotStatus = "请求快照清单…";
        Track(Task.Run(async () =>
        {
            try
            {
                var manifest = await socket.GetSnapshotManifestAsync(token).ConfigureAwait(false);
                if (manifest == null) { Post(id, () => SnapshotStatus = "服务器尚未导入快照"); return; }
                var path = Path.Combine(cache, manifest.Id + ".snapshot");
                await socket.DownloadSnapshotAsync(manifest, path, (done, total) =>
                    Post(id, () => SnapshotStatus = $"缓存 {done / 1024}/{total / 1024} KiB"), token).ConfigureAwait(false);
                Post(id, () => SnapshotStatus = "校验完成，已缓存；未替换当前游戏存档");
            }
            catch (Exception error) { Post(id, () => SnapshotStatus = "快照下载中断，可重试续传：" + error.Message); }
        }));
    }

    public static void Leave()
    {
        ++generation; // Drop all queued actions from an old socket, including late disconnect events.
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        var old = client;
        client = null;
        if (old != null) Track(Task.Run(async () => await old.DisposeAsync().ConfigureAwait(false)));
        TurboState.Authority.LeaveSession();
        Status = "未连接";
        Busy = false;
    }

    public static void Pump()
    {
        // Bound per-frame work so a large snapshot's progress reports cannot stall GUI indefinitely.
        for (int n = 0; n < 128 && gameActions.TryDequeue(out var action); n++)
            try { action(); } catch (Exception error) { Log.Warning("[TurboTick] " + error.Message); }
        operations.RemoveAll(task => task.IsCompleted);
    }

    private static void Post(int id, Action action) => gameActions.Enqueue(() => { if (id == generation) action(); });
    private static void Track(Task task)
    {
        // All task bodies handle their errors; this extra observer also covers Dispose failures.
        operations.Add(task.ContinueWith(t => { if (t.Exception != null) _ = t.Exception; }, TaskScheduler.Default));
    }
}
