using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace TurboTick.DeterminismScan;

/// <summary>One way a mod can make two machines disagree. Every rule names the IL member it matches and
/// says what it does to a lockstep room, because "risky" alone does not tell a player what to do.</summary>
internal sealed record Rule(string Id, string Name, string Why, string Fix, Risk Risk);

internal enum Risk { High, Medium }

internal sealed record Hit(string RuleId, string RuleName, string Risk, string Assembly,
    string Member, string Evidence, string Fix, string Context);

/// <summary>Which side of the process a call site runs on.
///
/// This is the distinction that makes the scan useful. In lockstep every peer runs the same tick code, so a
/// Rand draw inside Tick is perfectly safe: they all draw. A Rand draw inside a window or a draw call is not,
/// because only the player who opened that window runs it, and the stream shifts for that peer alone.
/// Reporting both as "high" would bury the real ones.</summary>
internal enum Site { Simulation, Client, Neutral }

/// <summary>Scans mod assemblies for the calls that break lockstep determinism.
///
/// The point is not to reject mods: most call nothing here. It is to name, before a room is opened, the
/// handful that will make two machines compute different colonies, and to say which call did it and what
/// to do about it.</summary>
internal static class Program
{
    private static readonly Rule[] Rules =
    [
        new("R1", "Rand",
            "Draws from the shared random stream. Safe when every peer runs the call, fatal when only one does, because that peer's stream moves alone.",
            "Keep the draw in tick code, or wrap in Rand.PushState(seed) / PopState.", Risk.High),
        new("R2", "WallClock",
            "Reads real time. It differs on every machine and every run.",
            "Use Find.TickManager.TicksGame instead.", Risk.High),
        new("R3", "FrameTime",
            "Reads frame delta, so the result depends on the machine's frame rate.",
            "Use a tick count, or move the work out of the simulation.", Risk.High),
        new("R4", "LocalRandom",
            "Uses a process-local random source with no shared seed.",
            "Use Verse.Rand, which the session seeds and replays.", Risk.High),
        new("R5", "LocalGuid",
            "Generates a value unique per process, not per colony.",
            "Derive the id from simulated state (thingIDNumber, tick, position).", Risk.High),
        new("R6", "Threading",
            "Runs game state work off the main thread; ordering is not reproducible.",
            "Keep simulation work on the main thread.", Risk.High),
        new("R7", "CurrentMap",
            "Asks for the camera's map, which is null or different on a headless host.",
            "Iterate Find.Maps, or pass the map down explicitly.", Risk.Medium),
    ];

    /// <summary>Method-name fragments that only run on the machine with a window open.</summary>
    private static readonly string[] ClientMarkers =
    [
        "OnGUI", "DoWindowContents", "WindowOnGUI", "Draw", "Print", "Gizmo", "InspectString",
        "GetInspect", "PostDraw", "DrawGUI", "RuntimeTest", "PreOpen", "FillTab", "DoSettingsWindow",
    ];

