using System.Text.RegularExpressions;

namespace Sts2Ai.Port;

/// <summary>A class with a primary constructor comes out of the decompiler with a constructor whose body sets the
/// fields the parameters go to and then calls the base constructor as a statement, `base._002Ector(..)`, which is
/// not C#. Here that call is put where C# has it, after the parameter list. The game's base constructors of these
/// classes read no field of the class below, so running before the fields are set changes nothing; a constructor
/// with anything after the call is not of this shape and is left as it is (and then fails to bind, which shows).</summary>
public static partial class PrimaryConstructors
{
    [GeneratedRegex(@"\)(\s*\{(?:[^{}]*?))\s*base\._002Ector\(([^;]*)\);(\s*\})")]
    private static partial Regex BaseCall();

    /// <summary>A class that keeps its primary constructor (`class LocString(string locTable, string locEntryKey)`)
    /// and reads the parameters in its members: the parameters become fields of the same names, set by a constructor
    /// written out. Only a class with no base constructor call and no initializer that reads a parameter has this
    /// shape; the others stay as they are.</summary>
    [GeneratedRegex(@"\bclass (\w+)\(([^()<>]+)\)(\s*(?::[^{()]*)?)\{")]
    private static partial Regex Captured();

    /// <summary>The fields made of captured parameters, as Class.field: the compiled game names such a field `&lt;name&gt;P`.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> captured = new();

    /// <summary>The name the compiled game has for a field of the source as port reads it.</summary>
    public static string ClrName(string type, string field) => captured.ContainsKey($"{type}.{field}") ? $"<{field}>P" : field;

    public static string Undo(string source)
    {
        if (source.Contains("base._002Ector(")) source = BaseCall().Replace(source, m => $") : base({m.Groups[2].Value}){m.Groups[1].Value}{m.Groups[3].Value}");
        if (!source.Contains(" class ")) return source;
        return Captured().Replace(source, m =>
        {
            var ps = m.Groups[2].Value.Split(',').Select(p => p.Trim()).ToList();
            var names = ps.Select(p => p[(p.LastIndexOf(' ') + 1)..]).ToList();
            // A parameter an initializer reads is not captured by the members.
            if (names.Any(n => Regex.IsMatch(source, $@"=\s*{Regex.Escape(n)}\s*;"))) return m.Value;
            foreach (string n in names) captured[$"{m.Groups[1].Value}.{n}"] = true;
            string fields = string.Concat(ps.Select(p => $" private readonly {p};"));
            string sets = string.Concat(names.Select(n => $" this.{n} = {n};"));
            return $"class {m.Groups[1].Value}{m.Groups[3].Value}{{{fields} public {m.Groups[1].Value}({m.Groups[2].Value}) {{{sets} }}";
        });
    }
}
