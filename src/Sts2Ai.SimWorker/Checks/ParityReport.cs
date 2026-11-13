using System.Text.RegularExpressions;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

/// <summary>
/// One combat of a lockstep sweep, as a line of its output: enough to play it again, what the check found, and its
/// share of the sweep's summary, so that the lines of a sweep's parts add up to the whole.
/// </summary>
internal sealed record ParityLine
{
    public required int I { get; init; }
    public required string Character { get; init; }
    public required int Ascension { get; init; }
    public required string Encounter { get; init; }
    public required int Act { get; init; }
    public required string Seed { get; init; }
    /// <summary>"starting", "random" or "given".</summary>
    public required string Deck { get; init; }
    /// <summary>The loadout as the command line takes it; null: the starting one.</summary>
    public string? Loadout { get; init; }
    /// <summary>How the real combat ended; null when it failed.</summary>
    public string? Outcome { get; init; }
    public int Decisions { get; init; }
    /// <summary>The checks made: worlds made at a decision, actions a carried world took, actions a continued world took.</summary>
    public int Imports { get; init; }
    public int Steps { get; init; }
    public int Continues { get; init; }
    /// <summary>The decisions where any check found a difference.</summary>
    public int Differed { get; init; }
    public int DifferenceCount { get; init; }
    /// <summary>The first <see cref="MaxDifferences"/> of them.</summary>
    public IReadOnlyList<Difference> Differences { get; init; } = [];
    /// <summary>Every difference by its path (<see cref="PathKey"/>): how many an import, a step and a continuation found.</summary>
    public Dictionary<string, int[]> Paths { get; init; } = [];
    /// <summary>Every difference after an action, by what the action acts with.</summary>
    public Dictionary<string, int> Subjects { get; init; } = [];
    /// <summary>By kind and id: the decisions it was present in, those of them that differed, the decisions whose
    /// action played or used it, and those of them where a world that agreed before the action differed after it.</summary>
    public Dictionary<string, Dictionary<string, int[]>> Met { get; init; } = [];
    /// <summary>What the real game, or the check itself, failed with: no check was made.</summary>
    public string? Failure { get; init; }
    /// <summary>The first of the game's own Log.Error lines while the real combat was played.</summary>
    public IReadOnlyList<string> GameErrors { get; init; } = [];
    /// <summary>With the engine's world: the combats written out for it, their bytes in all, and the time that took.</summary>
    public int Exports { get; init; }
    public long ExportBytes { get; init; }
    public double ExportMs { get; init; }
    /// <summary>With the engine's world: the classes outside its schema that the combat pointed to, with the references
    /// to each over the combat's exports.</summary>
    public Dictionary<string, int> Outside { get; init; } = [];
    public double Ms { get; init; }

    public const int MaxDifferences = 30;
    private const int MaxValue = 160;
    private static readonly string[] Checks = ["import", "step", "continue"];

    public static ParityLine Of(SweepCombat combat, LockstepResult result, double ms, bool full)
    {
        var paths = new Dictionary<string, int[]>();
        var subjects = new Dictionary<string, int>();
        foreach (var d in result.Differences)
        {
            if (!paths.TryGetValue(PathKey(d), out var counts)) paths[PathKey(d)] = counts = new int[3];
            counts[Array.IndexOf(Checks, d.Check)]++;
            if (d.Subject != null) subjects[d.Subject] = subjects.GetValueOrDefault(d.Subject) + 1;
        }
        var met = GameIds.Kinds.ToDictionary(k => k, _ => new Dictionary<string, int[]>());
        for (int i = 0; i < result.Steps.Count - 1; i++)
        {
            var step = result.Steps[i];
            foreach (var (kind, ids) in step.Present)
                foreach (var id in ids)
                {
                    var counts = Counts(met[kind], id);
                    counts[0]++;
                    if (result.Differed[i]) counts[1]++;
                }
            if (step.Subject!.Split(' ') is [("play" or "potion") and var verb, var used])
            {
                var counts = Counts(met[verb == "play" ? "cards" : "potions"], used);
                counts[2]++;
                if (result.ActionDiffered[i]) counts[3]++;
            }
        }
        return Start(combat) with
        {
            Outcome = result.Outcome,
            Decisions = result.Steps.Count - 1,
            Imports = result.Imports,
            Steps = result.StepChecks,
            Continues = result.ContinueChecks,
            Differed = result.Differed.Count(d => d),
            DifferenceCount = result.Differences.Count,
            Differences = [.. result.Differences.Take(MaxDifferences).Select(d => full ? d : d with { Real = Short(d.Real), World = Short(d.World) })],
            Paths = paths,
            Subjects = subjects,
            Met = met.Where(kv => kv.Value.Count > 0).ToDictionary(),
            GameErrors = [.. result.GameErrors.Take(3).Select(Logs.FirstLine)],
            Ms = Math.Round(ms, 1),
        };

        static int[] Counts(Dictionary<string, int[]> ids, string id) => ids.TryGetValue(id, out var counts) ? counts : ids[id] = new int[4];
        static string? Short(string? value) => value is { Length: > MaxValue } ? value[..MaxValue] + "..." : value;
    }

