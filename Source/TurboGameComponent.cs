using Verse;

namespace TurboTick;

/// <summary>Per-game settings only; sockets and server policy are transient, never serialized into a save.</summary>
public class TurboGameComponent : GameComponent
{
    private readonly Game? ownerGame;
    public TurboGameComponent() { }

    // 1.7 creates several temporary Game objects before reading/finalizing a save. Construction proves
    // nothing about load success. Only the final instance receives LoadedGame below.
    public TurboGameComponent(Game game) => ownerGame = game;

    private bool savedEnabled = true;
    private float savedMultiplier = 2f;
    private float savedCap = 60f;
    private ulong savedRandomState;
    private bool hasRandomState;
    private bool stableSnapshotIds;

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            var policy = TurboState.Authority.Current;
            savedEnabled = policy.Enabled;
            savedMultiplier = policy.Multiplier;
            savedCap = policy.MaxRate;
            savedRandomState = SimulationRandom.Capture();
            hasRandomState = true;
            stableSnapshotIds = SharedSnapshotStorage.Writing;
        }
        Scribe_Values.Look(ref savedEnabled, "turboEnabled", true);
        Scribe_Values.Look(ref savedMultiplier, "turboMultiplier", 2f);
        Scribe_Values.Look(ref savedCap, "turboMaxRate", 60f);
        Scribe_Values.Look(ref savedRandomState, "turboRandomState", 0UL);
        Scribe_Values.Look(ref hasRandomState, "turboHasRandomState", false);
        Scribe_Values.Look(ref stableSnapshotIds, "turboStableSnapshotIds", false);
    }

    public override void LoadedGame()
    {
        if (!TurboState.Authority.SessionRequested)
            TurboState.Authority.SetLocal(savedEnabled, Math.Clamp(savedMultiplier, 1f, 20f), Math.Clamp(savedCap, 1f, 240f));
        if (ownerGame is not null)
        {
            SharedColony.ScheduleWorldLoaded(ownerGame, WorldLoadScope.CurrentSave,
                hasRandomState ? savedRandomState : null, stableSnapshotIds);
            MultiplayerHosting.GameReady(ownerGame, WorldLoadScope.CurrentSave, newGame: false);
        }
    }

    public override void StartedNewGame()
    {
        if (!TurboState.Authority.SessionRequested) TurboState.InitializeLocal(TurboTickMod.Settings);
        if (ownerGame is not null) MultiplayerHosting.GameReady(ownerGame, null, newGame: true);
    }

    public override void GameComponentUpdate()
    {
        TurboState.Update();
        MultiplayerHosting.Update();
        RoomConnection.AutoConnectOnce();
        SharedColony.Update();
    }
    public override void GameComponentOnGUI() => TurboHud.Draw();
}
