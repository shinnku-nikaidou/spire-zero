using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Sts2Ai.Port;

/// <summary>What port did: the numbers, what it could not translate and why, and what is left for a person to look at.</summary>
public sealed class Report
{
    private readonly ConcurrentDictionary<string, int> counts = new();
    private readonly ConcurrentDictionary<string, (int Count, string Example)> unsupported = new();
    private readonly ConcurrentDictionary<string, (int Count, string Example)> notes = new();
    private readonly ConcurrentBag<string> droppedWithCall = [];
    private readonly ConcurrentBag<string> failed = [];
    private readonly ConcurrentBag<string> fieldsSkipped = [];
    private readonly ConcurrentBag<string> deferred = [];
    private readonly ConcurrentBag<string> heldChains = [];
    private readonly ConcurrentBag<string> usings = [];
    /// <summary>The events of the translated classes with who subscribes (Output.Events).</summary>
    public List<string> Events = [];
    /// <summary>The classes whose fields the kept walks read, each with the first read found (World.MemoClasses); the
    /// hand-written members they reach, read as their C# is; how many methods they reach.</summary>
    public List<string> MemoClasses = [], MemoManual = [];
    public int MemoMethods;

    public int ClassesTranslated, MembersTranslated, MembersFailed, MembersPresentation, Stubs, ManualRefs, Dispatchers, StatementsLeftOut, Quarantined;

    public void Count(string what) => counts.AddOrUpdate(what, 1, (_, n) => n + 1);

    public void Note(string what, string where) => notes.AddOrUpdate(what, (1, where), (_, v) => (v.Count + 1, v.Example));

    public void FieldSkipped(string what) => fieldsSkipped.Add(what);

