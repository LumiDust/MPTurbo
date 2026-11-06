using RimWorld;
using Verse;
using static TurboTick.TurboTickSettings;

namespace TurboTick;

/// <summary>Coordinates the room host's save selection after the dedicated server grants that role.
/// This client owns no listener or server process. Every intent belongs to one connected room.</summary>
internal static class MultiplayerHosting
{
    private enum LaunchStage { None, Choosing, Publishing }
    private static LaunchStage stage;
    private static int generation;
    private static int connectionGeneration;
    private static string? expectedSave;
    private static bool expectNew;
    private static double armedAt;
    private sealed record ResetReconnect(int ConnectionGeneration, TurboTickSettings Target);
    private static ResetReconnect? resetReconnect;
    public static string Status { get; private set; } = "";
    public static bool Pending => stage != LaunchStage.None;
    public static bool IsHosting => RoomConnection.IsHost;
    public static bool ReadyToChoose => IsHosting && !RoomConnection.Busy && !Pending
        && RoomConnection.Client?.PresenceEpoch == -1
        && SharedColony.Session.Phase is Simulation.SharedPhase.Offline or Simulation.SharedPhase.Faulted;

    public static bool Validate(TurboTickSettings settings, out string error)
    {
        // A copied endpoint may include its port; both Host and Join use the same parsing rules.
        var raw = settings.ServerHost.Trim();
        if (raw.Contains(':') && Uri.TryCreate("tcp://" + raw, UriKind.Absolute, out var endpoint)
            && endpoint.Port is > 0 and <= 65535)
        {
            settings.ServerHost = endpoint.Host.Trim('[', ']');
            settings.ServerPort = endpoint.Port.ToString();
        }
        if (string.IsNullOrWhiteSpace(settings.ServerHost)) error = T("TurboTick_InvalidHost");
        else if (!int.TryParse(settings.ServerPort, out var port) || port is < 1 or > 65535)
            error = T("TurboTick_InvalidPort");
        else if (string.IsNullOrWhiteSpace(settings.PlayerName) || settings.PlayerName.Trim().Length > 32
            || settings.PlayerName.Any(char.IsControl)) error = T("TurboTick_InvalidName");
        else { error = ""; return true; }
        return false;
    }

    internal static void ReconnectAfterServerReset(TurboTickSettings target)
    {
        // Native scenario setup belongs to the entry scene, never an abandoned live Game instance.
        RoomConnection.Leave();
        if (!GenScene.InEntryScene || Current.ProgramState != ProgramState.Entry) GenScene.GoToMainMenu();
        // GoToMainMenu also leaves the room. Arm only afterwards, bound to that exact disconnected generation.
        resetReconnect = new ResetReconnect(RoomConnection.SessionGeneration, new TurboTickSettings
        {
            ServerHost = target.ServerHost, ServerPort = target.ServerPort, PlayerName = target.PlayerName
        });
    }

    public static bool Begin(FileInfo? save, bool newGame, bool useCurrent)
    {
        if (!ReadyToChoose) { Status = T("TurboTick_HostConnectFirst"); return false; }
        if (!PatchInstaller.AllCritical) { Status = T("TurboTick_HostPatchesMissing"); return false; }
        generation++;
        connectionGeneration = RoomConnection.SessionGeneration;
        expectedSave = save == null ? null : Path.GetFileNameWithoutExtension(save.Name);
        expectNew = newGame;
        stage = LaunchStage.Choosing;
        armedAt = Clock.Now;
        Status = T(newGame ? "TurboTick_HostCreating" : "TurboTick_HostLoading");
        try
        {
            if (SharedColony.Session.Phase == Simulation.SharedPhase.Faulted) SharedColony.Reset();
            if (useCurrent) Publish(Current.Game);
            else if (newGame)
            {
                Current.Game = new Game { InitData = new GameInitData { startedFromEntry = true } };
                Find.WindowStack.Add(new Page_SelectScenario());
            }
            else if (save != null) GameDataSaveLoader.CheckVersionAndLoadGame(expectedSave!);
            else throw new InvalidOperationException(T("TurboTick_ChooseSave"));
            return true;
        }
        catch (Exception failure) { Fail(failure.Message); return false; }
    }

