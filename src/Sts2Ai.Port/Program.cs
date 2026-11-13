// port: translates the decompiled game (C#) to the Rust of engine/crates/sts2ai-engine/src/gen. See engine/DESIGN.md.
//
//   port --decompiled DIR --bin DIR --config engine/port.toml --out engine/crates/sts2ai-engine/src/gen [--quarantine FILE]
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sts2Ai.Port;

string Arg(string name, string? fallback = null)
{
    int i = Array.IndexOf(args, "--" + name);
    if (i >= 0 && i + 1 < args.Length) return args[i + 1];
    return fallback ?? throw new ArgumentException($"missing --{name}");
}

string decompiled = Arg("decompiled"), bin = Arg("bin"), configPath = Arg("config"), outDir = Arg("out");
string quarantinePath = Arg("quarantine", "");
var clock = Stopwatch.StartNew();
var cfg = Config.Load(configPath);
if (!Path.GetFileName(decompiled.TrimEnd('/')).EndsWith(cfg.GameVersion))
    throw new ArgumentException($"{configPath} is for the game {cfg.GameVersion}, {decompiled} is another");

// The whole decompiled tree is compiled only to be asked questions (types, symbols, conversions). The game's own
// assembly is left out of the references: its source is what we read.
var parse = new CSharpParseOptions(LanguageVersion.Preview);
var trees = Directory.EnumerateFiles(decompiled, "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/"))
    .Order(StringComparer.Ordinal).AsParallel().AsOrdered()
    .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f)).ToList();
var refs = Directory.EnumerateFiles(bin, "*.dll").Where(f => Path.GetFileName(f) != "sts2.dll")
    .Where(f => { try { System.Reflection.AssemblyName.GetAssemblyName(f); return true; } catch { return false; } })
    .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
var comp = CSharpCompilation.Create("sts2", trees, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
Console.Error.WriteLine($"port: {trees.Count} files, {refs.Count} references ({clock.Elapsed.TotalSeconds:F1} s)");

var world = new World(comp, cfg);
var emitter = new Emitter(world);
if (quarantinePath.Length > 0 && File.Exists(quarantinePath))
    emitter.Quarantine = File.ReadAllLines(quarantinePath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();

// An interface is a class here when it has code of its own: a static method, a default implementation.
static bool HasCode(INamedTypeSymbol t) => t.GetMembers().OfType<IMethodSymbol>().Any(m => !m.IsAbstract && m.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet);
var types = world.AllTypes.Where(t => (t.TypeKind is TypeKind.Class or TypeKind.Struct || (t.TypeKind == TypeKind.Interface && HasCode(t))) && world.CatOf(t) == Cat.Translated)
    .OrderBy(World.FullName, StringComparer.Ordinal).ToList();
var eq = SymbolEqualityComparer.Default;
List<Emitter.ClassResult> results = [];
for (int pass = 1; pass <= 6; pass++)
{
    world.Rep.Reset();
    world.StubRefs.Clear();
    world.ManualRefs.Clear();
    world.Slots.Clear();
    var bag = new ConcurrentBag<Emitter.ClassResult>();
    Parallel.ForEach(types, t => bag.Add(emitter.Class(t)));
    results = bag.OrderBy(r => World.FullName(r.Type), StringComparer.Ordinal).ToList();

    // A method with nothing left in it but presentation is presentation: the next pass leaves out the calls to it too.
    // A virtual one only when every implementation of its slot is like that.
    var hollow = new HashSet<ISymbol>(results.SelectMany(r => r.Hollow), eq);
    var bySlot = results.SelectMany(r => r.Virtuals).Where(m => !m.IsAbstract).GroupBy(m => World.Slot(m), eq).ToDictionary(g => g.Key!, g => g.ToList(), eq);
    var found = new HashSet<ISymbol>(eq);
    foreach (var m in hollow.Cast<IMethodSymbol>())
    {
        var slot = World.Slot(m);
        bool isVirtual = m.IsVirtual || m.IsOverride || bySlot.ContainsKey(m) || !eq.Equals(slot, m);
        if (!isVirtual) { found.Add(m); continue; }
        if (slot.ContainingType.TypeKind == TypeKind.Interface || world.CatOf(slot.ContainingType) != Cat.Translated) continue;
        if (bySlot.TryGetValue(slot, out var impls) && impls.All(hollow.Contains))
        {
            found.Add(slot);
            foreach (var i in impls) found.Add(i);
        }
    }
    found.ExceptWith(world.AutoDropped);
    Console.Error.WriteLine($"port: pass {pass}: {results.Sum(r => r.Translated)} members, {found.Count} more are presentation as a whole ({clock.Elapsed.TotalSeconds:F1} s)");
    if (found.Count == 0) break;
    world.AutoDropped.UnionWith(found);
}

var rep = world.Rep;
rep.ClassesTranslated = results.Count;
rep.MembersTranslated = results.Sum(r => r.Translated);
rep.MembersFailed = results.Sum(r => r.Failed);
rep.MembersPresentation = results.Sum(r => r.Presentation);
rep.Quarantined = results.Sum(r => r.Quarantined);
rep.StatementsLeftOut = results.Sum(r => r.LeftOut);

var output = new Output(world, emitter);
output.Build(results);

if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
foreach (var (path, text) in output.Files)
{
    string full = Path.Combine(outDir, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, text);
}
File.WriteAllText(Path.Combine(outDir, "report.txt"), rep.Full());
Console.Write(rep.Summary());
Console.Error.WriteLine($"port: {output.Files.Count} files written to {outDir} ({clock.Elapsed.TotalSeconds:F1} s)");
