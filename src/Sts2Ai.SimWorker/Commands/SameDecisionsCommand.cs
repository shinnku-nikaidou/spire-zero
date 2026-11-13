using System.CommandLine;
using System.Text.Json;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// same-decisions: whether decision logs (<c>run --log</c>) are the same record by record, all but the time a decision
/// took: surface, brain, what it was shown (the observation's hash), how many options, the action, the plan after it,
/// the candidates with their values, the seed. It is how the planner's two worlds are held against each other: the same
/// seeds played with <c>--combat-world replay</c> and with <c>engine</c>, and the engine on one thread and on many,
/// must leave equal logs with no replanned step in any. It needs no game.
/// </summary>
internal static class SameDecisionsCommand
{
    private static readonly Option<string> LogFiles = Opt.Required<string>("--logs", "the decision logs, such as replay.jsonl,engine.jsonl: each is held against the first");

    public static Command Command()
    {
        var command = Opt.Command("same-decisions", "whether decision logs are the same record by record, all but the times: for the same runs played on plan's two worlds",
            [LogFiles]);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        var files = r.GetValue(LogFiles)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (files.Length < 2) throw new CliException("--logs names at least two logs");
        var logs = files.Select(f => File.ReadLines(f).Where(l => l.Length > 0).Select(ProtocolJson.Deserialize<DecisionEntry>).ToList()).ToList();
        bool clean = true;
        for (int i = 0; i < files.Length; i++)
        {
            var log = logs[i];
            int replans = log.Count(e => e.Replanned == true);
            string? differs = i == 0 ? null : Difference(logs[0], log);
            clean &= replans == 0 && differs == null;
            Console.WriteLine($"{files[i]}: {log.Count} decisions in {log.Select(e => e.Run).Distinct().Count()} runs, {Plans(log)} combat plans, {replans} replanned" +
                              (i == 0 ? "" : differs == null ? "; the same as the first" : $"; DIFFERS: {differs}"));
        }
        return clean ? 0 : 1;
    }

    /// <summary>The combat decisions that were searched: a search leaves candidates, a planned step and a forced answer none.</summary>
    private static int Plans(IReadOnlyList<DecisionEntry> log) => log.Count(e => e.Budget != null && e.Candidates != null);

    /// <summary>Where <paramref name="other"/> first differs from <paramref name="first"/>, as text; null when they
    /// are the same record by record, the times left out.</summary>
    public static string? Difference(IReadOnlyList<DecisionEntry> first, IReadOnlyList<DecisionEntry> other)
    {
        for (int k = 0; k < Math.Min(first.Count, other.Count); k++)
        {
            var (a, b) = (Timeless(first[k]), Timeless(other[k]));
            if (a == b) continue;
            using var left = JsonDocument.Parse(a);
            using var right = JsonDocument.Parse(b);
            var names = left.RootElement.EnumerateObject().Select(m => m.Name).Union(right.RootElement.EnumerateObject().Select(m => m.Name));
            string member = names.First(n => Text(left.RootElement, n) != Text(right.RootElement, n));
            return $"record {k} (run {first[k].Run} decision {first[k].DecisionId}, {first[k].Surface}), {member}: {Short(Text(left.RootElement, member))} against {Short(Text(right.RootElement, member))}";
        }
        return first.Count == other.Count ? null : $"{first.Count} decisions against {other.Count}";
    }

    private static string Timeless(DecisionEntry entry) => ProtocolJson.Serialize(entry with { ElapsedMs = 0 });

    private static string Text(JsonElement record, string member) => record.TryGetProperty(member, out var value) ? value.GetRawText() : "nothing";

    private static string Short(string text) => text.Length <= 160 ? text : text[..160] + "...";
}
