using System.Globalization;
using TurboTick.Networking;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    var options = new ServerOptions();
    var hostEnv = Environment.GetEnvironmentVariable("TURBOTICK_HOST");
    var portEnv = Environment.GetEnvironmentVariable("TURBOTICK_PORT");
    var dataEnv = Environment.GetEnvironmentVariable("TURBOTICK_DATA");
    var gameVerEnv = Environment.GetEnvironmentVariable("TURBOTICK_GAME_VERSION");
    var modHashEnv = Environment.GetEnvironmentVariable("TURBOTICK_MOD_HASH");
    var ctrlOnlyEnv = Environment.GetEnvironmentVariable("TURBOTICK_CONTROLLER_ONLY");

    if (!string.IsNullOrWhiteSpace(hostEnv)) options = options with { Address = hostEnv };
    if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)) options = options with { Port = parsedPort };
    if (!string.IsNullOrWhiteSpace(dataEnv)) options = options with { DataDirectory = dataEnv };
    if (!string.IsNullOrWhiteSpace(gameVerEnv)) options = options with { GameVersion = gameVerEnv };
    if (!string.IsNullOrWhiteSpace(modHashEnv)) options = options with { ModHash = modHashEnv };
    if (string.Equals(ctrlOnlyEnv, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(ctrlOnlyEnv, "1")) options = options with { EveryoneCanEdit = false };

    string? importPath = null;
    try
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "--help" or "-h")
            {
                Console.WriteLine("TurboTick dedicated server (.NET 10; no game installation required)");
                Console.WriteLine("--host ADDRESS       Bind address (default 127.0.0.1)");
                Console.WriteLine("--port PORT          TCP port (default 30572; 0 selects a free port)");
                Console.WriteLine("--data DIRECTORY     Persistent state and snapshots (default Saved)");
                Console.WriteLine("--game-version VER   1.7 locks to first complete 1.7.x peer, or specify exact version");
                Console.WriteLine("--mod-hash HASH      Exact simulation hash; development locks to first peer");
                Console.WriteLine("--controller-only    One server-assigned controller; transfers to the host when claimed");
                Console.WriteLine("--import FILE        Import a local snapshot before accepting players");
                return 0;
            }
            if (argument == "--controller-only") { options = options with { EveryoneCanEdit = false }; continue; }
            if (++index >= arguments.Length) throw new ArgumentException($"Missing value for {argument}.");
            var value = arguments[index];
            switch (argument)
            {
                case "--host": options = options with { Address = value }; break;
                case "--port": options = options with { Port = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) }; break;
                case "--data": options = options with { DataDirectory = value }; break;
                case "--game-version": options = options with { GameVersion = value }; break;
                case "--mod-hash": options = options with { ModHash = value }; break;
                case "--import":
                case "--import-snapshot": importPath = value; break;
                default: throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; shutdown.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var server = new DedicatedServer(options);
            if (importPath is not null)
            {
                await using var source = new FileStream(importPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var manifest = await server.Snapshots.PutAsync(source, shutdown.Token);
                Console.WriteLine($"Imported snapshot {manifest.Id}: {manifest.Length} bytes, {manifest.ChunkHashes.Count} chunks.");
            }
            await server.StartAsync(shutdown.Token);
            Console.WriteLine($"TurboTick protocol {NetworkProtocol.Version}, client {NetworkProtocol.ClientVersion}; listening on {options.Address}:{server.Port}.");
            Console.WriteLine(options.EveryoneCanEdit ? "Policy editing: every joined peer." : "Policy editing: one server-assigned controller; transfers to a claimed host.");
            Console.WriteLine("Hosting: connect and claim the host role before publishing a colony; other players join the same dedicated server.");
            string? loggedVersion = null;
            string? loggedHash = null;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                var version = server.LockedGameVersion;
                var hash = server.LockedModHash;
                if (version != loggedVersion || hash != loggedHash)
                {
                    Console.WriteLine($"Compatibility lock: game={version ?? "waiting for first peer"}; modHash={hash ?? "waiting for first peer"}");
                    loggedVersion = version;
                    loggedHash = hash;
                }
            } while (await timer.WaitForNextTickAsync(shutdown.Token));
            await server.StopAsync();
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        finally { Console.CancelKeyPress -= cancel; }
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
    {
        Console.Error.WriteLine($"Configuration error: {ex.Message}");
        return 2;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
    {
        // Neither the full command line nor file contents are logged.
        Console.Error.WriteLine($"Server startup or storage failed ({ex.GetType().Name}).");
        return 1;
    }
}
