using System.Collections.Concurrent;
using TurboTick.Networking;
using UnityEngine;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>Independent administration connection; a failed player Hello never hides this window.</summary>
public sealed class ServerAdminWindow : Window
{
    private sealed record Endpoint(string Host, int Port)
    {
        public string Display => (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port;
    }
    private sealed record Operation(long Generation, Endpoint Endpoint, int RoomGeneration, CancellationToken Token);

    private readonly ConcurrentQueue<Action> completions = new();
    private string host;
    private string port;
    private string password = "";
    private string status = "";
    private bool failed;
    private bool busy;
    private bool closed;
    private bool confirming;
    private long generation;
    private CancellationTokenSource? cancellation;
    private DedicatedAdminClient? client;
    private Endpoint? authenticatedEndpoint;
    private Endpoint? activeEndpoint;
    private ServerAdminStatus? serverStatus;
    private Dialog_MessageBox? confirmation;
    private Vector2 scroll;
    private static readonly Color Muted = new(0.67f, 0.73f, 0.78f);

    public override Vector2 InitialSize => new(Math.Min(650f, UI.screenWidth - 40f), Math.Min(710f, UI.screenHeight - 40f));

    private ServerAdminWindow()
    {
        host = TurboTickMod.Settings.ServerHost;
        port = TurboTickMod.Settings.ServerPort;
        doCloseX = true;
        absorbInputAroundWindow = true;
        preventCameraMotion = true;
        forcePause = false;
        closeOnAccept = false;
        onlyOneOfTypeAllowed = true;
    }

    public static void Open()
    {
        if (Find.WindowStack == null || Find.WindowStack.WindowOfType<ServerAdminWindow>() != null) return;
        Find.WindowStack.Add(new ServerAdminWindow());
    }

    public override void PostOpen()
    {
        base.PostOpen();
        RoomConnection.SessionChanging += RoomChanging;
    }

    public override void PreClose()
    {
        closed = true;
        RoomConnection.SessionChanging -= RoomChanging;
        Invalidate();
        password = "";
        while (completions.TryDequeue(out _)) { }
        base.PreClose();
    }