    public static ParityLine Failed(SweepCombat combat, Exception e, double ms) =>
        Start(combat) with { Failure = $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}", Ms = Math.Round(ms, 1) };

    private static ParityLine Start(SweepCombat combat) => new()
    {
        I = combat.Index,
        Character = combat.Setup.Character,
        Ascension = combat.Setup.Ascension,
        Encounter = combat.Setup.Encounter.Id.Entry,
        Act = combat.Setup.ActIndex,
        Seed = combat.Setup.Seed,
        Deck = combat.Deck,
        Loadout = combat.Setup.Loadout is { } loadout ? RandomLoadout.Text(loadout) : null,
    };

    /// <summary>
    /// What differences are grouped by: the path without its indexes ("enemies[].powers[].amount"), so that the same
    /// field of any card or creature is one group; a world's failure by what it failed with.
    /// </summary>
    public static string PathKey(Difference d) => d.Path == "error" ? $"error: {d.World}" : Regex.Replace(d.Path, @"\[\d+\]", "[]");
}

/// <summary>The summary of a lockstep sweep, added up from its lines.</summary>
internal sealed class ParitySummary
{
    private readonly Dictionary<string, int> _outcomes = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, (int[] Counts, string Example)> _paths = [];
    private readonly Dictionary<string, int> _subjects = [];
    private readonly Dictionary<string, int> _gameErrors = [];
    private readonly Dictionary<string, int> _outside = [];
    private readonly Dictionary<string, Dictionary<string, int[]>> _met = GameIds.Kinds.ToDictionary(k => k, _ => new Dictionary<string, int[]>());
    private int _combats, _failed, _decisions, _imports, _steps, _continues, _differed, _differedCombats, _exports;
    private readonly int[] _differences = new int[3];
    private double _ms, _exportMs;
    private long _exportBytes;

    public int Combats => _combats;

    /// <summary>Whether every combat was checked and none differed.</summary>
    public bool Clean => _failed == 0 && _differences.Sum() == 0;

    public void Add(ParityLine line)
    {
        _combats++;
        _ms += line.Ms;
        foreach (var error in line.GameErrors) _gameErrors[error] = _gameErrors.GetValueOrDefault(error) + 1;
        if (line.Failure != null)
        {
            _failed++;
            _failures[line.Failure] = _failures.GetValueOrDefault(line.Failure) + 1;
            return;
        }
        _outcomes[line.Outcome!] = _outcomes.GetValueOrDefault(line.Outcome!) + 1;
        _exports += line.Exports;
        _exportBytes += line.ExportBytes;
        _exportMs += line.ExportMs;
        foreach (var (type, references) in line.Outside) _outside[type] = _outside.GetValueOrDefault(type) + references;
        _decisions += line.Decisions;
        _imports += line.Imports;
        _steps += line.Steps;
        _continues += line.Continues;
        _differed += line.Differed;
        if (line.DifferenceCount > 0) _differedCombats++;
        foreach (var (path, counts) in line.Paths)
        {
            if (!_paths.TryGetValue(path, out var group))
            {
                var first = line.Differences.FirstOrDefault(d => ParityLine.PathKey(d) == path);
                string example = first == null ? $"#{line.I}" : $"#{line.I} decision {first.Decision}" + (first.From is { } from ? $" (world made at {from})" : "")
                    + (first.Subject != null ? $", {first.Subject}" : "") + (first.Path == "error" ? "" : $": real {first.Real ?? "nothing"}, world {first.World ?? "nothing"}");
                _paths[path] = group = (new int[3], example);
            }
            for (int k = 0; k < 3; k++)
            {
                group.Counts[k] += counts[k];
                _differences[k] += counts[k];
            }
        }
        foreach (var (subject, count) in line.Subjects) _subjects[subject] = _subjects.GetValueOrDefault(subject) + count;
        foreach (var (kind, ids) in line.Met)
            foreach (var (id, counts) in ids)
            {
                if (!_met[kind].TryGetValue(id, out var total)) _met[kind][id] = total = new int[4];
                for (int k = 0; k < 4; k++) total[k] += counts[k];
            }
    }

