using System.Reflection;
using System.Xml;
using Verse;

namespace TurboTick;

/// <summary>
/// Mod compatibility core. Third-party mods never cooperate with a custom delta protocol, but every
/// mod that survives a save/reload has already described its state through <see cref="IExposable.ExposeData"/>.
/// Scribe is file oriented, yet both halves read and write plain XML: the saver writes to a StringWriter and
/// the loader reads from <c>curXmlParent</c>, a public field. So a field can be round-tripped entirely in
/// memory, which is what this class does.
///
/// This is why compatibility is "the same as save compatibility": if a mod saves and loads its state
/// correctly, its state can also be transported here, with no changes by the mod author.
/// </summary>
internal static class ScribeBridge
{
    /// <summary>Serialises one exposable object to an XML fragment. Returns null when Scribe is busy, because
    /// ExposeData must never be re-entered from inside another save or load.</summary>
    internal static string? Save(IExposable value, string element = "v")
    {
        if (value is null || Scribe.mode != LoadSaveMode.Inactive) return null;
        try
        {
            return Scribe.saver.DebugOutputFor(value);
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] Scribe save failed: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }

    /// <summary>Applies an XML fragment previously produced by <see cref="Save"/>. References to other
    /// objects (a job target, a carried item) cannot resolve until every cross reference is resolved, so
    /// that pass runs before the real loader state is restored.</summary>
    internal static bool Load(IExposable value, string xml)
    {
        if (value is null || string.IsNullOrEmpty(xml) || Scribe.mode != LoadSaveMode.Inactive) return false;
        var document = new XmlDocument();
        try { document.LoadXml("<r>" + xml + "</r>"); }
        catch (XmlException error)
        {
            Log.Warning("[TurboTick] Scribe payload is not valid XML: " + error.Message);
            return false;
        }
        var root = document.DocumentElement?.FirstChild;
        if (root is null) return false;
        // The fragment carries the sender's def, id, map and position. Applying those to a live object
        // assigns Map and Position directly, bypassing the thing grid, so the object would keep drawing
        // with a null map and Plant.Print would throw in GridsUtility.GetSnowDepth. Identity is restored
        // after the load instead of rewritten beforehand: serialising the receiver on every load doubled
        // the Scribe work per object per frame and showed up as multi-second frame hitches.
        var thing = value as Thing;
        // Cheap field reads only: no serialisation on this path.
        var mapBefore = thing?.Map;
        var positionBefore = thing?.Position ?? IntVec3.Invalid;
        var rotationBefore = thing?.Rotation ?? Rot4.North;
        var defBefore = thing?.def;
        var idBefore = thing?.thingIDNumber ?? -1;
        var loader = Scribe.loader;
        var previousParent = loader.curXmlParent;
        var previousPath = loader.curPathRelToParent;
        try
        {
            Scribe.mode = LoadSaveMode.LoadingVars;
            loader.curXmlParent = root;
            loader.curPathRelToParent = null;
            value.ExposeData();
            // Two-pass load: the object registered its wanted references above, so resolve them now.
            Scribe.mode = LoadSaveMode.ResolvingCrossRefs;
            loader.crossRefs.ResolveAllCrossReferences();
            Scribe.mode = LoadSaveMode.PostLoadInit;
            PostLoad(value);
            return true;
        }
        catch (Exception error)
        {
            Log.Warning("[TurboTick] Scribe load failed: " + error.GetType().Name + ": " + error.Message);
            return false;
        }
        finally
        {
            try { loader.crossRefs.Clear(errorIfNotEmpty: false); } catch (Exception) { }
            loader.curXmlParent = previousParent;
            loader.curPathRelToParent = previousPath;
            Scribe.mode = LoadSaveMode.Inactive;
            if (thing is not null)
            {
                if (defBefore is not null && thing.def is null) thing.def = defBefore;
                if (idBefore >= 0 && thing.thingIDNumber != idBefore) thing.thingIDNumber = idBefore;
                RestoreSpawn(thing, mapBefore, positionBefore, rotationBefore);
            }
        }
    }

    /// <summary>Thing.ExposeData deliberately clears the map index on load, because a real save re-registers
    /// every thing afterwards. A live object must be put back on the grid here, or it keeps drawing while
    /// its Map is null and Plant.Print throws in GridsUtility.GetSnowDepth.</summary>
    private static void RestoreSpawn(Thing thing, Map? map, IntVec3 position, Rot4 rotation)
    {
        if (map is null || thing.Destroyed) return;
        if (thing.Spawned && thing.Map == map) return;
        if (thing.Spawned) thing.DeSpawn(DestroyMode.Vanish);
        if (!position.IsValid || !position.InBounds(map)) return;
        GenSpawn.Spawn(thing, position, map, rotation, WipeMode.VanishOrMoveAside);
    }

    /// <summary>Runs an optional post-load hook by reflection: 1.7 exposes several unrelated PostLoad
    /// members without a shared interface, and a mod's Comp may have one.</summary>
    private static void PostLoad(IExposable value)
    {
        for (var type = value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            var method = type.GetMethod("PostLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                Type.EmptyTypes);
            if (method is null) continue;
            try { method.Invoke(value, null); }
            catch (Exception error) { Log.Warning("[TurboTick] PostLoad: " + error.GetType().Name); }
            return;
        }
    }
}
