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
    /// <summary>Classes written by hand in manual/, by simple name.</summary>
    public HashSet<string> ManualClasses = [];
    /// <summary>Members written by hand in manual/, as Class.Member; the rest of the class is translated.</summary>
    public HashSet<string> ManualMembers = [];
    /// <summary>Base classes that own no arena: their fields are part of every class directly below them.</summary>
    public HashSet<string> Open = [];
    /// <summary>Classes that are values in Rust (a struct, compared by content): immutable records such as ModelId.</summary>
    public HashSet<string> ValueClasses = [];
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
        foreach (var s in List(doc, "manual", "classes")) c.ManualClasses.Add(s);
        foreach (var s in List(doc, "manual", "members")) c.ManualMembers.Add(s);
        foreach (var s in List(doc, "open", "classes")) c.Open.Add(s);
        foreach (var s in List(doc, "values", "classes")) c.ValueClasses.Add(s);
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