    public void Failed(string where, string reason)
    {
        failed.Add($"{where}: {reason}");
        // Tallied without the detail in parentheses, so that one cause is one line.
        string key = reason;
        int paren = key.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && key.EndsWith(')')) key = key[..paren];
        unsupported.AddOrUpdate(key, (1, where), (_, v) => (v.Count + 1, v.Example));
    }

    public void DroppedWithCall(string where, IOperation op)
    {
        var span = op.Syntax.GetLocation().GetLineSpan();
        string text = op.Syntax.ToString().Replace("\r", "");
        string first = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Take(3));
        if (first.Length > 160) first = first[..160] + "...";
        droppedWithCall.Add($"{where} ({Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}): {first}");
    }

    /// <summary>A foreach over a LINQ chain that C# evaluates as the loop goes and the engine where it is written,
    /// with game code in the loop: where the two could differ.</summary>
    public void Deferred(string where, IOperation loop)
    {
        var span = loop.Syntax.GetLocation().GetLineSpan();
        string first = loop.Syntax.ToString().Replace("\r", "").Split('\n')[0].Trim();
        if (first.Length > 180) first = first[..180] + "...";
        deferred.Add($"{where} ({Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}): {first}");
    }

    /// <summary>A local that holds a LINQ chain whose source the engine reads where the local is declared, with game
    /// code between there and the local's last use: C# reads the source's items at each use.</summary>
    public void HeldChain(string where, IOperation declaration)
    {
        var span = declaration.Syntax.GetLocation().GetLineSpan();
        string first = declaration.Syntax.ToString().Replace("\r", "").Split('\n')[0].Trim();
        if (first.Length > 180) first = first[..180] + "...";
        heldChains.Add($"{where} ({Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}): {first}");
    }

    /// <summary>A using of translated code (a statement or a declaration) and what became of each resource.</summary>
    public void Using(string where, IOperation op, string what)
    {
        var span = op.Syntax.GetLocation().GetLineSpan();
        string first = op.Syntax.ToString().Replace("\r", "").Split('\n')[0].Trim();
        if (first.Length > 140) first = first[..140] + "...";
        usings.Add($"{where} ({Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}): {first}  ->  {what}");
    }

    public void Reset()
    {
        deferred.Clear();
        usings.Clear();
        heldChains.Clear();
        counts.Clear();
        unsupported.Clear();
        notes.Clear();
        droppedWithCall.Clear();
        failed.Clear();
        fieldsSkipped.Clear();
        ClassesTranslated = MembersTranslated = MembersFailed = MembersPresentation = Stubs = ManualRefs = Dispatchers = StatementsLeftOut = Quarantined = 0;
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"classes translated      {ClassesTranslated}");
        sb.AppendLine($"members translated      {MembersTranslated}");
        sb.AppendLine($"members not translated  {MembersFailed}  (body is todo!; reasons below)");
        sb.AppendLine($"members failing rustc   {Quarantined}  (body is todo!; listed in the quarantine file)");
        sb.AppendLine($"stubs                   {Stubs}  (members used but neither translated, hand-written nor presentation)");
        sb.AppendLine($"hand-written members    {ManualRefs}  (referred to by translated code)");
        sb.AppendLine($"dispatchers             {Dispatchers}");
        sb.AppendLine($"presentation members    {MembersPresentation}  (left out whole)");
        sb.AppendLine($"statements left out     {StatementsLeftOut}  (presentation)");
        sb.AppendLine($"  of them with a call that is not presentation: {droppedWithCall.Count}  (listed in report.txt, for review)");
        return sb.ToString();
    }

    public string Full()
    {
        var sb = new StringBuilder(Summary());
        sb.AppendLine().AppendLine("== why members were not translated (count, reason, one example)");
        foreach (var kv in unsupported.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"{kv.Value.Count,6}  {kv.Key}    e.g. {kv.Value.Example}");
        sb.AppendLine().AppendLine("== notes (count, what, one example)");
        foreach (var kv in notes.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"{kv.Value.Count,6}  {kv.Key}    e.g. {kv.Value.Example}");
        foreach (var kv in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"{kv.Value,6}  {kv.Key}");
        sb.AppendLine().AppendLine("== fields not kept (their type cannot be translated)");
        foreach (var s in fieldsSkipped.Distinct().Order(StringComparer.Ordinal)) sb.AppendLine(s);
        sb.AppendLine().AppendLine("== statements left out as presentation that held a call that is not presentation");
        foreach (var s in droppedWithCall.Order(StringComparer.Ordinal)) sb.AppendLine(s);
        sb.AppendLine().AppendLine("== loops over a LINQ chain C# evaluates as the loop goes (here: where it is written), with game code inside");
        foreach (var s in deferred.Order(StringComparer.Ordinal)) sb.AppendLine(s);
        sb.AppendLine().AppendLine("== locals that hold a LINQ chain, with game code between where they are declared (where the chain's source is read) and their last use");
        foreach (var s in heldChains.Order(StringComparer.Ordinal)) sb.AppendLine(s);
        sb.AppendLine().AppendLine("== usings of translated code, and what became of each resource (disposed at the end of its scope, or left out and why)");
        foreach (var s in usings.Order(StringComparer.Ordinal)) sb.AppendLine(s);
        sb.AppendLine().AppendLine($"== classes whose fields the kept walks read (port.toml [memo], {MemoMethods} methods reached): only a write to a field of one of these moves the write count");
        foreach (var s in MemoClasses) sb.AppendLine(s);
        sb.AppendLine().AppendLine("== hand-written members the kept walks reach, taken to read what their C# reads");
        foreach (var s in MemoManual) sb.AppendLine(s);
        sb.AppendLine().AppendLine("== events of the engine's classes that someone subscribes to (state: a subscriber is game code; then every subscriber, with what it is)");
        foreach (var s in Events) sb.AppendLine(s);
        sb.AppendLine().AppendLine("== members not translated");
        foreach (var s in failed.Order(StringComparer.Ordinal)) sb.AppendLine(s);
        return sb.ToString();
    }
}