    /// <param name="universe">Every id of the game by kind (<see cref="GameIds.All"/>).</param>
    public void Print(TextWriter output, Dictionary<string, List<string>> universe)
    {
        output.WriteLine($"combats    {_combats}: {string.Join(", ", _outcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}"
            + (_failed > 0 ? $"; {_failed} FAILED, not checked" : "") + $"  ({_ms / 1000:F0} s of work)");
        foreach (var (failure, count) in _failures.OrderByDescending(kv => kv.Value).Take(10)) output.WriteLine($"  failed x{count}: {failure}");
        foreach (var (error, count) in _gameErrors.OrderByDescending(kv => kv.Value).Take(5)) output.WriteLine($"  game error x{count}: {(error.Length > 200 ? error[..200] + "..." : error)}");
        output.WriteLine($"decisions  {_decisions}; checks: {_imports} imports, {_steps} steps, {_continues} continued steps");
        if (_exports > 0)
            output.WriteLine($"exports    {_exports}: {_exportBytes / 1024.0 / _exports:F0} KB and {_exportMs / _exports:F2} ms each");
        output.WriteLine($"differed   {_differed} decisions in {_differedCombats} combats; differences: {_differences[0]} import, {_differences[1]} step, {_differences[2]} continue");

        if (_outside.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("outside the engine's schema: classes the combats' objects pointed to, whose state the exports do not hold (references):");
            foreach (var (type, references) in _outside.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                output.WriteLine($"  {references,9}  {type}");
        }
        if (_paths.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("differences by first path (import / step / continue), with the first one met:");
            foreach (var (path, (counts, example)) in _paths.OrderByDescending(kv => kv.Value.Counts.Sum()).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(40))
                output.WriteLine($"  {counts.Sum(),7}  {path}  ({counts[0]} / {counts[1]} / {counts[2]})  {example}");
            if (_paths.Count > 40) output.WriteLine($"  ... and {_paths.Count - 40} more paths");
        }
        if (_subjects.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("differences after an action, by what it acts with:");
            foreach (var (subject, count) in _subjects.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(40))
                output.WriteLine($"  {count,7}  {subject}");
            if (_subjects.Count > 40) output.WriteLine($"  ... and {_subjects.Count - 40} more");
        }

        output.WriteLine();
        output.WriteLine("coverage: ids met in a checked decision, and those met in one that differed; cards played and potions used");
        output.WriteLine($"  {"kind",-13}{"ids",6}{"met",6}{"differed",10}{"used",6}{"differed",10}{"never met",11}");
        foreach (var kind in GameIds.Kinds)
        {
            var met = _met[kind];
            bool acts = kind is "cards" or "potions";
            output.WriteLine($"  {kind,-13}{universe[kind].Count,6}{met.Count(kv => kv.Value[0] > 0),6}{met.Count(kv => kv.Value[1] > 0),10}"
                + (acts ? $"{met.Count(kv => kv.Value[2] > 0),6}{met.Count(kv => kv.Value[3] > 0),10}" : $"{"",6}{"",10}")
                + $"{universe[kind].Count(id => !met.ContainsKey(id)),11}");
        }
        var worst = _met.SelectMany(kind => kind.Value.Where(kv => kv.Value[1] > 0).Select(kv => (Kind: kind.Key, Id: kv.Key, Counts: kv.Value)))
            .OrderByDescending(x => (double)x.Counts[1] / x.Counts[0]).ThenByDescending(x => x.Counts[1]).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        if (worst.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("ids by the share of their decisions that differed:");
            foreach (var (kind, id, counts) in worst.Take(40)) output.WriteLine($"  {100.0 * counts[1] / counts[0],5:F1} %  {counts[1],6} of {counts[0],-7} {kind} {id}");
            if (worst.Count > 40) output.WriteLine($"  ... and {worst.Count - 40} more ids");
        }
        output.WriteLine();
        foreach (var kind in GameIds.Kinds)
        {
            List(output, $"never met, {kind}", [.. universe[kind].Where(id => !_met[kind].ContainsKey(id))]);
            if (kind is "cards" or "potions")
                List(output, $"met but never {(kind == "cards" ? "played" : "used")}, {kind}", [.. _met[kind].Where(kv => kv.Value[2] == 0).Select(kv => kv.Key).Order(StringComparer.Ordinal)]);
        }

        // A long list is the coverage table's to hold.
        static void List(TextWriter output, string what, List<string> ids)
        {
            if (ids.Count > 0) output.WriteLine($"{what} ({ids.Count}): {string.Join(" ", ids.Take(60))}{(ids.Count > 60 ? " ... (--coverage FILE lists every id)" : "")}");
        }
    }

    /// <summary>The whole coverage table, one id a line: kind, id, the decisions it was present in, those that differed,
    /// the decisions that played or used it, those where the action differed. An id never met has zeros.</summary>
    public void WriteCoverage(string path, Dictionary<string, List<string>> universe)
    {
        using var file = new StreamWriter(path);
        file.WriteLine("kind\tid\tdecisions\tdiffered\tused\tused_differed");
        foreach (var kind in GameIds.Kinds)
            foreach (var id in universe[kind].Union(_met[kind].Keys).Order(StringComparer.Ordinal))
            {
                var counts = _met[kind].GetValueOrDefault(id) ?? new int[4];
                file.WriteLine($"{kind}\t{id}\t{counts[0]}\t{counts[1]}\t{counts[2]}\t{counts[3]}");
            }
    }
}
