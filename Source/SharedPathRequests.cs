using HarmonyLib;
using Verse;

namespace TurboTick;

/// <summary>Resolve shared requests at a deterministic game-thread point. The vanilla batched worker
/// captures pawn/provider costs across frames; a headless replica and a rendered replica can therefore
/// calculate a different route even for the same start, target and random state.</summary>
[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.PushRequest), new[] { typeof(PathRequest) })]
internal static class SharedPathRequests
{
    private static bool Prefix(PathFinder __instance, PathRequest request)
    {
        if (!SharedColony.HasSharedWorld) return true;
        if (request.ResultIsReady || request.Cancelled) return false;
        if (!request.Validate()) return false;
        // Preserve the captured traversal permissions, cost tuning, end mode and customizer. The Pawn
        // overload recreates TraverseParms and would silently drop caller-specific permissions.
        var path = __instance.FindPathNow(request.Start, request.Target, request.TraverseParms,
            request.Tuning, request.EndMode, request.customizer);
        request.Resolve(path);
        return false;
    }
}