    public override void WindowUpdate()
    {
        base.WindowUpdate();
        for (var n = 0; n < 32 && completions.TryDequeue(out var action); n++) action();
        if (confirming && confirmation is { IsOpen: false }) { confirming = false; confirmation = null; }
        if (!closed && authenticatedEndpoint != null && !busy && client?.IsConnected != true)
        {
            Invalidate();
            ShowFailure("TurboTick_AdminConnectionFailed");
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        var oldFont = Text.Font;
        var oldAnchor = Text.Anchor;
        var oldWrap = Text.WordWrap;
        var oldColor = GUI.color;
        try
        {
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Label(new Rect(8, 4, inRect.width - 40, 36), T("TurboTick_ManageServer"), GameFont.Medium);
            var viewport = new Rect(4, 48, inRect.width - 8, inRect.height - 54);
            var width = viewport.width - 20;
            Text.Font = GameFont.Small;
            var guideHeight = Math.Max(52f, Text.CalcHeight(T("TurboTick_AdminGuide"), width) + 8f);
            var statusHeight = Math.Max(78f, Text.CalcHeight(status, width) + 8f);
            var body = new Rect(0, 0, width, guideHeight + 450f + statusHeight);
            Widgets.BeginScrollView(viewport, ref scroll, body);
            try { DrawBody(body, guideHeight, statusHeight); }
            finally { Widgets.EndScrollView(); }
        }
        finally { Text.Font = oldFont; Text.Anchor = oldAnchor; Text.WordWrap = oldWrap; GUI.color = oldColor; }
    }

    private void DrawBody(Rect body, float guideHeight, float statusHeight)
    {
        var width = body.width;
        Label(new Rect(0, 0, width, guideHeight), T("TurboTick_AdminGuide"), GameFont.Small, Muted);
        var y = guideHeight + 8f;
        var previousHost = host;
        var previousPort = port;
        var previousPassword = password;
        host = Field(new Rect(0, y, width, 56), T("TurboTick_Host"), host);
        y += 62f;
        port = Field(new Rect(0, y, width, 56), T("TurboTick_Port"), port);
        y += 62f;
        Label(new Rect(0, y, width, 24), T("TurboTick_AdminPassword"), GameFont.Tiny, Muted);
        // Verified in unitystubs17.txt: GUI.PasswordField(Rect, String, Char). Widgets has no equivalent.
        Text.Font = GameFont.Small;
        password = GUI.PasswordField(new Rect(0, y + 26f, width, 28), password, '*');
        y += 66f;
        if (previousHost != host || previousPort != port || previousPassword != password)
        {
            Invalidate();
            status = T("TurboTick_AdminCredentialsChanged");
            failed = false;
        }

        var authenticated = client?.IsConnected == true && authenticatedEndpoint != null;
        if (Widgets.ButtonText(new Rect(0, y, width * 0.49f, 34), T(failed ? "TurboTick_AdminRetry" : "TurboTick_AdminAuthenticate"), true, true,
            !busy && !confirming && password.Length > 0)) Authenticate();
        if (Widgets.ButtonText(new Rect(width * 0.51f, y, width * 0.49f, 34), T("TurboTick_AdminRefresh"), true, true,
            authenticated && !busy && !confirming)) RefreshStatus();
        y += 50f;

        if (serverStatus != null && authenticated)
        {
            Label(new Rect(0, y, width, 30), T(serverStatus.GameRunning ? "TurboTick_AdminWorldRunning" : "TurboTick_AdminWorldEmpty"), GameFont.Small);
            Label(new Rect(0, y + 34f, width, 28), T("TurboTick_AdminTickPlayers", serverStatus.Tick, serverStatus.PlayerCount), GameFont.Small);
            Label(new Rect(0, y + 66f, width, 28), T("TurboTick_AdminVersion", Brief(serverStatus.GameVersion, 36)), GameFont.Small, Muted);
            Label(new Rect(0, y + 98f, width, 28), T("TurboTick_AdminFingerprint", Brief(serverStatus.ModHash, 16)), GameFont.Small, Muted);
            if (Widgets.ButtonText(new Rect(0, y + 138f, width, 38), T("TurboTick_AdminReset"), true, true, !busy && !confirming)) ConfirmReset();
        }
        else
            Label(new Rect(0, y + 4f, width, 118), T("TurboTick_AdminUnauthenticated"), GameFont.Small, Muted);

        Label(new Rect(0, y + 194f, width, statusHeight), status, GameFont.Small, failed ? new Color(1f, 0.66f, 0.53f) : Muted);
    }

    private bool TryEndpoint(out Endpoint endpoint)
    {
        var candidate = host.Trim();
        var portText = port.Trim();
        if (candidate.Contains(':') && Uri.TryCreate("tcp://" + candidate, UriKind.Absolute, out var uri) && uri.Port is > 0 and <= 65535)
        {
            candidate = uri.Host.Trim('[', ']');
            portText = uri.Port.ToString();
        }
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Any(char.IsControl))
        { endpoint = null!; ShowFailure("TurboTick_InvalidHost"); return false; }
        if (!int.TryParse(portText, out var number) || number is < 1 or > 65535)
        { endpoint = null!; ShowFailure("TurboTick_InvalidPort"); return false; }
        endpoint = new Endpoint(candidate.Trim('[', ']'), number);
        return true;
    }

    private void Authenticate()
    {
        if (busy || confirming || !TryEndpoint(out var endpoint)) return;
        Invalidate();
        var socket = client = new DedicatedAdminClient();
        var operation = Begin(endpoint);
        var secret = password; // Never placed in settings, error strings or logs.
        status = T("TurboTick_AdminConnecting");
        _ = Run(operation, async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(endpoint.Host, endpoint.Port, secret, timeout.Token).ConfigureAwait(false);
            var result = await socket.GetStatusAsync(timeout.Token).ConfigureAwait(false);
            Post(operation, () =>
            {
                authenticatedEndpoint = endpoint;
                serverStatus = result;
                busy = false;
                status = T("TurboTick_AdminAuthenticated");
            });
        });
    }

