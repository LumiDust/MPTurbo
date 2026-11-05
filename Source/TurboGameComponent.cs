using Verse;

namespace TurboTick;

/// <summary>Per-game settings only; sockets and server policy are transient, never serialized into a save.</summary>
public class TurboGameComponent : GameComponent
{
    // The base has a protected parameterless ctor, but Game.FillComponents constructs
    // concrete components with a Game argument (same contract as built-in components).
    public TurboGameComponent(Game game) { }

    private bool savedEnabled = true;
    private float savedMultiplier = 2f;
    private float savedCap = 60f;

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            var policy = TurboState.Authority.Current;
            savedEnabled = policy.Enabled;
            savedMultiplier = policy.Multiplier;
            savedCap = policy.MaxRate;
        }
        Scribe_Values.Look(ref savedEnabled, "turboEnabled", true);
        Scribe_Values.Look(ref savedMultiplier, "turboMultiplier", 2f);
        Scribe_Values.Look(ref savedCap, "turboMaxRate", 60f);
    }

    public override void LoadedGame()
    {
        if (!TurboState.Authority.SessionRequested)
            TurboState.Authority.SetLocal(savedEnabled, Math.Clamp(savedMultiplier, 1f, 20f), Math.Clamp(savedCap, 1f, 240f));
    }

    public override void StartedNewGame()
    {
        if (!TurboState.Authority.SessionRequested) TurboState.InitializeLocal(TurboTickMod.Settings);
    }

    public override void GameComponentUpdate()
    {
        TurboState.Update();
        SharedColony.Update();
    }
    public override void GameComponentOnGUI() => TurboHud.Draw();
}
