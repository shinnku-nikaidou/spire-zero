using System.Diagnostics;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// A combat brain as Harness calls it: the decision, the seed Harness drew for it, in SL the current node's clone
/// (null in NoSL), and the combat's record of enemy moves so far (<see cref="Dispatcher.History"/>).
/// </summary>
/// <param name="Budget">What the brain may spend, for the log; null for a policy that does not search.</param>
public sealed record CombatRoute(string Brain, Func<CombatDecision, ulong, NodeClone?, IReadOnlyList<EnemyTurns>, CombatDecisionResult> Decide,
    Budget? Budget = null)
{
    /// <summary>A placeholder policy: it gives its pick and no candidates.</summary>
    public static CombatRoute Of(string brain, ICombatPolicy policy) => new(brain, (decision, _, _, _) => new(policy.Choose(decision), [], []));

    /// <summary>
    /// Self-play exploration (docs: learning/loop §9): this route with its first action drawn by <paramref name="explore"/>
    /// over the search's candidates by value, where there are two or more. One that is not the search's pick is played
    /// without a plan, as the plan was the pick's, and is marked explored if it is valued below the pick
    /// (<see cref="Softmax.Explored"/>). The caller names the brain with its exploration.
    /// </summary>
    public CombatRoute Exploring(Softmax explore)
    {
        var decide = Decide;
        return this with
        {
            Decide = (decision, seed, node, history) =>
            {
                var result = decide(decision, seed, node, history);
                if (result.Candidates.Count < 2) return result;
                var candidates = result.Candidates;
                int drawn = explore.Draw(candidates.Select(c => c.Value).ToList());
                if (drawn == 0) return result;
                bool explored = explore.Explored(candidates[drawn].Value, candidates[0].Value);
                return new(CombatAction.Parse(candidates[drawn].Action), [], candidates, Explored: explored);
            },
        };
    }
}

/// <summary>A brain for one side of the routing table, with its name for the log.</summary>
public sealed record Route<T>(string Brain, T Policy);

/// <param name="DecisionId">The decision's number in its run, from 0, card prompts included.</param>
/// <param name="ObsHash">The hash of what the brain was shown: the combat observation, the run observation, or the card prompt.</param>
/// <param name="Legal">How many legal actions (or prompt options) there were.</param>
/// <param name="Chosen">The action in text form; for a prompt, the picked option indexes.</param>
/// <param name="Plan">What the brain planned after <paramref name="Chosen"/>: the rest of the turn in combat, the rest of
/// the act's route on the map.</param>
/// <param name="Obs">On a combat decision, the observation the brain was shown: the input of the learned combat parts
/// and of the fusion panel.</param>
/// <param name="RunView">On a macro decision, the run the brain was shown: what V is trained on (docs: macro-brain/value §3).</param>
/// <param name="History">On a combat decision, what each enemy of the combat showed at the start of each round so far,
/// this one's included (<see cref="Dispatcher.History"/>).</param>
/// <param name="Explored">True when <paramref name="Chosen"/> was drawn by self-play exploration and valued below the
/// brain's best (docs: learning/loop §9; <see cref="Softmax.Explored"/>); absent otherwise.</param>
/// <param name="Replanned">True when the combat brain searched again because a step of its plan did not hold
/// (<see cref="CombatDecisionResult.Replanned"/>); absent otherwise.</param>
public sealed record DecisionEntry(
    string Run, int DecisionId, string Env, string Surface, string Brain, string ObsHash, int Legal, string Chosen,
    IReadOnlyList<string>? Plan, IReadOnlyList<Candidate>? Candidates, Budget? Budget, ulong? Seed, double ElapsedMs,
    CombatObservation? Obs = null, RunView? RunView = null,
    IReadOnlyList<EnemyTurns>? History = null, bool? Explored = null, bool? Replanned = null);

/// <summary>Who made a decision, and what it planned and scored: what its recommendation carries.</summary>
public sealed record DecisionNote(string Brain, IReadOnlyList<string>? Plan = null, IReadOnlyList<Candidate>? Candidates = null);

