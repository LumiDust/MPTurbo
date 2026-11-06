using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Identifies which file the engine is actually loading. A Game constructor cannot do this:
/// 1.7 creates multiple temporary games before deserializing the final one.</summary>
[HarmonyPatch(typeof(SavedGameLoaderNow), nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow))]
internal static class WorldLoadScope
{
    [ThreadStatic] private static string? currentSave;
    internal static string? CurrentSave => currentSave;

    private static void Prefix(string __0, out string? __state)
    {
        __state = currentSave;
        currentSave = Path.GetFileNameWithoutExtension(__0);
    }

    private static Exception? Finalizer(Exception? __exception, string? __state)
    {
        currentSave = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(GameAndMapInitExceptionHandlers), nameof(GameAndMapInitExceptionHandlers.ErrorWhileLoadingGame))]
internal static class SharedLoadFailurePatch
{
    private static void Prefix(Exception __0)
    {
        if (SharedColony.LoadPending) SharedColony.OnWorldLoadFailed(__0.Message);
    }
}