    // The loading worker queues completion; only the finished Game on the original room may publish.
    public static void GameReady(Game game, string? saveName, bool newGame)
    {
        if (stage != LaunchStage.Choosing || expectNew != newGame) return;
        if (!newGame && !string.Equals(expectedSave, saveName, StringComparison.OrdinalIgnoreCase)) return;
        var id = generation;
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            if (id != generation || stage != LaunchStage.Choosing || !ReferenceEquals(Current.Game, game)) return;
            try { Publish(game); }
            catch (Exception error) { Fail(error.Message); }
        });
    }

    private static void Publish(Game game)
    {
        if (connectionGeneration != RoomConnection.SessionGeneration || !IsHosting)
        { Fail(T("TurboTick_HostConnectionLost")); return; }
        // A running world received from the server always wins over a local setup intent.
        if (RoomConnection.Client?.PresenceEpoch != -1) { Cancel(); return; }
        if (!ReferenceEquals(Current.Game, game) || Current.ProgramState != ProgramState.Playing)
            throw new InvalidOperationException(T("TurboTick_HostLoading"));
        stage = LaunchStage.Publishing;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        Status = T("TurboTick_HostPublishing");
        RoomConnection.StartSharedColony();
        MultiplayerWindow.Open();
    }

    public static void Update()
    {
        if (resetReconnect is { } restart)
        {
            if (restart.ConnectionGeneration != RoomConnection.SessionGeneration) resetReconnect = null;
            else if (GenScene.InEntryScene && Current.ProgramState == ProgramState.Entry && !LongEventHandler.AnyEventNowOrWaiting)
            {
                resetReconnect = null;
                RoomConnection.Connect(restart.Target, asHost: true);
                MultiplayerWindow.OpenAfterServerReset();
            }
        }
        if (!Pending) return;
        if (connectionGeneration != RoomConnection.SessionGeneration || !IsHosting)
        { Fail(T("TurboTick_HostConnectionLost")); return; }
        if (stage == LaunchStage.Choosing)
        {
            if (RoomConnection.Client?.PresenceEpoch != -1) { Cancel(); return; }
            if ((Current.ProgramState == ProgramState.Entry || !expectNew) && Clock.Now - armedAt > 1
                && !LongEventHandler.AnyEventNowOrWaiting
                && !Find.WindowStack.Windows.Any(window => window is Page or Dialog_MessageBox or Dialog_ModMismatch))
            {
                Cancel();
                Status = T("TurboTick_HostCancelled");
                MultiplayerWindow.Open();
            }
        }
        else if (SharedColony.Session.Phase == Simulation.SharedPhase.Playing)
        {
            Cancel();
            Status = T("TurboTick_HostReady");
            Find.WindowStack.WindowOfType<MultiplayerWindow>()?.Close();
        }
        else if (SharedColony.Session.Phase == Simulation.SharedPhase.Faulted)
            Fail(SharedColony.Session.LastError);
    }

    public static void LoadFailed(string reason)
    {
        if (stage != LaunchStage.Choosing) return;
        var id = generation;
        LongEventHandler.ExecuteWhenFinished(() => { if (id == generation && stage == LaunchStage.Choosing) Fail(reason); });
    }

    private static void Fail(string reason)
    {
        Cancel(); // Keep a healthy dedicated connection and its host role so save selection can be retried.
        Status = T("TurboTick_HostFailed", reason);
        Log.Warning("[TurboTick] " + Status);
        MultiplayerWindow.Open();
    }

    public static void Cancel()
    {
        resetReconnect = null;
        generation++;
        stage = LaunchStage.None;
        expectedSave = null;
        Status = "";
    }
}
