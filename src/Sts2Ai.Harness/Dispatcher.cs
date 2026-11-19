using System.Diagnostics;
using Sts2Ai.Brain;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// A combat brain as Harness calls it: the decision, the seed Harness drew for it, in SL the current node's clone
/// (null in NoSL), and the combat's record of enemy moves so far (<see cref="Dispatcher.History"/>). A card prompt
/// during a combat is one of its decisions: the observation shows the prompt (<see cref="CombatObservation.Prompt"/>),
/// no play is legal, and the answer is a <see cref="CombatAction.Pick"/>.
/// </summary>
/// <param name="Budget">What the brain may spend, for the log; null for a policy that does not search.</param>
/// <param name="Costs">What the brain's plans have cost so far in this run; null for one that keeps no count.</param>
/// <param name="Rest">Told that no decision of the combat comes now (it ended, or the run did): a brain that searches
/// between decisions stops (<see cref="SolverRoute"/>).</param>
public sealed record CombatRoute(string Brain, Func<CombatDecision, ulong, NodeClone?, IReadOnlyList<EnemyTurns>, CombatDecisionResult> Decide,
    Budget? Budget = null, Func<PlanTally>? Costs = null, Action? Rest = null)
{
    /// <summary>A placeholder policy: it gives its pick and no candidates, and at a card prompt the first options
    /// (<see cref="FirstPicks"/>).</summary>
    public static CombatRoute Of(string brain, ICombatPolicy policy) => new(brain, (decision, _, _, _) =>
        new(decision.Observation.Prompt is { } prompt ? FirstPicks.Of(prompt) : policy.Choose(decision), [], []));
}

/// <summary>A brain for one side of the routing table, with its name for the log.</summary>
public sealed record Route<T>(string Brain, T Policy);

/// <param name="DecisionId">The decision's number in its run, from 0, card prompts included.</param>
/// <param name="ObsHash">The hash of what the brain was shown: the combat observation, the run observation, or the card prompt.</param>
/// <param name="Legal">How many legal actions (or prompt options) there were.</param>
/// <param name="Chosen">The action in text form; for a prompt, the picked option indexes.</param>
/// <param name="Plan">What the brain planned after <paramref name="Chosen"/>: the rest of the turn in combat, the rest of
/// the act's route on the map.</param>
/// <param name="Obs">On a combat decision, the observation the brain was shown: the combat net's input.</param>
/// <param name="RunView">On a macro decision, the run the brain was shown: what V is trained on.</param>
/// <param name="History">On a combat decision, what each enemy of the combat showed at the start of each round so far,
/// this one's included (<see cref="Dispatcher.History"/>).</param>
/// <param name="Explored">True when <paramref name="Chosen"/> was drawn by self-play exploration
/// (<see cref="Uniform"/>); absent otherwise.</param>
/// <param name="Replanned">True when the combat brain searched again because a step of its plan did not hold
/// (<see cref="CombatDecisionResult.Replanned"/>); absent otherwise.</param>
/// <param name="Nodes">Where the SL planner planned: the actions its search simulated
/// (<see cref="CombatDecisionResult.Nodes"/>); absent otherwise.</param>
/// <param name="Turns">With <paramref name="Nodes"/>: the turns its beams searched.</param>
/// <param name="Proved">True when the solver proved its pick the best there is (<see cref="CombatDecisionResult.Proved"/>);
/// absent otherwise.</param>
/// <param name="Event">NoSL, at an event, for V's training: in self-play each option whose result its button does not
/// show with where NoSL's line from it ends (<see cref="MacroAnswer.Event"/>), and the option taken if it is one
/// (<see cref="EventLine.Taken"/>).</param>
public sealed record DecisionEntry(
    string Run, int DecisionId, string Env, string Surface, string Brain, string ObsHash, int Legal, string Chosen,
    IReadOnlyList<string>? Plan, IReadOnlyList<Candidate>? Candidates, Budget? Budget, ulong? Seed, double ElapsedMs,
    CombatObservation? Obs = null, RunView? RunView = null,
    IReadOnlyList<EnemyTurns>? History = null, bool? Explored = null, bool? Replanned = null, int? Nodes = null, int? Turns = null,
    IReadOnlyList<EventLine>? Event = null, bool? Proved = null);

