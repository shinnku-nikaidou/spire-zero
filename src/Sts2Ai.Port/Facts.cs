using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sts2Ai.Port;

// ---------------------------------------------------------------- what port.toml states about the game's code

/// <summary>The facts port.toml states by hand about the game's classes (a fixed value, a member written by hand, a
/// pair that leaves an object as it was, a field that only caches, ...), each with the code it rests on: the members
/// or classes it names. Compared with the code of the version they were last read against (`[game] facts`): where that
/// code is the same, the fact holds again; where it changed or is gone, a person reads it again. Whole namespaces
/// ([scope], [drop] namespaces and types) are not compared; [memo] port checks itself at every run (Reads.cs).</summary>
public static class Facts
{
    /// <summary>A fact: its table, its entry as port.toml writes it, and what it rests on, each a class and a member
    /// (null: the whole class; a name ending in `*`: every member that starts so; `~name`: the class's members that
    /// name the field, with it; `#fields`: the class's fields and properties, what a fact about its shape rests on).</summary>
    public sealed record Fact(string Table, string Entry, List<(string Class, string? Member)> On);

    public static List<Fact> Stated(Config c)
    {
        var facts = new List<Fact>();
        static (string, string?) Split(string s)
        {
            int dot = s.IndexOf('.');
            return dot < 0 ? (s, null) : (s[..dot], s[(dot + 1)..]);
        }
        void Add(string table, string entry, params (string, string?)[] on) => facts.Add(new Fact(table, entry, on.ToList()));

        foreach (var kv in c.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var parts = kv.Key.Split('@');
            Add("value", kv.Key, parts.Select(Split).ToArray());
        }
        foreach (var s in c.ManualClasses.Order(StringComparer.Ordinal)) Add("manual", s, (s, null));
        foreach (var s in c.ManualMembers.Order(StringComparer.Ordinal)) Add("manual", s, Split(s));
        foreach (var (cls, open, close) in c.OnlyPairs) Add("only", $"{cls}.{open}/{close}", (cls, null));
        foreach (var s in c.OnlyScratch.Order(StringComparer.Ordinal)) Add("only", s, (s, null));
        foreach (var s in c.Caches.Order(StringComparer.Ordinal)) { var (t, f) = Split(s); Add("caches", s, (t, "~" + f)); }
        foreach (var s in c.TransientDelegates.Order(StringComparer.Ordinal)) { var (t, f) = Split(s); Add("delegates", s, (t, "~" + f)); }
        foreach (var s in c.Statics.Order(StringComparer.Ordinal)) Add("roots", s, Split(s));
        foreach (var s in c.DropMembers.Order(StringComparer.Ordinal)) Add("drop", s, Split(s));
        foreach (var s in c.KeepMembers.Order(StringComparer.Ordinal)) Add("drop", s, Split(s));
        foreach (var s in c.DropFields.Order(StringComparer.Ordinal)) { var (t, f) = Split(s); Add("drop", s, (t, "~" + f)); }
        foreach (var s in c.DropParams.Order(StringComparer.Ordinal))
        {
            var p = s.Split('.');
            Add("drop", s, (p[0], p.Length > 1 ? (p[1] == "" ? ".ctor" : p[1]) : null));
        }
        foreach (var kv in c.OnlyFields.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Add("fields", kv.Key, (kv.Key, "#fields"));
        foreach (var s in c.ValueClasses.Order(StringComparer.Ordinal)) Add("values", s, (s, "#fields"));
        foreach (var s in c.Open.Order(StringComparer.Ordinal)) Add("open", s, (s, "#fields"));
        foreach (var s in c.Enter.Concat(c.EnterExcept).Order(StringComparer.Ordinal)) Add("enter", s, Split(s));
        foreach (var kv in c.EnterArguments.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Add("enter.arguments", kv.Key, (kv.Key, null));
        return facts;
    }

    /// <summary>Every type of the code by its simple name (nested ones too), all their declarations.</summary>
    public static Dictionary<string, List<BaseTypeDeclarationSyntax>> Index(IEnumerable<SyntaxTree> trees)
    {
        var index = new Dictionary<string, List<BaseTypeDeclarationSyntax>>(StringComparer.Ordinal);
        foreach (var tree in trees)
            foreach (var t in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (!index.TryGetValue(t.Identifier.Text, out var list)) index[t.Identifier.Text] = list = [];
                list.Add(t);
            }
        return index;
    }

    private static string Text(SyntaxNode n) => Regex.Replace(n.ToString(), @"\s+", " ");

    private static IEnumerable<string> Names(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => [x.Identifier.Text],
        PropertyDeclarationSyntax x => [x.Identifier.Text],
        EventDeclarationSyntax x => [x.Identifier.Text],
        IndexerDeclarationSyntax => ["this[]"],
        ConstructorDeclarationSyntax => [".ctor"],
        OperatorDeclarationSyntax x => ["op_" + x.OperatorToken.Text],
        BaseFieldDeclarationSyntax x => x.Declaration.Variables.Select(v => v.Identifier.Text),
        BaseTypeDeclarationSyntax x => [x.Identifier.Text],
        _ => [],
    };

    /// <summary>The code a fact rests on in one version, as text with its spaces folded; null when its class is not
    /// there, "" (with `missing`) when its class is but the member is not.</summary>
    private static string? Code(Dictionary<string, List<BaseTypeDeclarationSyntax>> index, string cls, string? member, out bool missing)
    {
        missing = false;
        if (!index.TryGetValue(cls, out var decls)) return null;
        if (member == null) return string.Join("\n", decls.Select(Text));
        var members = decls.OfType<TypeDeclarationSyntax>().SelectMany(d => d.Members).ToList();
        List<MemberDeclarationSyntax> found;
        if (member == "#fields")
            found = members.Where(m => m is BaseFieldDeclarationSyntax or PropertyDeclarationSyntax).ToList();
        else if (member.StartsWith('~'))
        {
            // The field and every member of its class that names it.
            string field = member[1..];
            found = members.Where(m => Names(m).Contains(field) || m.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == field)).ToList();
        }
        else if (member.EndsWith('*'))
        {
            string prefix = member[..^1];
            found = members.Where(m => Names(m).Any(n => n.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        }
        else found = members.Where(m => Names(m).Contains(member)).ToList();
        if (found.Count == 0) { missing = true; return ""; }
        return string.Join("\n", found.Select(Text));
    }

    private static string Named(string cls, string? member) => member switch
    {
        null => cls,
        "#fields" => $"{cls}'s fields",
        _ => $"{cls}.{member.TrimStart('~')}",
    };

    /// <summary>The report's section: each fact with what became of the code it rests on since the version it was read
    /// against (`stated`: that version's code; null when it is the version translated now).</summary>
    public static (List<string> Lines, int Looked, int Same) Compare(Config c, Dictionary<string, List<BaseTypeDeclarationSyntax>> now,
        Dictionary<string, List<BaseTypeDeclarationSyntax>>? stated)
    {
        var lines = new List<string>();
        int look = 0, same = 0;
        foreach (var f in Stated(c))
        {
            var notes = new List<string>();
            bool library = true, toLook = false;
            foreach (var (cls, member) in f.On)
            {
                string on = Named(cls, member);
                string? code = Code(now, cls, member, out bool gone);
                if (code == null && (stated == null || Code(stated, cls, member, out _) == null)) { notes.Add($"{on}: not the game's code"); continue; }
                library = false;
                if (code == null || gone) { notes.Add($"{on}: not there"); toLook = true; continue; }
                if (stated == null) continue;
                string? before = Code(stated, cls, member, out bool goneBefore);
                if (before == null || goneBefore) { notes.Add($"{on}: new since {c.FactsVersion}"); toLook = true; }
                else if (before != code) { notes.Add($"{on}: changed since {c.FactsVersion}"); toLook = true; }
            }
            string status = toLook ? "LOOK" : library ? "library" : stated == null ? "read" : "same";
            if (toLook) look++;
            else if (!library) same++;
            string on2 = string.Join(", ", f.On.Select(o => Named(o.Class, o.Member)));
            lines.Add($"{status,-7}  [{f.Table}] {f.Entry}  (on {on2}){(notes.Count > 0 ? "  " + string.Join("; ", notes) : "")}");
        }
        return (lines, look, same);
    }
}
