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
    /// that pass runs before the real loader state is restored. Identity fields are preserved: a fragment
    /// that fails to resolve its def must not blank the live object, which would break drawing.</summary>
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
        }
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
