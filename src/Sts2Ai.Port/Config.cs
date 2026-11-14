using Tomlyn;
using Tomlyn.Model;

namespace Sts2Ai.Port;

/// <summary>engine/port.toml: what is translated, what is written by hand, what is presentation.</summary>
public sealed class Config
{
    /// <summary>Namespaces (exact) and full type names that are translated.</summary>
    public HashSet<string> Translate = [];
    /// <summary>Full type names taken out of the translated namespaces again (they stay stubs).</summary>
    public HashSet<string> Exclude = [];
    /// <summary>Namespaces whose types are presentation (the namespace and everything below it).</summary>
    public List<string> DropNamespaces = [];
    /// <summary>Namespaces below a dropped one that are kept.</summary>
    public HashSet<string> KeepNamespaces = [];
    /// <summary>Types that are presentation, by full name (game or library types).</summary>
    public HashSet<string> DropTypes = [];
    /// <summary>Members that are presentation, as Class.Member.</summary>
    public HashSet<string> DropMembers = [];
    /// <summary>Members that are kept although a `Class.*` of the list above takes their class, as Class.Member.</summary>
    public HashSet<string> KeepMembers = [];
    /// <summary>Classes written by hand in manual/, by simple name.</summary>
    public HashSet<string> ManualClasses = [];
    /// <summary>Members written by hand in manual/, as Class.Member; the rest of the class is translated.</summary>
    public HashSet<string> ManualMembers = [];
    /// <summary>Base classes that own no arena: their fields are part of every class directly below them.</summary>
    public HashSet<string> Open = [];
    /// <summary>Classes that are values in Rust (a struct, compared by content): immutable records such as ModelId.</summary>
    public HashSet<string> ValueClasses = [];
    /// <summary>The roots: statics of the game that are state, as Class.Member. They are part of the combat (Combat.s),
    /// read and written like fields, and where an export starts.</summary>
    public HashSet<string> Statics = [];
    /// <summary>Statics whose initializer reads more than constants and gives the same value whenever it is read, as
    /// Class.Field: translated as a function that makes it again at every read (.NET makes it once a process).</summary>
    public HashSet<string> FixedStatics = [];
    /// <summary>Members whose strings compared by the current culture are names of ASCII letters, digits and
    /// underscores, where culture and ordinal agree, as Class.Member: translated as ordinal.</summary>
    public HashSet<string> OrdinalStrings = [];
    /// <summary>Statics that only remember the result of a computation, as Class.Field: never kept, computed at each use.</summary>
    public HashSet<string> Caches = [];
    /// <summary>Instance fields that are not state, as Class.Field (or Class.Property for the field behind one).</summary>
    public HashSet<string> DropFields = [];
    /// <summary>Parameters that only carry presentation (`Class.Method.parameter`, a constructor as `Class..ctor.parameter`).</summary>
    public HashSet<string> DropParams = [];
    /// <summary>Classes of which only the named fields are state (by field or property name); their other fields are not.</summary>
    public Dictionary<string, HashSet<string>> OnlyFields = [];
    /// <summary>Members that tell hand-written code that they run, as Class.Member (a `*` at the end takes every member
    /// that starts so): `m::class::enter(name, arguments.., c)` before the member's body and `m::class::leave(c)` after it.</summary>
    public List<string> Enter = [];
    public HashSet<string> EnterExcept = [];
    /// <summary>What `enter` is given of the member's parameters, by class: each picked by `type:Name` or `name:name`;
    /// a member without such a parameter gives null or None. A class with no list gives only the member's name.</summary>
    public Dictionary<string, List<string>> EnterArguments = [];

    public bool Enters(string type, string member)
    {
        string full = type + "." + member;
        return !EnterExcept.Contains(full) && Enter.Any(e => e.EndsWith('*') ? full.StartsWith(e[..^1], StringComparison.Ordinal) : e == full);
    }

    /// <summary>Fields that hold a delegate which is never alive at a decision, as Class.Field: kept, not exported.</summary>
    public HashSet<string> TransientDelegates = [];

    /// <summary>Walks whose list is kept while the combat is unchanged, as Class.Method, in the order of their slots.</summary>
    public List<string> Memo = [];

    /// <summary>Getters of an object kept for each object while the combat is unchanged, as Class.Property, in the
    /// order of their slots.</summary>
    public List<string> MemoGetters = [];

    /// <summary>port.toml [only] pairs: two members of a class, as (Class, Open, Close), where a call of Close right after
    /// a call of Open with the same value leaves the object as anything reads it.</summary>
    public List<(string Class, string Open, string Close)> OnlyPairs = [];

    /// <summary>port.toml [only] scratch: classes an object of which, made in a hook loop for one listener's call,
    /// changes nothing outside itself by being made or by its own members, as Class.</summary>
    public HashSet<string> OnlyScratch = [];

