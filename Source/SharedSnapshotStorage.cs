using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Compressed map cells rebuild stones/chunks with new ThingIDs when loaded. Shared snapshots
/// need original IDs for object commands, tick phases and future ID allocation. This only affects our
/// explicit checkpoint writer, never global Defs or ordinary single-player saves.</summary>
internal static class SharedSnapshotStorage
{
    [ThreadStatic] private static int depth;
    internal static bool Writing => depth > 0;

    internal static IDisposable Enter()
    {
        ++depth;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            --depth;
        }
    }
}

[HarmonyPatch(typeof(CompressibilityDeciderUtility), nameof(CompressibilityDeciderUtility.IsSaveCompressible))]
internal static class SharedCompressionPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!SharedSnapshotStorage.Writing) return true;
        __result = false;
        return false;
    }
}