/// <summary>Who made a decision, and what it planned and scored: what its recommendation carries.</summary>
public sealed record DecisionNote(string Brain, IReadOnlyList<string>? Plan = null, IReadOnlyList<Candidate>? Candidates = null, long? Nodes = null,
    bool? Proved = null);

/// <summary>
/// Harness for one run. The host asks it as its combat, card-prompt and macro policy; it routes
/// each decision to exactly one brain, draws the brains' seeds from the run id and the decision's number (never from the
/// game), and logs every decision when asked to. A surface with no route throws: there is no fallback.
/// A card prompt asked during a combat is the combat brain's, as a decision of the combat (<see cref="CombatRoute"/>);
/// any other goes to the macro side.
///
/// In SL the host also sends the current node's checkpoint and each decision taken in it, which make the node's clone
/// for the combat brain and, with <paramref name="clones"/>, for the macro side, which gets it opened at each of its
/// decisions (<see cref="MacroDecision.Clone"/>).
///
/// In NoSL self-play with <paramref name="clones"/> the host sends them too, for the log alone: at an event the macro
/// side gets the node's clone as <see cref="MacroDecision.Lines"/>, on which it plays the options out for V's training
/// (<see cref="Sts2Ai.Brain.Macro.EventLines"/>); no answer is valued on it, and the combat brain gets none. Without
/// <paramref name="clones"/> NoSL refuses them (<see cref="TakesNodes"/>).
///
/// With <paramref name="explain"/> (a host that shows the answers: the mod), each map option the macro brain scored
/// carries the route it would take after it.
/// </summary>
public sealed class Dispatcher(
    string runId, PlayEnvironment env, CombatRoute combat, Route<IMacroPolicy> macro, Route<IChoicePolicy> macroChoices, bool log = false, Func<NodeClone, IMacroClone>? clones = null, bool explain = false)
    : ICombatPolicy, IChoicePolicy, IMacroPolicy
{
    /// <summary>Every surface outside combat that has a route.</summary>
    public static readonly IReadOnlySet<string> MacroSurfaces = new HashSet<string>
    {
        Surfaces.Map, Surfaces.Rewards, Surfaces.CardReward, Surfaces.Bundle, Surfaces.Event, Surfaces.Shop, Surfaces.Rest,
        Surfaces.Treasure, Surfaces.CrystalSphere,
    };

    private readonly ulong _runHash = ProtocolJson.Hash(runId);
    private readonly List<DecisionEntry> _log = [];
    private readonly Dictionary<string, int> _brains = [];
    private IReadOnlyList<string> _nodeTrace = [];
    private readonly MoveRecord _moves = new();
    private int _decisions;
    private NodeCheckpoint? _node;

    /// <summary>The run's decisions so far, when logging.</summary>
    public IReadOnlyList<DecisionEntry> Log => _log;

    /// <summary>How many decisions each brain made so far.</summary>
    public IReadOnlyDictionary<string, int> Brains => _brains;

    /// <summary>
    /// How many combat decisions were searched again because a step of the brain's plan did not hold. With PIMC that is a
    /// plan repeating a cycle that something revealed broke; with the SL planner, a clone that differs from the real combat.
    /// </summary>
    public int Replans { get; private set; }

    /// <summary>Combat decisions since the last decision outside combat: 0 before a combat's first.</summary>
    private int _fought;
    private int _promptOpens, _promptAfterPlan, _promptReplanned;

    /// <summary>What the combat brain's plans cost in this run so far and where they were rooted; null for a brain
    /// that keeps no count.</summary>
    public PlanTally? Search => combat.Costs?.Invoke() is not { } costs ? null : costs with
    {
        Replans = Replans, PromptOpens = _promptOpens, PromptAfterPlan = _promptAfterPlan, PromptReplanned = _promptReplanned,
    };

    /// <summary>The brain on each side of the routing table, for reports.</summary>
    public string Champion =>
        $"combat {combat.Brain}{(combat.Budget is { } b ? $" {b}" : "")}, combat prompts first, macro {macro.Brain}, macro prompts {macroChoices.Brain}";

    /// <summary>
    /// The current combat's public record of enemy moves: each enemy's intents at the first decision
    /// of each round. A combat starts over after any macro decision, or when the round goes back.
    /// </summary>
    public IReadOnlyList<EnemyTurns> History => _moves.History;

    /// <summary>The steps of a plan that are logged and shown.</summary>
    private const int ShownPlan = 256;

    public CombatAction Choose(CombatDecision decision) => Decide(decision).Action;

    public ChoiceAnswer Choose(ChoiceDecision decision) => Decide(decision).Answer;

    public MacroAnswer Choose(MacroDecision decision) => Decide(decision).Answer;

    public IReadOnlyList<MacroAction> Plan(MacroDecision decision, MacroAction action) => macro.Policy.Plan(decision, action);

    /// <summary>No decision comes for now: the run ended, or its host went. A combat brain searching between decisions
    /// stops (<see cref="CombatRoute.Rest"/>).</summary>
    public void Rest() => combat.Rest?.Invoke();

    /// <summary>The host played the answer given at <paramref name="state"/> (<see cref="MacroAnswer.State"/>,
    /// <see cref="ChoiceAnswer.State"/>), a macro decision's or a card prompt's: each macro route hears it, once.</summary>
    public void Played(ulong state)
    {
        macro.Policy.Played(state);
        if (!ReferenceEquals(macroChoices.Policy, macro.Policy)) macroChoices.Policy.Played(state);
    }

    /// <summary>A combat decision, with who made it and what it planned and scored.</summary>
    public (CombatAction Action, DecisionNote Note) Decide(CombatDecision decision)
    {
        var (result, note, id, seed, ms, history) = Fight(decision);
        if (decision.Observation.Prompt != null || !decision.Legal.Contains(result.First))
            throw new InvalidOperationException($"{combat.Brain} chose {result.First}, which is not legal");
        if (log)
            _log.Add(new(runId, id, env.ToString(), Surfaces.Combat, combat.Brain, Hex(ProtocolJson.Hash(decision.Observation)), decision.Legal.Count,
                result.First.ToString(), note.Plan, note.Candidates, combat.Budget?.For(decision.Observation.Room), seed, ms, Obs: decision.Observation, History: history,
                Replanned: Mark(result.Replanned), Nodes: result.Nodes, Turns: result.Turns, Proved: Mark(result.Proved)));
        return (result.First, note);
    }

    /// <summary>The combat brain at a decision of a combat, a card prompt's included: its answer, its note, and what the log needs.</summary>
    private (CombatDecisionResult Result, DecisionNote Note, int Id, ulong Seed, double Ms, IReadOnlyList<EnemyTurns> History) Fight(CombatDecision decision)
    {
        _moves.Witness(decision.Observation);
        var history = History;
        var (id, seed, clock) = Begin(combat.Brain);
        var result = combat.Decide(decision, seed, env == PlayEnvironment.SL ? Clone() : null, history);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (result.Replanned) Replans++;
        // A search leaves candidates, a planned step and a forced answer none: so a plan was made here, at a prompt.
        if (decision.Observation.Prompt != null && result.Candidates.Count > 0)
            _ = result.Replanned ? _promptReplanned++ : _fought == 0 ? _promptOpens++ : _promptAfterPlan++;
        _fought++;
        return (result, Note(result), id, seed, ms, history);
    }

    /// <summary>A combat answer as the host is told it: who made it, what it planned and what it scored.</summary>
    public DecisionNote Note(CombatDecisionResult result) =>
        // A plan that repeats a cycle may have thousands of steps left, at each of them.
        new(combat.Brain, result.Plan.Count > 0 ? result.Plan.Take(ShownPlan).Select(a => a.ToString()).ToList() : null,
            result.Candidates.Count > 0 ? result.Candidates : null, result.Nodes, result.Proved ? true : null);

    /// <summary>A card prompt's answer, with who gave it and what it scored. During a combat the prompt is a decision of
    /// the combat, the combat brain's (<see cref="CombatRoute"/>); the host needs the combat it is asked in for that. A
    /// fight the run's rule wins at its start (<see cref="RunView.AutoWin"/>) is no combat to decide: a prompt asked in
    /// it, before the win, is the macro side's, as one outside combat.</summary>
    public (ChoiceAnswer Answer, DecisionNote Note) Decide(ChoiceDecision decision)
    {
        var choice = decision.Choice;
        if (choice.Context.InCombat && Fought(decision) is var (around, shown))
        {
            var (result, note, at, drawn, took, _) = Fight(new CombatDecision([], () => around with { Prompt = choice }, _ => shown,
                early: decision.Early, hurry: decision.Hurry, after: decision.After));
            if (result.First.Kind != CombatActionKind.Pick || !choice.Accepts(result.First.Picks))
                throw new InvalidOperationException($"{combat.Brain} gave {result.First}, which the prompt does not accept");
            if (log)
                _log.Add(new(runId, at, env.ToString(), Surfaces.CardChoice, combat.Brain, Hex(ProtocolJson.Hash(choice)), choice.Options.Count,
                    result.First.Answer!, note.Plan, note.Candidates, combat.Budget?.For(around.Room), drawn, took, Replanned: Mark(result.Replanned),
                    Nodes: result.Nodes, Turns: result.Turns, Proved: Mark(result.Proved)));
            // The plan goes on with plays of the combat, which a host showing the prompt's screen has no cards to name.
            return (new([.. result.First.Picks]), note with { Plan = null });
        }
        combat.Rest?.Invoke();
        var (id, seed, clock) = Begin(macroChoices.Brain);
        if (MacroClone() is { } node) decision = decision.With(node);
        var answer = macroChoices.Policy.Choose(decision);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!choice.Accepts(answer.Picks)) throw new InvalidOperationException($"{macroChoices.Brain} gave [{string.Join(",", answer.Picks)}], which the prompt does not accept");
        if (log)
            _log.Add(new(runId, id, env.ToString(), Surfaces.CardChoice, macroChoices.Brain, Hex(ProtocolJson.Hash(choice)), choice.Options.Count,
                string.Join(",", answer.Picks), null, null, null, seed, ms, Explored: Mark(answer.Explored)));
        return (answer, new(macroChoices.Brain, null, answer.Scored.Count > 0 ? answer.Scored : null));
    }

    /// <summary>The combat a card prompt is asked in and the observation that shows it, if the run fights that combat.</summary>
    private static (CombatObservation Around, Observation Shown)? Fought(ChoiceDecision decision)
    {
        if (decision.Observation is not { Screen: CardChoiceScreen { Combat: { } around } } shown)
            throw new InvalidOperationException("a card prompt during a combat, and the host does not show the combat");
        return shown.Run.WinsAtStart(around.Room) ? null : (around, shown);
    }

    /// <summary>A decision outside combat, with who made it and what it planned and scored.</summary>
    public (MacroAnswer Answer, DecisionNote Note) Decide(MacroDecision decision)
    {
        _moves.Clear();
        _fought = 0;
        combat.Rest?.Invoke();
        var (id, seed, clock) = Begin(macro.Brain);
        if (!MacroSurfaces.Contains(decision.Surface)) throw new InvalidOperationException($"uncovered surface {decision.Surface}");
        if (MacroClone() is { } node) decision = decision.With(node);
        else if (LinesClone(decision) is { } lines) decision = decision.WithLines(lines);
        var answer = macro.Policy.Choose(decision);
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!decision.Legal.Contains(answer.Action)) throw new InvalidOperationException($"{macro.Brain} chose {answer.Action} on {decision.Surface}, which is not legal");
        static List<string>? Text(IReadOnlyList<MacroAction> route) => route.Count > 0 ? [.. route.Select(a => a.ToString())] : null;
        var plan = Text(macro.Policy.Plan(decision, answer.Action));
        if (explain && decision.Surface == Surfaces.Map)
            answer = answer with { Scored = [.. answer.Scored.Select(c => c with { Plan = Text(macro.Policy.Plan(decision, MacroAction.Parse(c.Action))) ?? [] })] };
        if (log)
            _log.Add(new(runId, id, env.ToString(), decision.Surface, macro.Brain, Hex(ProtocolJson.Hash(decision.Observation)), decision.Legal.Count,
                answer.Action.ToString(), plan, null, null, seed, ms, RunView: decision.Observation.Run,
                Explored: Mark(answer.Explored), Event: Events(decision.Observation.Screen, answer)));
        return (answer, new(macro.Brain, plan, answer.Scored.Count > 0 ? answer.Scored : null));
    }

    /// <summary>
    /// NoSL, at an event, for the log: the lines of the options played out (<see cref="MacroAnswer.Event"/>), and the
    /// option taken if its result was not shown (it was valued pending, <see cref="ScreenAfterstates.Pends"/>), marked
    /// <see cref="EventLine.Taken"/>: V learns it from what followed in the run. Null in SL and otherwise.
    /// </summary>
    private IReadOnlyList<EventLine>? Events(ScreenView screen, MacroAnswer answer)
    {
        if (env != PlayEnvironment.NoSL || screen is not EventScreen events) return null;
        var lines = answer.Event ?? [];
        if (!ScreenAfterstates.Pends(events, answer.Action)) return lines.Count > 0 ? lines : null;
        int taken = answer.Action.Index;
        return lines.Any(l => l.Option.Index == taken)
            ? [.. lines.Select(l => l.Option.Index == taken ? l with { Taken = true } : l)]
            : [.. lines, new EventLine(events.Options.First(o => o.Index == taken), Taken: true)];
    }

    /// <summary>
    /// Whether the host sends the node's checkpoints and the steps since (<see cref="Checkpoint"/>, <see cref="Steps"/>):
    /// in SL always; in NoSL only for the event lines of self-play's log (<c>clones</c>).
    /// </summary>
    public bool TakesNodes => env == PlayEnvironment.SL || clones != null;

    /// <summary>A new checkpoint (one of the game's saves); the decisions after it follow.</summary>
    public void Checkpoint(NodeCheckpoint checkpoint)
    {
        if (!TakesNodes) throw new InvalidOperationException($"a node checkpoint in {env}");
        if (checkpoint.NodeIndex <= (_node?.NodeIndex ?? -1)) throw new InvalidDataException($"node {checkpoint.NodeIndex} after node {_node!.NodeIndex}");
        _node = checkpoint;
        _nodeTrace = [];
    }

    /// <summary>
    /// The steps taken since the latest checkpoint, whole, before a decision (<see cref="DecisionRequest.Trace"/>): a
    /// decision comes with them when the host sends nodes (<see cref="TakesNodes"/>), and never otherwise.
    /// </summary>
    public void Steps(IReadOnlyList<string>? steps)
    {
        if (TakesNodes) _nodeTrace = steps ?? throw new InvalidDataException($"a decision in {env} without the steps since its checkpoint");
        else if (steps != null) throw new InvalidOperationException($"node steps in {env}");
    }

    /// <summary>SL with <c>clones</c>: the node's clone at this decision, for the macro side.</summary>
    private IMacroClone? MacroClone() => env == PlayEnvironment.SL && clones != null ? clones(Clone()) : null;

    /// <summary>NoSL with <c>clones</c>, at an event: the node's clone for the log's event lines alone.</summary>
    private IMacroClone? LinesClone(MacroDecision decision) =>
        env == PlayEnvironment.NoSL && clones != null && decision.Observation.Screen is EventScreen ? clones(Clone()) : null;

    private NodeClone Clone()
    {
        var node = _node ?? throw new InvalidOperationException($"a decision in {env} before any node checkpoint");
        return new(node.Checkpoint, [.. _nodeTrace], node.AutoWin);
    }

    private (int Id, ulong Seed, Stopwatch Clock) Begin(string brain)
    {
        _brains[brain] = _brains.GetValueOrDefault(brain) + 1;
        int id = _decisions++;
        return (id, Mix(_runHash, (ulong)id), Stopwatch.StartNew());
    }

    private static string Hex(ulong hash) => hash.ToString("x16");

    /// <summary>The log marks explored, replanned and proved steps only.</summary>
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