/// <summary>
/// Harness for one run (docs: harness/routing). The host asks it as its combat, card-prompt and macro policy; it routes
/// each decision to exactly one brain, draws the brains' seeds from the run id and the decision's number (never from the
/// game), and logs every decision when asked to. A surface with no route throws: there is no fallback.
/// Card prompts go to the combat side when asked during a combat, to the macro side otherwise.
///
/// In SL the host also sends the current node's checkpoint and each decision taken in it (docs: protocol/wire §6), which
/// make the node's clone for the combat brain and, with <paramref name="clones"/>, for the macro side, which gets it
/// opened at each of its decisions (<see cref="MacroDecision.Clone"/>). In NoSL they are refused.
/// </summary>
public sealed class Dispatcher(
    string runId, PlayEnvironment env, CombatRoute combat, Route<IChoicePolicy> combatChoices, Route<IMacroPolicy> macro,
    Route<IChoicePolicy> macroChoices, bool log = false, Func<NodeClone, IMacroClone>? clones = null) : ICombatPolicy, IChoicePolicy, IMacroPolicy
{
    /// <summary>Every surface outside combat that has a route (docs: harness/routing §3).</summary>
    public static readonly IReadOnlySet<string> MacroSurfaces = new HashSet<string>
    {
        Surfaces.Map, Surfaces.Rewards, Surfaces.CardReward, Surfaces.Bundle, Surfaces.Event, Surfaces.Shop, Surfaces.Rest,
        Surfaces.Treasure, Surfaces.CrystalSphere,
    };

    private readonly ulong _runHash = ProtocolJson.Hash(runId);
    private readonly List<DecisionEntry> _log = [];
    private readonly Dictionary<string, int> _brains = [];
    private readonly List<string> _nodeTrace = [];
    private readonly MoveRecord _moves = new();
    private int _decisions;
    private NodeCheckpoint? _node;

    /// <summary>The run's decisions so far, when logging.</summary>
    public IReadOnlyList<DecisionEntry> Log => _log;

    public PlayEnvironment Env => env;

    /// <summary>The latest decision's brain, plan and candidates.</summary>
    public DecisionNote? Last { get; private set; }

    /// <summary>How many decisions each brain made so far.</summary>
    public IReadOnlyDictionary<string, int> Brains => _brains;

    /// <summary>
    /// How many combat decisions were searched again because a step of the brain's plan did not hold. With PIMC's
    /// --follow that is a turn revealing something; with the SL planner, a clone that differs from the real combat.
    /// </summary>
    public int Replans { get; private set; }

    /// <summary>The brain on each side of the routing table, for reports.</summary>
    public string Champion =>
        $"combat {combat.Brain}{(combat.Budget is { } b ? $" {b}" : "")}, combat prompts {combatChoices.Brain}, " +
        $"macro {macro.Brain}, macro prompts {macroChoices.Brain}";

    /// <summary>
    /// The current combat's public record of enemy moves (docs: harness §4): each enemy's intents at the first decision
    /// of each round. A combat starts over after any macro decision, or when the round goes back.
    /// </summary>
    public IReadOnlyList<EnemyTurns> History => _moves.History;

    /// <summary>The steps of a plan that are logged and shown.</summary>
    private const int ShownPlan = 256;

    /// <summary>What the combat brain answered for the first prompt of the action it just chose (<see cref="CombatAction.Pick"/>).</summary>
    private int? _pick;

    public CombatAction Choose(CombatDecision decision)
    {
        _pick = null;
        _moves.Witness(decision.Observation);
        var history = History;
        var (id, seed, clock) = Begin(combat.Brain);
        var result = combat.Decide(decision, seed, env == PlayEnvironment.SL ? Clone() : null, history);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!decision.Legal.Contains(result.First.Plain)) throw new InvalidOperationException($"{combat.Brain} chose {result.First}, which is not legal");
        if (result.Replanned) Replans++;
        // A plan that repeats a cycle may have thousands of steps left, at each of them.
        Last = new(combat.Brain, result.Plan.Count > 0 ? result.Plan.Take(ShownPlan).Select(a => a.ToString()).ToList() : null, result.Candidates.Count > 0 ? result.Candidates : null);
        if (log)
            _log.Add(new(runId, id, env.ToString(), Surfaces.Combat, combat.Brain, Hex(ProtocolJson.Hash(decision.Observation)), decision.Legal.Count,
                result.First.ToString(), Last.Plan, Last.Candidates, combat.Budget, seed, ms, Obs: decision.Observation, History: history,
                Explored: Mark(result.Explored), Replanned: Mark(result.Replanned)));
        _pick = result.First.Pick;
        return result.First.Plain;
    }

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        var choice = decision.Choice;
        var route = choice.Context.InCombat ? combatChoices : macroChoices;
        var (id, seed, clock) = Begin(route.Brain);
        if (!choice.Context.InCombat && MacroClone() is { } node) decision = decision.With(node);
        // The answer search played its line with, for the action just chosen; the host never sees it.
        var picks = choice.Context.InCombat && _pick is int pick && choice.Accepts([pick]) ? [pick] : route.Policy.Choose(decision);
        _pick = null;
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!choice.Accepts(picks)) throw new InvalidOperationException($"{route.Brain} gave [{string.Join(",", picks)}], which the prompt does not accept");
        Last = new(route.Brain);
        if (log)
            _log.Add(new(runId, id, env.ToString(), Surfaces.CardChoice, route.Brain, Hex(ProtocolJson.Hash(choice)), choice.Options.Count,
                string.Join(",", picks), null, null, null, seed, ms, Explored: Mark(route.Policy.Explored)));
        return picks;
    }

    public string Choose(MacroDecision decision)
    {
        _moves.Clear();
        var (id, seed, clock) = Begin(macro.Brain);
        if (!MacroSurfaces.Contains(decision.Surface)) throw new InvalidOperationException($"uncovered surface {decision.Surface}");
        if (MacroClone() is { } node) decision = decision.With(node);
        var action = macro.Policy.Choose(decision);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!decision.Legal.Contains(action)) throw new InvalidOperationException($"{macro.Brain} chose {action} on {decision.Surface}, which is not legal");
        var plan = macro.Policy.Plan(decision, action);
        Last = new(macro.Brain, plan.Count > 0 ? plan : null);
        if (log)
            _log.Add(new(runId, id, env.ToString(), decision.Surface, macro.Brain, Hex(ProtocolJson.Hash(decision.Observation)), decision.Legal.Count,
                action, Last.Plan, null, null, seed, ms, RunView: decision.Observation.Run,
                Explored: Mark(macro.Policy.Explored)));
        return action;
    }

    /// <summary>SL: the host arrived at a node; its decisions follow.</summary>
    public void Checkpoint(NodeCheckpoint checkpoint)
    {
        if (env != PlayEnvironment.SL) throw new InvalidOperationException($"a node checkpoint in {env}");
        if (checkpoint.NodeIndex <= (_node?.NodeIndex ?? -1)) throw new InvalidDataException($"node {checkpoint.NodeIndex} after node {_node!.NodeIndex}");
        _node = checkpoint;
        _nodeTrace.Clear();
    }

    /// <summary>SL: a decision taken in the current node.</summary>
    public void Act(NodeAction action)
    {
        if (env != PlayEnvironment.SL) throw new InvalidOperationException($"a node action in {env}");
        if (action.NodeIndex != _node?.NodeIndex || action.Seq != _nodeTrace.Count)
            throw new InvalidDataException($"node action {action.NodeIndex}:{action.Seq}, expected {_node?.NodeIndex}:{_nodeTrace.Count}");
        _nodeTrace.Add(action.Action);
    }

    /// <summary>SL with <c>clones</c>: the node's clone at this decision, for the macro side.</summary>
    private IMacroClone? MacroClone() => env == PlayEnvironment.SL && clones != null ? clones(Clone()) : null;

    private NodeClone Clone()
    {
        var node = _node ?? throw new InvalidOperationException("an SL decision before any node checkpoint");
        return new(node.Checkpoint, _nodeTrace.ToList(), node.Gauntlet);
    }

    private (int Id, ulong Seed, Stopwatch Clock) Begin(string brain)
    {
        _brains[brain] = _brains.GetValueOrDefault(brain) + 1;
        int id = _decisions++;
        return (id, Mix(_runHash, (ulong)id), Stopwatch.StartNew());
    }

    private static string Hex(ulong hash) => hash.ToString("x16");

    /// <summary>The log marks explored and replanned steps only.</summary>
    private static bool? Mark(bool explored) => explored ? true : null;

    /// <summary>SplitMix64 of <paramref name="a"/> stepped <paramref name="b"/> + 1 times.</summary>
    private static ulong Mix(ulong a, ulong b)
    {
        ulong z = a + 0x9E3779B97F4A7C15 * (b + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
