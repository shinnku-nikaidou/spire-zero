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
// The decompiled code of the version port.toml's hand-stated facts were read against, when that is another one.
string statedPath = Arg("stated", "");
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
    .Select(f => CSharpSyntaxTree.ParseText(PrimaryConstructors.Undo(File.ReadAllText(f)), parse, f)).ToList();
var refs = Directory.EnumerateFiles(bin, "*.dll").Where(f => Path.GetFileName(f) != "sts2.dll")
    .Where(f => { try { System.Reflection.AssemblyName.GetAssemblyName(f); return true; } catch { return false; } })
    .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
var comp = CSharpCompilation.Create("sts2", trees, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
Console.Error.WriteLine($"port: {trees.Count} files, {refs.Count} references ({clock.Elapsed.TotalSeconds:F1} s)");

var world = new World(comp, cfg);
if (File.Exists(Path.Combine(bin, "sts2.dll"))) world.Lambdas = new Lambdas(Path.Combine(bin, "sts2.dll"));
var emitter = new Emitter(world);
if (quarantinePath.Length > 0 && File.Exists(quarantinePath))
    emitter.Quarantine = File.ReadAllLines(quarantinePath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();

// An interface is a class here when it has code of its own: a static method, a default implementation.
static bool HasCode(INamedTypeSymbol t) => t.GetMembers().OfType<IMethodSymbol>().Any(m => !m.IsAbstract && m.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet);
var types = world.AllTypes.Where(t => (t.TypeKind is TypeKind.Class or TypeKind.Struct || (t.TypeKind == TypeKind.Interface && HasCode(t))) && world.CatOf(t) == Cat.Translated)
    .OrderBy(World.FullName, StringComparer.Ordinal).ToList();
var eq = SymbolEqualityComparer.Default;
List<Emitter.ClassResult> results = [];
world.FindSubscriptions();
for (int pass = 1; pass <= 6; pass++)
{
    world.Rep.Reset();
    world.FindStateEvents();
    world.StubRefs.Clear();
    world.ManualRefs.Clear();
    world.Slots.Clear();
    world.Fns.Clear();
    world.Closures.Clear();
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
var memoFields = world.MemoFields();
foreach (var (path, text) in output.Files)
{
    string full = Path.Combine(outDir, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, Quiet(text, memoFields, rep));
}
// The hand-stated facts: each against the code it rests on, now and in the version it was read against.
{
    var now = Facts.Index(trees);
    Dictionary<string, List<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>>? before = null;
    if (cfg.FactsVersion != cfg.GameVersion)
    {
        if (statedPath.Length == 0 || !Path.GetFileName(statedPath.TrimEnd('/')).EndsWith(cfg.FactsVersion) || !Directory.Exists(statedPath))
            throw new ArgumentException($"{configPath}: the facts were read against {cfg.FactsVersion}: --stated must be its decompiled code");
        before = Facts.Index(Directory.EnumerateFiles(statedPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).AsParallel()
            .Select(f => CSharpSyntaxTree.ParseText(PrimaryConstructors.Undo(File.ReadAllText(f)), parse, f)).ToList());
    }
    // Each [drop] namespace and type rests on the members it takes code out of (as this version's translation found them).
    var drops = rep.RemovedFrom.GroupBy(r => r.Entry).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new Facts.Fact("drop", g.Key, g.Select(r => r.Member).Distinct().Order(StringComparer.Ordinal)
            .Select(m =>
            {
                // Class.Member; an explicit interface member is named in the code by its last part.
                int dot = m.IndexOf('.');
                string member = m[(dot + 1)..];
                if (!member.StartsWith('.') && member.LastIndexOf('.') is var last and > 0) member = member[(last + 1)..];
                return (m[..dot], (string?)member);
            }).Distinct().ToList())).ToList();
    var (lines, look, same) = Facts.Compare(cfg, now, before, drops);
    rep.Facts = lines;
    rep.FactsHead = before == null
        ? $"== hand-stated facts about the game's code (port.toml), read against this version ({cfg.GameVersion}): each with the code it rests on"
        : $"== hand-stated facts about the game's code (port.toml), read against {cfg.FactsVersion}, now {cfg.GameVersion}: \"same\" holds again (its code is the same), \"LOOK\" a person reads again (its code changed, is new or is gone), \"library\" rests on .NET only";
    rep.FactsSummary = before == null
        ? $"hand-stated facts      {lines.Count}  (read against this version)"
        : $"hand-stated facts      {lines.Count}  ({same} hold again, {look} for a person to read again: code changed since {cfg.FactsVersion})";
}
rep.ValueSlots = world.ValueSlots();
rep.EqualityTypes = world.EqualityTypes()
    .Select(e => $"{World.FullName(e.Type)} ({(e.Type.IsRecord ? "record " : "")}{e.Type.TypeKind.ToString().ToLowerInvariant()}): {e.How}; here " +
        (world.IsValue(e.Type) ? (world.EqualityLeftOut(e.Type) is [_, ..] left ? $"a value, its fields compared, but its own leaves out {string.Join(", ", left)}: not the same" : "a value, its fields compared, as its own does")
            : world.CatOf(e.Type) == Cat.Translated ? "an object" : $"not translated ({world.CatOf(e.Type)})"))
    .Order(StringComparer.Ordinal).ToList();
File.WriteAllText(Path.Combine(outDir, "report.txt"), rep.Full());
Console.Write(rep.Summary());
Console.Error.WriteLine($"port: {output.Files.Count} files written to {outDir} ({clock.Elapsed.TotalSeconds:F1} s)");

/// <summary>A write of the translated code to a field no kept walk reads, of a class whose `of_mut` counts writes
/// (World.MemoFields): `X::of_mut(recv, c).field` becomes `X::of_mut_q(recv, c).field`, which counts none, so the
/// walks' lists and answers stay kept across it. Only a write that names one of the class's own state fields right
/// after the call is changed; every other use of `of_mut` (the whole object held, a field of no kept walk's class)
/// counts as before.</summary>
static string Quiet(string text, Dictionary<string, (HashSet<string> Read, HashSet<string> All)> fields, Report rep)
{
    const string call = "::of_mut(";
    var sb = new System.Text.StringBuilder(text.Length);
    int from = 0;
    for (int at = text.IndexOf(call, StringComparison.Ordinal); at >= 0; at = text.IndexOf(call, at + call.Length, StringComparison.Ordinal))
    {
        int start = at;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        if (!fields.TryGetValue(text[start..at], out var f)) continue;
        // The call's closing parenthesis (no string in a receiver holds one).
        int depth = 1, end = at + call.Length;
        for (; end < text.Length && depth > 0; end++)
            depth += text[end] == '(' ? 1 : text[end] == ')' ? -1 : 0;
        if (end >= text.Length || text[end] != '.') continue;
        int name = end + 1, stop = name;
        while (stop < text.Length && (char.IsLetterOrDigit(text[stop]) || text[stop] == '_')) stop++;
        string field = text[name..stop];
        if (!f.All.Contains(field) || f.Read.Contains(field)) continue;
        sb.Append(text, from, at - from).Append("::of_mut_q(");
        from = at + call.Length;
        rep.QuietWrites++;
    }
    return sb.Append(text, from, text.Length - from).ToString();
}
