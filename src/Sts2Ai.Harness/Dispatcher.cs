using System.Diagnostics;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>A combat brain as Harness calls it: the decision, and the seed Harness drew for it.</summary>
/// <param name="Budget">What the brain may spend, for the log; null for a policy that does not search.</param>
public sealed record CombatRoute(string Brain, Func<CombatDecision, ulong, CombatDecisionResult> Decide, Budget? Budget = null)
{
    /// <summary>A placeholder policy: it gives its pick and no candidates.</summary>
    public static CombatRoute Of(string brain, ICombatPolicy policy) => new(brain, (decision, _) => new(policy.Choose(decision), [], []));
}

/// <summary>A brain for one side of the routing table, with its name for the log.</summary>
public sealed record Route<T>(string Brain, T Policy);

/// <param name="DecisionId">The decision's number in its run, from 0, card prompts included.</param>
/// <param name="ObsHash">The hash of what the brain was shown: the combat observation, the run observation, or the card prompt.</param>
/// <param name="Legal">How many legal actions (or prompt options) there were.</param>
/// <param name="Chosen">The action in text form; for a prompt, the picked option indexes.</param>
/// <param name="Plan">The rest of the turn after <paramref name="Chosen"/>, as the brain planned it.</param>
public sealed record DecisionEntry(
    string Run, int DecisionId, string Env, string Surface, string Brain, string ObsHash, int Legal, string Chosen,
    IReadOnlyList<string>? Plan, IReadOnlyList<Candidate>? Candidates, Budget? Budget, ulong? Seed, double ElapsedMs);

/// <summary>Who made a decision, and what a searching brain planned and scored: what its recommendation carries.</summary>
public sealed record DecisionNote(string Brain, IReadOnlyList<string>? Plan = null, IReadOnlyList<Candidate>? Candidates = null);

/// <summary>
/// Harness for one run (docs: harness §4–§6). The host asks it as its combat, card-prompt and macro policy; it routes
/// each decision to exactly one brain, draws the brains' seeds from the run id and the decision's number (never from the
/// game), and logs every decision when asked to. A surface with no route throws: there is no fallback.
/// Card prompts go to the combat side when asked during a combat, to the macro side otherwise.
/// </summary>
public sealed class Dispatcher(
    string runId, PlayEnvironment env, CombatRoute combat, Route<IChoicePolicy> combatChoices, Route<IMacroPolicy> macro,
    Route<IChoicePolicy> macroChoices, bool log = false) : ICombatPolicy, IChoicePolicy, IMacroPolicy
{
    /// <summary>Every surface outside combat that has a route (docs: harness §4).</summary>
    public static readonly IReadOnlySet<string> MacroSurfaces = new HashSet<string>
    {
        Surfaces.Map, Surfaces.Rewards, Surfaces.CardReward, Surfaces.Bundle, Surfaces.Event, Surfaces.Shop, Surfaces.Rest,
        Surfaces.Treasure, Surfaces.CrystalSphere,
    };

    private readonly ulong _runHash = ProtocolJson.Hash(runId);
    private readonly List<DecisionEntry> _log = [];
    private readonly Dictionary<string, int> _brains = [];
    private int _decisions;

    /// <summary>The run's decisions so far, when logging.</summary>
    public IReadOnlyList<DecisionEntry> Log => _log;

    public PlayEnvironment Env => env;

    /// <summary>The latest decision's brain, plan and candidates.</summary>
    public DecisionNote? Last { get; private set; }

    /// <summary>How many decisions each brain made so far.</summary>
    public IReadOnlyDictionary<string, int> Brains => _brains;

    /// <summary>The brain on each side of the routing table, for reports.</summary>
    public string Champion =>
        $"combat {combat.Brain}{(combat.Budget is { } b ? $" {b.Worlds}x{b.NodesPerWorld}" : "")}, combat prompts {combatChoices.Brain}, " +
        $"macro {macro.Brain}, macro prompts {macroChoices.Brain}";

    public CombatAction Choose(CombatDecision decision)
    {
        var (id, seed, clock) = Begin(combat.Brain);
        var result = combat.Decide(decision, seed);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!decision.Legal.Contains(result.First)) throw new InvalidOperationException($"{combat.Brain} chose {result.First}, which is not legal");
        Last = new(combat.Brain, result.Plan.Count > 0 ? result.Plan.Select(a => a.ToString()).ToList() : null, result.Candidates.Count > 0 ? result.Candidates : null);
        if (log)
            _log.Add(new(runId, id, env.ToString(), Surfaces.Combat, combat.Brain, Hex(ProtocolJson.Hash(decision.Observation)), decision.Legal.Count,
                result.First.ToString(), Last.Plan, Last.Candidates, combat.Budget, seed, ms));
        return result.First;
    }

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        var choice = decision.Choice;
        var route = choice.Context.InCombat ? combatChoices : macroChoices;
        var (id, seed, clock) = Begin(route.Brain);
        var picks = route.Policy.Choose(decision);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!choice.Accepts(picks)) throw new InvalidOperationException($"{route.Brain} gave [{string.Join(",", picks)}], which the prompt does not accept");
        Last = new(route.Brain);
        if (log)
            _log.Add(new(runId, id, env.ToString(), Surfaces.CardChoice, route.Brain, Hex(ProtocolJson.Hash(choice)), choice.Options.Count,
                string.Join(",", picks), null, null, null, seed, ms));
        return picks;
    }

    public string Choose(MacroDecision decision)
    {
        var (id, seed, clock) = Begin(macro.Brain);
        if (!MacroSurfaces.Contains(decision.Surface)) throw new InvalidOperationException($"uncovered surface {decision.Surface}");
        var action = macro.Policy.Choose(decision);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!decision.Legal.Contains(action)) throw new InvalidOperationException($"{macro.Brain} chose {action} on {decision.Surface}, which is not legal");
        Last = new(macro.Brain);
        if (log)
            _log.Add(new(runId, id, env.ToString(), decision.Surface, macro.Brain, Hex(ProtocolJson.Hash(decision.Observation)), decision.Legal.Count,
                action, null, null, null, seed, ms));
        return action;
    }

    private (int Id, ulong Seed, Stopwatch Clock) Begin(string brain)
    {
        _brains[brain] = _brains.GetValueOrDefault(brain) + 1;
        int id = _decisions++;
        return (id, Mix(_runHash, (ulong)id), Stopwatch.StartNew());
    }

    private static string Hex(ulong hash) => hash.ToString("x16");

    /// <summary>SplitMix64 of <paramref name="a"/> stepped <paramref name="b"/> + 1 times.</summary>
    private static ulong Mix(ulong a, ulong b)
    {
        ulong z = a + 0x9E3779B97F4A7C15 * (b + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