    /// <summary>Method-name fragments that run on every peer on the same tick.</summary>
    private static readonly string[] SimulationMarkers =
    [
        "Tick", "Update", "ExposeData", "SpawnSetup", "PostMake", "Notify_", "JobDriver", "Toil",
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: determinism-scan <mods-directory> [--json report.json] [--verbose]");
            return 2;
        }
        var root = args[0];
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"not a directory: {root}");
            return 2;
        }
        string? jsonPath = null;
        var verbose = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
            else if (args[i] == "--verbose") verbose = true;
        }

        // Only Assemblies/ is loaded by the game; scanning the rest would report dead code.
        var assemblies = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
            .Where(path => path.Replace('\\', '/').Contains("/Assemblies/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hits = new List<Hit>();
        var failed = new List<string>();
        foreach (var assembly in assemblies)
        {
            try { Scan(assembly, hits); }
            catch (Exception error) { failed.Add($"{Path.GetFileName(assembly)}: {error.GetType().Name}: {error.Message}"); }
        }

        Report(assemblies, hits, failed, jsonPath, verbose);
        // High risk is what actually splits a room.
        return hits.Any(hit => hit.Risk == "high") ? 1 : 0;
    }

    private static void Scan(string path, List<Hit> hits)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return;
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) return;
        var assembly = md.GetString(md.GetAssemblyDefinition().Name);

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var typeName = FullName(md, type);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                MethodBodyBlock body;
                try { body = pe.GetMethodBody(method.RelativeVirtualAddress); }
                catch { continue; }
                var il = body.GetILBytes();
                if (il is null || il.Length == 0) continue;
                var member = typeName + "." + md.GetString(method.Name);
                Walk(il, md, assembly, member, hits);
            }
        }
    }

    /// <summary>Walks the IL and asks the rules about every call, callvirt and newobj target. The operand
    /// width table has to be right: a single misread width turns the rest of the method into noise.</summary>
    private static void Walk(byte[] il, MetadataReader md, string assembly, string member, List<Hit> hits)
    {
        var i = 0;
        while (i < il.Length)
        {
            var opcode = il[i];
            int operand;
            var isCall = false;
            if (opcode == 0xFE)
            {
                if (i + 1 >= il.Length) break;
                var sub = il[i + 1];
                operand = TwoByteOperandSize(sub);
                if (operand < 0) break;
                isCall = sub is 0x06 or 0x07; // ldftn, ldvirtftn take a method token
                i += 2;
            }
            else
            {
                operand = OneByteOperandSize(opcode);
                if (operand < 0) break;
                isCall = opcode is 0x28 or 0x6F or 0x73; // call, callvirt, newobj
                if (opcode == 0x45) // switch: 4-byte count followed by that many 4-byte offsets
                {
                    if (i + 5 > il.Length) break;
                    var count = BitConverter.ToInt32(il, i + 1);
                    if (count < 0 || count > (il.Length - i) / 4) break;
                    i += 1 + 4 + count * 4;
                    continue;
                }
                i += 1;
            }
            if (operand >= 4 && i + 4 <= il.Length && isCall)
                Classify(md, BitConverter.ToInt32(il, i), assembly, member, hits);
            i += operand;
        }
    }

    private static void Classify(MetadataReader md, int token, string assembly, string member, List<Hit> hits)
    {
        var handle = (token >> 24) switch
        {
            0x0A => (EntityHandle)MetadataTokens.MemberReferenceHandle(token & 0x00FFFFFF),
            0x06 => MetadataTokens.MethodDefinitionHandle(token & 0x00FFFFFF),
            _ => default,
        };
        if (handle.IsNil) return;

        string typeName, memberName;
        try
        {
            if (handle.Kind == HandleKind.MemberReference)
            {
                var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                typeName = reference.Parent.Kind switch
                {
                    HandleKind.TypeReference => TypeName(md, md.GetTypeReference((TypeReferenceHandle)reference.Parent)),
                    HandleKind.TypeDefinition => FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)reference.Parent)),
                    _ => "?",
                };
                memberName = md.GetString(reference.Name);
            }
            else
            {
                var definition = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                typeName = FullName(md, md.GetTypeDefinition(definition.GetDeclaringType()));
                memberName = md.GetString(definition.Name);
            }
        }
        catch { return; }

        foreach (var rule in Rules)
        {
            if (!Matches(rule.Id, typeName, memberName)) continue;
            var site = ClassifySite(member);
            // A draw from the shared stream is only a divergence when one peer runs it and the others do
            // not. Inside tick code every peer draws, so it is reported but not counted against the mod.
            var risk = rule.Id == "R1" && site != Site.Client ? "low" : (rule.Risk == Risk.High ? "high" : "medium");
            hits.Add(new Hit(rule.Id, rule.Name, risk, assembly, member,
                typeName + "." + memberName, rule.Fix, site.ToString().ToLowerInvariant()));
        }
    }

    private static bool Matches(string rule, string type, string member) => rule switch
    {
        // Every draw from the shared stream is simulation state.
        "R1" => type is "Verse.Rand"
            && member is not ("PushState" or "PopState" or "get_State" or "set_State"
                or "get_StateCompressed" or "set_StateCompressed" or "EnsureGlobalSeed" or "SeedFromTime"),
        "R2" => (type is "System.DateTime" && member is "get_Now" or "get_UtcNow" or "get_Today")
            || (type is "System.DateTimeOffset" && member is "get_Now" or "get_UtcNow")
            || (type is "System.Environment" && member is "get_TickCount" or "get_TickCount64"),
        "R3" => type is "UnityEngine.Time" && member.StartsWith("get_", StringComparison.Ordinal),
        "R4" => type is "System.Random" or "System.Security.Cryptography.RandomNumberGenerator",
        "R5" => type is "System.Guid" && member.StartsWith("New", StringComparison.Ordinal),
        "R6" => (type is "System.Threading.Tasks.Task" && member is "Run")
            || (type is "System.Threading.Thread" && member is ".ctor" or "Start"),
        "R7" => type is "Verse.Find" && member is "get_CurrentMap",
        _ => false,
    };

    /// <summary>Decides whether a call site runs on every peer or only on the machine with a window open.
    /// Client markers win over simulation markers: a method called <c>CompTick</c> that also draws is still a
    /// tick method, but <c>DoWindowContents</c> contains "Contents" and no tick word, and a name with both is
    /// rare enough that guessing client is the safer error.</summary>
    private static Site ClassifySite(string member)
    {
        var name = member;
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        foreach (var marker in ClientMarkers)
            if (name.Contains(marker, StringComparison.Ordinal)) return Site.Client;
        foreach (var marker in SimulationMarkers)
            if (name.Contains(marker, StringComparison.Ordinal)) return Site.Simulation;
        return Site.Neutral;
    }

    /// <summary>Operand width for single-byte opcodes. -1 ends the walk rather than guessing.</summary>
    private static int OneByteOperandSize(byte opcode) => opcode switch
    {
        <= 0x0D => 0,
        0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 => 1,
        <= 0x1E => 0,
        0x1F => 1,
        0x20 or 0x22 => 4,
        0x21 or 0x23 => 8,
        0x24 or 0x25 or 0x26 => 0,
        0x27 => 4,
        0x28 or 0x29 => 4,
        0x2A => 0,
        >= 0x2B and <= 0x37 => 1,
        >= 0x38 and <= 0x44 => 4,
        0x45 => 0,              // handled separately
        >= 0x46 and <= 0x6E => 0,
        0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 => 4,
        0x76 => 0,
        0x79 => 4,
        0x7A => 0,
        >= 0x7B and <= 0x83 => 4,
        >= 0x84 and <= 0x8B => 0,
        0x8C or 0x8D => 4,
        0x8E => 0,
        0x8F => 4,
        >= 0x90 and <= 0xA4 => 0,
        0xA5 => 4,
        >= 0xA6 and <= 0xBA => 0,
        0xC2 => 4,
        0xC3 => 0,
        0xC6 => 4,
        0xD0 => 4,
        >= 0xD1 and <= 0xDC => 0,
        0xDD => 4,
        0xDE => 1,
        0xDF => 0,
        0xE0 => 4,
        _ => -1,
    };

    private static int TwoByteOperandSize(byte sub) => sub switch
    {
        <= 0x05 => 0,
        0x06 or 0x07 => 4,
        0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E => 2,
        0x0F => 0,
        0x11 => 0,
        0x12 => 1,
        0x13 or 0x14 => 0,
        0x15 or 0x16 => 4,
        0x17 or 0x18 => 0,
        0x19 => 1,
        0x1A => 0,
        0x1C => 4,
        0x1D or 0x1E => 0,
        _ => -1,
    };

    private static string TypeName(MetadataReader md, TypeReference reference)
    {
        var ns = md.GetString(reference.Namespace);
        var name = md.GetString(reference.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var ns = md.GetString(type.Namespace);
        var name = md.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static void Report(List<string> assemblies, List<Hit> hits, List<string> failed,
        string? jsonPath, bool verbose)
    {
        Console.WriteLine($"scanned {assemblies.Count} assemblies under Assemblies/");
        if (failed.Count > 0)
        {
            Console.WriteLine($"{failed.Count} could not be read:");
            foreach (var line in failed) Console.WriteLine("  " + line);
        }

        // Engine and framework assemblies run no colony code; their clock reads cannot diverge a room.
        var relevant = hits.Where(hit => !IsFramework(hit.Assembly)).ToList();
        if (relevant.Count == 0)
        {
            Console.WriteLine("no determinism risks found in mod code");
            return;
        }

        var ordering = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
        foreach (var group in relevant
            .GroupBy(hit => hit.Assembly)
            .OrderByDescending(group => group.Count(hit => hit.Risk == "high"))
            .ThenByDescending(group => group.Count(hit => hit.Risk == "medium"))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var high = group.Count(hit => hit.Risk == "high");
            var medium = group.Count(hit => hit.Risk == "medium");
            if (high == 0 && medium == 0 && !verbose) continue;
            Console.WriteLine();
            Console.WriteLine($"{group.Key}: {high} high, {medium} medium");
            foreach (var byRule in group
                .GroupBy(hit => (hit.RuleId, hit.RuleName))
                .OrderBy(byRule => ordering[byRule.Min(hit => hit.Risk)])
                .ThenBy(byRule => byRule.Key.RuleId, StringComparer.Ordinal))
            {
                var rule = Rules.First(rule => rule.Id == byRule.Key.RuleId);
                var worst = byRule.OrderBy(hit => ordering[hit.Risk]).First().Risk;
                Console.WriteLine($"  [{byRule.Key.RuleId}] {byRule.Key.RuleName} x{byRule.Count()} ({worst})");
                Console.WriteLine($"       {rule.Why}");
                Console.WriteLine($"       fix: {rule.Fix}");
                foreach (var hit in byRule.OrderBy(hit => ordering[hit.Risk]).Take(verbose ? int.MaxValue : 3))
                    Console.WriteLine($"       - [{hit.Context}] {hit.Member} -> {hit.Evidence}");
                if (!verbose && byRule.Count() > 3) Console.WriteLine($"       - ... {byRule.Count() - 3} more");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"total: {relevant.Count(hit => hit.Risk == "high")} high, " +
            $"{relevant.Count(hit => hit.Risk == "medium")} medium, " +
            $"{relevant.Count(hit => hit.Risk == "low")} low");
        Console.WriteLine("high = runs on one peer only and moves shared state; medium = host/client assumption.");

        if (jsonPath is null) return;
        var report = new
        {
            scanned = assemblies.Count,
            unreadable = failed,
            total = relevant.Count,
            high = relevant.Count(hit => hit.Risk == "high"),
            medium = relevant.Count(hit => hit.Risk == "medium"),
            low = relevant.Count(hit => hit.Risk == "low"),
            byAssembly = relevant.GroupBy(hit => hit.Assembly).ToDictionary(
                group => group.Key,
                group => group.Select(hit => new
                {
                    rule = hit.RuleId, name = hit.RuleName, risk = hit.Risk, site = hit.Context,
                    member = hit.Member, call = hit.Evidence, fix = hit.Fix,
                }).ToList()),
        };
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report: {jsonPath}");
    }

    /// <summary>Harmony, the BCL shims and other infrastructure. They are shipped inside mod folders but no
    /// colony code of theirs runs on the shared timeline.</summary>
    private static bool IsFramework(string assembly) => assembly is
        "0Harmony" or "HarmonyMod" or "Mono.Cecil" or "System.Buffers" or "System.Memory"
        or "System.Runtime.CompilerServices.Unsafe" or "0PrepatcherAPI" or "Prepatcher";
}