    /// <summary>The version of the game the tables are for.</summary>
    public string GameVersion = "";
    /// <summary>The version whose code the hand-stated facts about the game's classes were last read against
    /// (`[game] facts`; the translated version when not given): port compares the code each rests on (Facts.cs).</summary>
    public string FactsVersion = "";
    /// <summary>The name of the game's assembly, as a type of it is named from outside.</summary>
    public string GameAssembly = "sts2";
    /// <summary>Members replaced by a fixed Rust expression, as Class.Member or Class.Member@Class.Method (inside that method only).</summary>
    public Dictionary<string, string> Values = [];

    public static Config Load(string path)
    {
        var doc = Toml.ToModel(File.ReadAllText(path));
        var c = new Config();
        foreach (var s in List(doc, "scope", "translate")) c.Translate.Add(s);
        foreach (var s in List(doc, "scope", "exclude")) c.Exclude.Add(s);
        foreach (var s in List(doc, "drop", "namespaces")) c.DropNamespaces.Add(s);
        foreach (var s in List(doc, "drop", "keep")) c.KeepNamespaces.Add(s);
        foreach (var s in List(doc, "drop", "types")) c.DropTypes.Add(s);
        foreach (var s in List(doc, "drop", "members")) c.DropMembers.Add(s);
        foreach (var s in List(doc, "drop", "keep_members")) c.KeepMembers.Add(s);
        foreach (var s in List(doc, "manual", "classes")) c.ManualClasses.Add(s);
        foreach (var s in List(doc, "manual", "members")) c.ManualMembers.Add(s);
        foreach (var s in List(doc, "open", "classes")) c.Open.Add(s);
        foreach (var s in List(doc, "values", "classes")) c.ValueClasses.Add(s);
        foreach (var s in List(doc, "roots", "members")) c.Statics.Add(s);
        foreach (var s in List(doc, "statics", "fixed")) c.FixedStatics.Add(s);
        foreach (var s in List(doc, "strings", "ordinal")) c.OrdinalStrings.Add(s);
        foreach (var s in List(doc, "drop", "fields")) c.DropFields.Add(s);
        foreach (var s in List(doc, "drop", "parameters")) c.DropParams.Add(s);
        foreach (var s in List(doc, "caches", "members")) c.Caches.Add(s);
        foreach (var s in List(doc, "enter", "members")) c.Enter.Add(s);
        foreach (var s in List(doc, "enter", "except")) c.EnterExcept.Add(s);
        if (doc.TryGetValue("enter", out var en) && en is TomlTable enter && enter.TryGetValue("arguments", out var ea) && ea is TomlTable byClass)
            foreach (var kv in byClass) c.EnterArguments[kv.Key] = ((TomlArray)kv.Value).Select(x => (string)x!).ToList();
        if (doc.TryGetValue("fields", out var fl) && fl is TomlTable only)
            foreach (var kv in only) c.OnlyFields[kv.Key] = ((TomlArray)kv.Value).Select(x => (string)x!).ToHashSet();
        if (doc.TryGetValue("game", out var g) && g is TomlTable game && game.TryGetValue("version", out var ver)) c.GameVersion = (string)ver;
        c.FactsVersion = doc.TryGetValue("game", out var gf) && gf is TomlTable gameF && gameF.TryGetValue("facts", out var fv) ? (string)fv : c.GameVersion;
        if (doc.TryGetValue("game", out var g2) && g2 is TomlTable game2 && game2.TryGetValue("assembly", out var asm)) c.GameAssembly = (string)asm;
        foreach (var s in List(doc, "delegates", "transient")) c.TransientDelegates.Add(s);
        c.Memo.AddRange(List(doc, "memo", "walks"));
        c.MemoGetters.AddRange(List(doc, "memo", "getters"));
        foreach (var s in List(doc, "only", "pairs"))
        {
            var dot = s.IndexOf('.');
            var slash = s.IndexOf('/');
            if (dot < 0 || slash < dot) throw new InvalidDataException($"{path}: [only] pairs: '{s}' is not Class.Open/Close");
            c.OnlyPairs.Add((s[..dot], s[(dot + 1)..slash], s[(slash + 1)..]));
        }
        foreach (var s in List(doc, "only", "scratch")) c.OnlyScratch.Add(s);
        if (c.GameVersion.Length == 0) throw new InvalidDataException($"{path}: no [game] version");
        if (doc.TryGetValue("value", out var v) && v is TomlTable values)
            foreach (var kv in values) c.Values[kv.Key] = (string)kv.Value;
        return c;
    }

    private static IEnumerable<string> List(TomlTable doc, string table, string key)
    {
        if (!doc.TryGetValue(table, out var t) || t is not TomlTable tt || !tt.TryGetValue(key, out var a) || a is not TomlArray arr) yield break;
        foreach (var x in arr) yield return (string)x!;
    }
}