    private void RefreshStatus()
    {
        if (busy || confirming || client is not { IsConnected: true } socket || authenticatedEndpoint is not { } endpoint) return;
        var operation = Begin(endpoint);
        status = T("TurboTick_AdminRefreshing");
        _ = Run(operation, async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await socket.GetStatusAsync(timeout.Token).ConfigureAwait(false);
            Post(operation, () => { serverStatus = result; busy = false; status = T("TurboTick_AdminAuthenticated"); });
        });
    }

    private void ConfirmReset()
    {
        if (busy || confirming || client is not { IsConnected: true } socket || authenticatedEndpoint is not { } endpoint) return;
        var operation = Begin(endpoint);
        busy = false;
        confirming = true;
        confirmation = Dialog_MessageBox.CreateConfirmation(T("TurboTick_AdminConfirmReset", endpoint.Display), () =>
        {
            if (!IsCurrent(operation) || !confirming || !ReferenceEquals(client, socket)) return;
            confirming = false;
            confirmation = null;
            ResetRoom(operation, socket);
        }, () =>
        {
            if (!IsCurrent(operation)) return;
            confirming = false;
            confirmation = null;
        }, true);
        Find.WindowStack.Add(confirmation);
    }

    private void ResetRoom(Operation operation, DedicatedAdminClient socket)
    {
        if (!IsCurrent(operation) || !socket.IsConnected) return;
        var endpoint = operation.Endpoint;
        var target = new TurboTickSettings
        {
            ServerHost = endpoint.Host, ServerPort = endpoint.Port.ToString(), PlayerName = TurboTickMod.Settings.PlayerName
        };
        if (!MultiplayerHosting.Validate(target, out var validation)) { status = validation; failed = true; return; }
        // These are the only Verse reads for the worker; they happen after the user's confirmation.
        ManifestInputs inputs;
        string exportFile;
        try
        {
            inputs = CompatibilityManifest.Capture();
            exportFile = Path.Combine(GenFilePaths.ConfigFolderPath, "TurboTick", "compatibility-manifest.json");
        }
        catch (Exception) { ShowFailure("TurboTick_AdminManifestFailed"); return; }
        busy = true;
        status = T("TurboTick_AdminResetting");
        _ = Run(operation, async () =>
        {
            var hello = CompatibilityManifest.Build(inputs, target.PlayerName.Trim(), exportFile, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await socket.ResetRoomAsync(hello, timeout.Token).ConfigureAwait(false);
            Post(operation, () =>
            {
                // Close first to revoke all administration callbacks and clear the password.
                Close();
                MultiplayerHosting.ReconnectAfterServerReset(target);
            });
        });
    }

    private Operation Begin(Endpoint endpoint)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        busy = true;
        failed = false;
        activeEndpoint = endpoint;
        return new Operation(++generation, endpoint, RoomConnection.SessionGeneration, cancellation.Token);
    }

    private async Task Run(Operation operation, Func<Task> action)
    {
        try { await Task.Run(action).ConfigureAwait(false); }
        catch (Exception error)
        {
            Post(operation, () =>
            {
                busy = false;
                failed = true;
                status = ErrorText(error);
                // Retrying always authenticates a fresh connection, including after a reset timeout.
                DropClient();
            });
        }
    }

    private void Post(Operation operation, Action action) => completions.Enqueue(() => { if (IsCurrent(operation)) action(); });
    private bool IsCurrent(Operation operation) => !closed && operation.Generation == generation
        && operation.Endpoint == activeEndpoint && !operation.Token.IsCancellationRequested
        && operation.RoomGeneration == RoomConnection.SessionGeneration;

    private void RoomChanging()
    {
        Invalidate();
        status = T("TurboTick_AdminCancelled");
        failed = false;
    }

    private void Invalidate()
    {
        generation++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        activeEndpoint = null;
        busy = false;
        confirming = false;
        var dialog = confirmation;
        confirmation = null;
        dialog?.Close();
        DropClient();
    }

    private void DropClient()
    {
        var old = client;
        client = null;
        authenticatedEndpoint = null;
        serverStatus = null;
        if (old == null) return;
        _ = Task.Run(async () => { try { await old.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } });
    }

    private void ShowFailure(string key) { failed = true; busy = false; status = T(key); }

    private static string ErrorText(Exception error) => T(error switch
    {
        NetworkException { Code: "AdminDisabled" } => "TurboTick_AdminDisabled",
        NetworkException { Code: "AdminAuthenticationFailed" } => "TurboTick_AdminAuthenticationFailed",
        NetworkException { Code: "AdminRateLimited" } => "TurboTick_AdminRateLimited",
        NetworkException { Code: "AdminUnsupported" or "HelloRequired" or "UnsupportedMessage" or "UnknownMessage" or "ProtocolMismatch"
            or "VersionMismatch" or "ClientVersionMismatch" or "InvalidAdminChallenge" or "InvalidAdminWelcome" }
            => "TurboTick_AdminUnsupported",
        NetworkException { Code: "HandshakeTimedOut" or "AdminAuthenticationTimedOut" or "RequestTimedOut" } => "TurboTick_AdminTimeout",
        OperationCanceledException => "TurboTick_AdminTimeout",
        _ => "TurboTick_AdminConnectionFailed"
    });

    private static string Brief(string? value, int limit) => string.IsNullOrWhiteSpace(value) ? T("TurboTick_AdminUnknown")
        : value.Length <= limit ? value : value[..limit] + "…";

    private static string Field(Rect rect, string label, string value)
    {
        Label(new Rect(rect.x, rect.y, rect.width, 24), label, GameFont.Tiny, Muted);
        Text.Font = GameFont.Small;
        return Widgets.TextField(new Rect(rect.x, rect.y + 26, rect.width, 28), value);
    }

    private static void Label(Rect rect, string text, GameFont font, Color? color = null)
    {
        Text.Font = font;
        GUI.color = color ?? Color.white;
        Widgets.Label(rect, text);
        GUI.color = Color.white;
    }
}
