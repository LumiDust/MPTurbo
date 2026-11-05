namespace TurboTick.Simulation;

/// <summary>
/// Colony-wide UI actions that cannot be replayed safely (trade, caravan forming) are gated instead of
/// being replicated. A wrong replication duplicates silver or goods; a gate only limits who may act.
/// </summary>
public static class InteractionPolicy
{
    /// <summary>
    /// Who may run a colony-wide irreversible action while a shared colony is running.
    /// The checkpoint owner is the same peer already trusted with snapshots.
    /// </summary>
    public static bool CanRunColonyWideAction(bool sharedPlaying, bool isOwner, bool allowEveryone) =>
        !sharedPlaying || allowEveryone || isOwner;

    public static string DeniedMessage(string action) =>
        $"共享殖民地进行中：{action}由房主统一处理，避免两边结果不一致。";
}
