using System.Text.Json;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>One decision of a logged run: what was asked and answered, and the hash of what the brain was shown.</summary>
/// <param name="Act">On a macro decision, the act it was in, from its run view.</param>
internal sealed record LoggedStep(string Surface, string Chosen, string ObsHash, int Legal, bool Explored, int? Act);

/// <summary>A run of a decision log (docs: harness §6) with its setup from its run record: its decisions in order.</summary>
internal sealed record LoggedRun(string RunId, RunSetup Setup, IReadOnlyList<LoggedStep> Steps)
{
    /// <summary>
    /// The runs of the logs that have a run record, in the logs' order. A log holds each run's lines together, in order;
    /// a run stopped midway has no record and may end in a cut line, so it is left out, as is a run with a line missing.
    /// </summary>
    public static IEnumerable<LoggedRun> Read(string logs, string records)
    {
        var setups = new Dictionary<string, RunSetup>();
        foreach (var r in RunRecord.Read(records)) setups.TryAdd(RunHeader.IdOf(r.Seed), new RunSetup(r.Character, r.Seed, r.Ascension));
        foreach (var path in logs.Split(','))
        {
            string? run = null;
            var steps = new List<LoggedStep>();
            bool whole = true;
            foreach (var line in File.ReadLines(path))
            {
                if (Parse(line) is not var (id, decision, step)) continue;
                if (id != run)
                {
                    if (run != null && whole && setups.TryGetValue(run, out var setup)) yield return new(run, setup, steps);
                    (run, steps, whole) = (id, [], true);
                }
                whole &= decision == steps.Count;
                steps.Add(step);
            }
            if (run != null && whole && setups.TryGetValue(run, out var last)) yield return new(run, last, steps);
        }
    }

    private static (string Run, int DecisionId, LoggedStep Step)? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            int? act = e.TryGetProperty("run_view", out var view) ? view.GetProperty("act").GetInt32() : null;
            bool explored = e.TryGetProperty("explored", out var x) && x.GetBoolean();
            return (e.GetProperty("run").GetString()!, e.GetProperty("decision_id").GetInt32(), new LoggedStep(e.GetProperty("surface").GetString()!,
                e.GetProperty("chosen").GetString()!, e.GetProperty("obs_hash").GetString()!, e.GetProperty("legal").GetInt32(), explored, act));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Answers a logged run's decisions in order, card prompts included, and checks each against the log: the same surface
/// (else it throws), and the same observation, by hash (else it counts a mismatch: the observation's format may have
/// changed since). <paramref name="seen"/> sees each macro decision it answers. Past the log, <paramref name="next"/>
/// answers the first decision, which must be a macro one, then <paramref name="then"/> the rest.
/// </summary>
internal sealed class Script(IReadOnlyList<LoggedStep> steps, IRunBrain? then, Func<MacroDecision, string>? next = null,
    Action<MacroDecision, int>? seen = null) : ICombatPolicy, IChoicePolicy, IMacroPolicy
{
    private int _done;
    private bool _nextAsked;

    public int Pending => steps.Count - _done;

    /// <summary>Decisions whose observation's hash differs from the log's.</summary>
    public int Mismatches { get; private set; }

    public CombatAction Choose(CombatDecision decision) =>
        _done < steps.Count ? CombatAction.Parse(Step(Surfaces.Combat, ProtocolJson.Hash(decision.Observation))) : Then().Choose(decision);

    public IReadOnlyList<int> Choose(ChoiceDecision decision) =>
        _done < steps.Count
            ? Step(Surfaces.CardChoice, ProtocolJson.Hash(decision.Choice)).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
            : Then().Choose(decision);

    public string Choose(MacroDecision decision)
    {
        if (_done < steps.Count)
        {
            seen?.Invoke(decision, _done);
            return Step(decision.Surface, ProtocolJson.Hash(decision.Observation));
        }
        if (next == null || _nextAsked) return Then().Choose(decision);
        _nextAsked = true;
        return next(decision);
    }

    private string Step(string surface, ulong hash)
    {
        var step = steps[_done++];
        if (step.Surface != surface) throw new InvalidOperationException($"replay diverged at decision {_done - 1}: logged on {step.Surface}, asked on {surface}");
        if (hash.ToString("x16") != step.ObsHash) Mismatches++;
        return step.Chosen;
    }

    private IRunBrain Then()
    {
        if (next != null && !_nextAsked) throw new InvalidOperationException($"the first decision past the log was not a macro one");
        return then ?? throw new InvalidOperationException("asked beyond the log");
    }
}
