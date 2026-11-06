using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;

namespace TurboTick;

/// <summary>Isolates visual motes, flecks, and audio effects from the simulation RNG.
/// Cosmetic effects draw random rotation, scale, pitch and volume, but must never advance
/// or shift the deterministic simulation random stream.</summary>
[HarmonyPatch]
internal static class TurboEffectIsolation
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Visual effecters
        yield return AccessTools.Method(typeof(Effecter), nameof(Effecter.EffectTick));
        yield return AccessTools.Method(typeof(Effecter), nameof(Effecter.Trigger));
        yield return AccessTools.Method(typeof(Effecter), nameof(Effecter.Cleanup));
        yield return AccessTools.Method(typeof(SubEffecter), nameof(SubEffecter.SubEffectTick));
        yield return AccessTools.Method(typeof(SubEffecter), nameof(SubEffecter.SubTrigger));
        yield return AccessTools.Method(typeof(SubEffecter), nameof(SubEffecter.SubCleanup));

        // Audio effects
        yield return AccessTools.Method(typeof(SubSoundDef), nameof(SubSoundDef.TryPlay));
        yield return AccessTools.Method(typeof(SubSoundDef), nameof(SubSoundDef.RandomizedResolvedGrain));
        yield return AccessTools.Method(typeof(SubSustainer), nameof(SubSustainer.SubSustainerUpdate));
        var startSample = AccessTools.Method(typeof(SubSustainer), "StartSample");
        if (startSample is not null) yield return startSample;
        var sampleCtor = typeof(Sample).GetConstructor(new[] { typeof(SubSoundDef) });
        if (sampleCtor is not null) yield return sampleCtor;
        var makeSustainer = AccessTools.Method(typeof(SampleSustainer), nameof(SampleSustainer.TryMakeAndPlay));
        if (makeSustainer is not null) yield return makeSustainer;
        var makeOneShot = AccessTools.Method(typeof(SampleOneShot), nameof(SampleOneShot.TryMakeAndPlay));
        if (makeOneShot is not null) yield return makeOneShot;
        yield return AccessTools.Method(typeof(Pawn_CallTracker), nameof(Pawn_CallTracker.CallTrackerTickInterval));
        yield return AccessTools.Method(typeof(Pawn_CallTracker), nameof(Pawn_CallTracker.DoCall));
        var resetCalls = AccessTools.Method(typeof(Pawn_CallTracker), "ResetTicksToNextCall");
        if (resetCalls is not null) yield return resetCalls;

        // Ambient environment sprayers
        yield return AccessTools.Method(typeof(IntermittentSteamSprayer), nameof(IntermittentSteamSprayer.SteamSprayerTick));

        // Fleck and Mote makers
        foreach (var method in typeof(FleckMaker).GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (!method.IsGenericMethod) yield return method;
        foreach (var method in typeof(MoteMaker).GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (!method.IsGenericMethod) yield return method;
    }

    private const int CosmeticSeed = 0x45465853; // "EFXS"

    [HarmonyPriority(Priority.First)]
    private static void Prefix(out bool __state)
    {
        Rand.PushState(CosmeticSeed);
        __state = true;
    }

    [HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state)
        {
            Rand.PopState();
        }
        return __exception;
    }
}
