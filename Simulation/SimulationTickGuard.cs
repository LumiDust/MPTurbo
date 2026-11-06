namespace TurboTick.Simulation;

/// <summary>A permit protects one actual game tick, not one render frame. Call Begin immediately before
/// the engine simulation method and Complete only after it succeeds. Game-thread owned.</summary>
public sealed class SimulationTickGuard(TickBarrier barrier)
{
    public readonly record struct Permit(int PreviousGameTick, int NextLogicalTick);
    private Permit? executing;

    public bool TryBegin(int gameTick, out Permit permit)
    {
        permit = default;
        if (executing is not null || !barrier.CanAdvance) return false;
        permit = new Permit(gameTick, checked(barrier.AppliedTick + 1));
        executing = permit;
        return true;
    }

    public bool Complete(Permit permit, int gameTick)
    {
        if (executing != permit) return false;
        executing = null;
        // A skipped/failed/debug-jumped engine tick must never be counted as one successful simulation.
        if ((long)gameTick - permit.PreviousGameTick != 1
            || barrier.AppliedTick != permit.NextLogicalTick - 1) return false;
        return barrier.TryAdvance();
    }

    public void Cancel() => executing = null;
}
