using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>How much search one decision may use: worlds, and actions simulated per world.</summary>
/// <param name="NodesPerWorld">PIMC: the actions simulated in each world's search of the turn. The SL planner: in each
/// search of one turn from one state.</param>
/// <param name="Beam">The SL planner's states kept from one turn to the next (<see cref="SlPlanner"/>); 0 for PIMC,
/// which does not read it. The planner has one world, and ignores <paramref name="Worlds"/>.</param>
/// <param name="Replan">The SL planner's turns of an unproved losing plan played before planning again; 0 plays it to its end.</param>
/// <param name="LossPlans">The most unproved losing plans the SL planner makes in a row, that is in one combat
/// (<see cref="SlRoute"/>); the last is played to its end. 0: no limit.</param>
/// <param name="EasyBeam">The SL planner's states kept when its greedy dive already wins the combat; 0:
/// <paramref name="Beam"/>.</param>
/// <param name="Elite">An elite fight's budget, where it differs (<see cref="For"/>); null: this one.</param>
/// <param name="Boss">A boss fight's budget, where it differs; null: this one.</param>
/// <param name="TurnDecay">What a line of the SL planner keeps of its end's value for each turn it ended, when the plan
/// is picked (<see cref="SlPlanner"/>); 1: all of it, as self-play and the tests plan. It is this budget's in every room.</param>
/// <param name="Solve">The combat solver's actions simulated at each decision before it answers (<see cref="SolverRoute"/>,
/// max): one solver a combat, begun from the beam's plan that the rest of the budget makes; 0: no solver, the beam
/// alone.</param>
public sealed record Budget(int Worlds = 8, int NodesPerWorld = 300, int Beam = 0, int Replan = 1, int LossPlans = 0, int EasyBeam = 0,
    Budget? Elite = null, Budget? Boss = null, double TurnDecay = 1, long Solve = 0)
{
    /// <summary>The budget of a fight in a room of this type (<see cref="CombatObservation.Room"/>: Monster, Elite or Boss).</summary>
    public Budget For(string? room) => room switch
    {
        "Elite" when Elite != null => Elite with { TurnDecay = TurnDecay },
        "Boss" when Boss != null => Boss with { TurnDecay = TurnDecay },
        _ => Elite == null && Boss == null ? this : this with { Elite = null, Boss = null },
    };

    /// <summary>The planner's budget at an effort level: beam, nodes per state and turn, the
    /// easy beam, the turns of an unproved losing plan played before planning again (0: to its end), and the losing
    /// plans in a row before the last is played to its end. Since 2026-11-18 each name is the budget of the level above
    /// it before (low was medium's, medium high's, high max's); max is the combat solver (<see cref="SolverRoute"/>,
    /// plans/2026-11-18-1412-max-solver.md), begun from high's beam, with <see cref="MaxSolve"/> nodes a decision.</summary>
    public static Budget Effort(string level) => level switch
    {
        "low" => new(NodesPerWorld: 300, Beam: 8, EasyBeam: 4, LossPlans: 3),
        "medium" => new(NodesPerWorld: 300, Beam: 8, Replan: 1),
        "high" => new(NodesPerWorld: 300, Beam: 16, Replan: 1),
        "max" => new(NodesPerWorld: 300, Beam: 16, Replan: 1, Solve: MaxSolve),
        _ => throw new ArgumentException($"no effort {level}: low, medium, high or max"),
    };

    /// <summary>max's solver nodes a decision (<see cref="Solve"/>): to be set by what the friend's replay measures
    /// (plans/2026-11-18-1412-max-solver.md, 2.5).</summary>
    public const long MaxSolve = 100_000;

    /// <summary>The effort levels, cheapest first.</summary>
    public static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "max"];

    /// <summary>The search's size: pimc's worlds by their nodes (8x300), the planner's beam by its nodes per state and
    /// turn (beam 16x300; beam 16xall for <see cref="SlPlanner.Unlimited"/>).</summary>
    public string Size => FormattableString.Invariant(
        $"{(Beam > 0 ? $"beam {Beam}" : $"{Worlds}")}x{(NodesPerWorld == SlPlanner.Unlimited ? "all" : NodesPerWorld.ToString(System.Globalization.CultureInfo.InvariantCulture))}");

    /// <summary>As the champion's name shows it: the size (<see cref="Size"/>), then the planner's easy beam,
    /// replan when not 1, loss plans when limited, the turn decay when not 1 and the solver's nodes a decision when it
    /// solves; then an elite's and a boss's budget where they differ.</summary>
    public override string ToString() => FormattableString.Invariant(
        $"{Size}{(Beam > 0 && EasyBeam > 0 ? $" easy {EasyBeam}" : "")}{(Beam > 0 && Replan != 1 ? $" replan {Replan}" : "")}{(Beam > 0 && LossPlans > 0 ? $" loss plans {LossPlans}" : "")}{(Beam > 0 && TurnDecay != 1 ? $" turn decay {TurnDecay}" : "")}{(Solve > 0 ? $" solve {Solve}" : "")}") +
        (Elite != null ? $" elite ({Elite})" : "") + (Boss != null ? $" boss ({Boss})" : "");
}

/// <summary>Searches every world from the decision's state: the best line after each of <paramref name="first"/>, in
/// that order, per world in world order, each first action with <paramref name="nodesPerFirst"/> nodes.</summary>
public delegate IReadOnlyList<IReadOnlyList<PlannedLine>> RootSearch(IReadOnlyList<CombatAction> first, int nodesPerFirst);

/// <param name="First">One of the legal actions; at a card prompt, one of its answers.</param>
/// <param name="Plan">The rest of the turn after <paramref name="First"/>, in the first world (for narration and highlight).</param>
/// <param name="Candidates">Every first action, best first.</param>
/// <param name="PlanHashes">When every world planned the same <paramref name="Plan"/> and expected the same observation
/// before each of its steps, the hashes of those observations; otherwise null.</param>
/// <param name="Replanned">Whether this search replaced a step of a plan that the combat did not show
/// (<see cref="TurnPlan"/>). In SL that means the clone differs from the real combat.</param>
/// <param name="Repeats">Whether the line repeats a cycle (<see cref="LoopSearch"/>): PIMC follows such a plan, since
/// searching again before each of hundreds of repeats would only find it again.</param>
/// <param name="Nodes">The SL planner's search behind the result: the actions it simulated (<see cref="SlPlan.Nodes"/>).
/// Two worlds that plan alike search alike, so the log keeps it.</param>
/// <param name="Turns">With <paramref name="Nodes"/>: the turns its beams searched.</param>
public sealed record CombatDecisionResult(CombatAction First, IReadOnlyList<CombatAction> Plan, IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<ulong>? PlanHashes = null, bool Replanned = false, bool Repeats = false, int? Nodes = null, int? Turns = null);

/// <summary>
/// The rest of the plan that a search made, played without searching again. A step is
/// played only while the combat shows the observation the plan expected before it. PIMC's plan is the rest of a turn
/// that every world planned the same: anything that comes to light (a draw, a random result) ends it, and the decision
/// is searched. The SL planner's plan is the rest of the combat in its one exact world, so it ends only with the
/// combat; a step that does not hold there means the world differs from the real combat. One per combat route.
///
/// A plan that is not kept still leaves the answers it starts with: the prompts that the action just chosen raises are
/// part of choosing it. PIMC's worlds are samples, so such an answer is given wherever the prompt takes it, without the
/// state it expected.
/// </summary>
/// <param name="all">Whether to keep every plan a search makes (the SL planner), or only one that repeats a cycle
/// (<see cref="CombatDecisionResult.Repeats"/>; PIMC).</param>
public sealed class TurnPlan(bool all = true)
{
    /// <summary>The steps left, each with the hash of the observation it expects; null: wherever it can be taken.</summary>
    private readonly Queue<(CombatAction Action, ulong? Hash)> _steps = new();

    /// <summary>The plan's next step if it still holds here, else <paramref name="search"/>, whose plan is kept; marked
    /// replanned if a step was left that did not hold.</summary>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<CombatDecisionResult> search)
    {
        bool pending = _steps.TryDequeue(out var step);
        if (pending && (step.Hash == null || step.Hash == ProtocolJson.Hash(observation)) && observation.Takes(step.Action, legal))
            return new(step.Action, [.. _steps.Select(s => s.Action)], [], _steps.All(s => s.Hash != null) ? [.. _steps.Select(s => s.Hash!.Value)] : null);
        _steps.Clear();
        var result = search();
        if (pending && step.Hash != null) result = result with { Replanned = true };
        if ((all || result.Repeats) && result.PlanHashes is { } hashes)
            foreach (var (action, hash) in result.Plan.Zip(hashes)) _steps.Enqueue((action, hash));
        else
            foreach (var answer in result.Plan.TakeWhile(a => a.Kind == CombatActionKind.Pick)) _steps.Enqueue((answer, null));
        return result;
    }
}

/// <summary>
/// Combat brain v0: PIMC over K worlds. In each world, a depth-first search over this turn's
/// action sequences, with ending the turn an option at every step, one action of each that are the same
/// (<see cref="Ordered"/>); states are merged by <see cref="StateKey"/> and the world's hidden state
/// (<see cref="ICombatWorld.HiddenKey"/>), so each transposition is expanded once, and two lines that look the same but
/// drew or rolled differently are not. A line ends when the turn ends (after the enemies'
/// turn) or the combat does, and is scored by the leaf (<see cref="ICombatLeaf"/>, the combat net). Each first action is scored
/// by its best line in each world, then averaged over the worlds; never the max across worlds, which is strategy fusion. Only the first action is played: the
/// caller asks again after it. In SL there is one exact world, so this is plain planning.
///
/// A card prompt inside a world is a state, and its answers are its actions (<see cref="PromptAnswers"/>); the answers a
/// line gives right after the first action go with it (<see cref="TurnPlan"/>). A prompt the combat shows that no plan
/// answered gets <see cref="FirstPicks"/>: a world is built from an observation, and none can be built in the middle of
/// an action.
///
/// Each world's nodes go to the first actions evenly, so each gets a fair look (<see cref="Root"/>); inside a subtree
/// the search stops expanding when its share is spent, and what is left is scored
/// as ending the turn there (that option is tried first). A state cut short so is not kept for its transpositions: its
/// value is only a lower bound, and another first action reaching it searches it with its own share.
///
/// The worlds are independent, so a decision is three steps that can run apart: <see cref="Ordered"/> first actions, then
/// <see cref="SearchWorld"/> for each world wherever that world lives (this process, or a worker), then merging by
/// <see cref="Root"/>. <see cref="Decide"/> runs them all here.
/// </summary>
public sealed class PimcBrain(ICombatLeaf leaf)
{
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>), for the leaves.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, IWorldSource worlds, Budget budget, ulong seed,
        IReadOnlyList<EnemyTurns>? history = null)
    {
        if (Unplanned(observation) is { } unplanned) return unplanned;
        if (legal.Count == 1) return Only(legal[0]);
        var live = worlds.Worlds(budget.Worlds, seed);
        var roots = live.Select(w => w.Snapshot()).ToList();
        return Root(Ordered(legal, observation), budget, (first, nodes) => [.. live.Select((w, k) =>
        {
            w.Restore(roots[k]);
            return SearchWorld(w, observation.Round, first, nodes, history);
        })]);
    }

    /// <summary>The answer when there is nothing to choose: no search, so no candidates.</summary>
    public static CombatDecisionResult Only(CombatAction action) => new(action, [], []);

    /// <summary>The one thing there is to do at a decision, if it is so: the only legal play, or a prompt's only answer.</summary>
    public static CombatAction? Forced(CombatObservation obs, IReadOnlyList<CombatAction> legal)
    {
        if (obs.Prompt is not { } prompt) return legal.Count == 1 ? legal[0] : null;
        return prompt.Answers().Take(2).ToList() is [var only] ? CombatAction.Pick(only) : null;
    }

    /// <summary>PIMC's answer at a card prompt: none of its worlds stands there (<see cref="FirstPicks"/>).</summary>
    public static CombatDecisionResult? Unplanned(CombatObservation obs) => obs.Prompt is { } prompt ? Only(FirstPicks.Of(prompt)) : null;

    /// <summary>One world's part of a decision: the best line after each of <paramref name="first"/>, in that order.</summary>
    /// <param name="history">The combat's record of enemy moves up to the decision, which every leaf of this turn follows.</param>
    public IReadOnlyList<PlannedLine> SearchWorld(ICombatWorld world, int round, IReadOnlyList<CombatAction> first, int nodesPerFirst,
        IReadOnlyList<EnemyTurns>? history = null) =>
        new WorldSearch(world, round, leaf, history ?? [], nodesPerFirst).Root(first);

    /// <summary>Shares each world's nodes evenly over the first actions and merges the worlds' lines.</summary>
    public static CombatDecisionResult Root(IReadOnlyList<CombatAction> first, Budget budget, RootSearch search) =>
        Merge(first, search(first, Math.Max(1, budget.NodesPerWorld / first.Count)));

    /// <summary>Scores each first action by its mean over the worlds; the plan is the best action's line in the first
    /// world. The candidates are listed best first.</summary>
    private static CombatDecisionResult Merge(IReadOnlyList<CombatAction> first, IReadOnlyList<IReadOnlyList<PlannedLine>> worlds)
    {
        var candidates = first.Select((action, a) => Summarize(action.ToString(), [.. worlds.Select(lines => lines[a])])).ToList();
        var order = Enumerable.Range(0, first.Count).OrderByDescending(a => candidates[a].Value).ToList();
        var line = worlds[0][order[0]];
        bool agreed = line.Hashes != null && worlds.All(lines =>
            lines[order[0]].Actions.SequenceEqual(line.Actions) && lines[order[0]].Hashes!.SequenceEqual(line.Hashes));
        return new(first[order[0]], [.. line.Actions.Skip(1)], [.. order.Select(a => candidates[a])], agreed ? line.Hashes!.Skip(1).ToList() : null, Repeats: line.Repeats);
    }

    /// <summary>
    /// The actions search tries at <paramref name="obs"/>, in its order: ending the turn first (the fallback a spent budget
    /// leaves), then cards in hand order, then potions. Of the actions that are the same,
    /// only the first: the same card, by <see cref="ProtocolJson.CardKey"/>, or the same potion, at the same target.
    ///
    /// The Foul Potion is never thrown in a fight (<see cref="Foul"/>): the user's rule (2026-11-16). In a fight it hits
    /// every creature but pets for 12, the player too; at a merchant it is worth 100 gold (MacroBrain.Offered throws it
    /// there first). V rated it below an empty slot everywhere, so the planner threw it in the next fight: in v10g1's A0-A9
    /// runs 285 times, 98 % of those held at a fight's start, 94 % with no card played after it.
    /// </summary>
    public static List<CombatAction> Ordered(IReadOnlyList<CombatAction> legal, CombatObservation obs)
    {
        var seen = new HashSet<string>();
        return [.. legal.OrderBy(a => a.Kind switch { CombatActionKind.EndTurn => 0, CombatActionKind.PlayCard => 1, _ => 2 })
            .Where(a => !(a.Kind == CombatActionKind.UsePotion && obs.Potions[a.PotionSlot!.Value] == Foul))
            .Where(a => seen.Add(Same(a, obs)))];
    }

    /// <summary>The Foul Potion's id.</summary>
    public const string Foul = "FOUL_POTION";

    /// <summary>What makes an action the one it is to search: its card's key or its potion's id, and its target.</summary>
    private static string Same(CombatAction action, CombatObservation obs) => action.Kind switch
    {
        CombatActionKind.PlayCard => "play " + ProtocolJson.CardKey(obs.Hand.FirstOrDefault(c => c.Uid == action.Card)
            ?? throw new InvalidOperationException($"{action} plays a card not in the hand")) + $" {action.Target}",
        CombatActionKind.UsePotion => $"potion {obs.Potions[action.PotionSlot!.Value]} {action.Target}",
        _ => action.ToString(),
    };

    /// <summary>
    /// The actions search tries at a state of <paramref name="world"/>: where the player can play, the plays
    /// (<see cref="Ordered"/>); where a card prompt waits, its answers, as many as <paramref name="most"/> nodes can try
    /// (<see cref="PromptAnswers"/>).
    /// </summary>
    /// <param name="cut">Whether answers were left out.</param>
    public static List<CombatAction> Actions(ICombatWorld world, CombatObservation obs, int most, out bool cut)
    {
        cut = false;
        return obs.Prompt is { } prompt ? PromptAnswers.Of(prompt, most, out cut) : Ordered(world.Legal(), obs);
    }

    /// <summary>A state as search tells states apart (<see cref="ProtocolJson.StateKey"/>).</summary>
    public static ulong StateKey(CombatObservation obs) => ProtocolJson.StateKey(obs);

    /// <param name="lines">The action's best line in each world.</param>
    private static Candidate Summarize(string action, IReadOnlyList<PlannedLine> lines)
    {
        var values = lines.Select(l => l.Value).ToArray();
        double mean = values.Average();
        double ci = values.Length < 2 ? 0 : 1.96 * Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1) / values.Length);
        return new(action, mean, ci, values.Length, [.. values.Select(v => Math.Round(v, 4))]);
    }

    private sealed class WorldSearch(ICombatWorld world, int round, ICombatLeaf leaf, IReadOnlyList<EnemyTurns> history, int nodesPerFirst)
    {
        private readonly Dictionary<(ulong Obs, ulong Hidden), PlannedLine> _memo = [];
        private readonly List<LoopPoint> _path = [];
        private readonly List<CombatAction> _actions = [];
        private readonly List<ulong> _hashes = [];
        private readonly LoopSearch _loops = new(world, round, (status, obs, actions, hashes) =>
        {
            var outcome = leaf.Outcome(status, obs, world.Run(), history);
            return new TurnEnd([.. actions], [.. hashes], status, outcome.Value, 0, history, outcome.Win, outcome.Hp);
        });
        private WorldSnapshot _root = new([]);
        private ulong _rootHash;
        /// <summary>The nodes spent on the first action so far, and where the line being searched must stop.</summary>
        private int _nodes, _limit;
        /// <summary>States cut short by the budget so far: a state whose subtree has none is searched in full.</summary>
        private int _cuts;

        public PlannedLine[] Root(IReadOnlyList<CombatAction> first)
        {
            _root = world.Snapshot();
            var obs = world.Observe();
            _rootHash = world.Hash(obs);
            _path.Add(new(obs, 0, _root));
            var lines = new PlannedLine[first.Count];
            for (int a = 0; a < first.Count; a++)
            {
                if (a > 0) world.Restore(_root);
                (_nodes, _limit) = (0, nodesPerFirst);
                lines[a] = First(first[a]);
            }
            return lines;
        }

        /// <summary>The best line that starts with <paramref name="action"/>: the search's, then the cycles it found. A
        /// prompt the action raises shares the action's nodes evenly over its answers.</summary>
        private PlannedLine First(CombatAction action)
        {
            var after = After(action, _rootHash, nodesPerFirst);
            var line = new PlannedLine(after.Value, [action, .. after.Actions], _nodes, [_rootHash, .. after.Hashes!]);
            foreach (var end in _loops.RunFound())
                if (end.Value > line.Value) line = new(end.Value, end.Actions, _nodes, end.Hashes, Repeats: true);
            if (!double.IsNegativeInfinity(line.Value)) return line;
            // The move was a cycle at once and no budget was left for it: what is left is ending the turn, with the first
            // answer to any prompt on the way.
            world.Restore(_root);
            var actions = new List<CombatAction>();
            var hashes = new List<ulong>();
            void Play(CombatAction next)
            {
                for (CombatAction? step = next; step is { } taken; step = world.Status == CombatStatus.InProgress && world.Observe().Prompt is { } prompt ? FirstPicks.Of(prompt) : null)
                {
                    hashes.Add(world.Hash(world.Observe()));
                    world.Execute(taken);
                    actions.Add(taken);
                }
            }
            Play(action);
            if (world.Status == CombatStatus.InProgress && world.Observe().Round == round) Play(CombatAction.EndTurn);
            return new(leaf.Value(world.Status, world.Observe(), world.Run(), history), actions, _nodes, hashes);
        }

        /// <summary>Plays <paramref name="action"/> and returns the best line from there.</summary>
        /// <param name="shareTo">Where the action's share of the nodes ends, if a prompt it raises is to share it.</param>
        private PlannedLine After(CombatAction action, ulong hash, int? shareTo = null)
        {
            _nodes++;
            world.Execute(action);
            _actions.Add(action);
            _hashes.Add(hash);
            var line = Reached(action, shareTo);
            _actions.RemoveAt(_actions.Count - 1);
            _hashes.RemoveAt(_hashes.Count - 1);
            return line;
        }

        /// <summary>A line ends with the combat, or where the player can play again after the turn's end; a prompt on the
        /// way is answered first.</summary>
        private PlannedLine Reached(CombatAction action, int? shareTo)
        {
            var status = world.Status;
            var obs = world.Observe();
            if (status == CombatStatus.InProgress && obs.Prompt != null) return Best(obs, shareTo);
            if (status != CombatStatus.InProgress || action.Kind == CombatActionKind.EndTurn || obs.Round != round)
                return new(leaf.Value(status, obs, world.Run(), history), [], 0, []);
            return _loops.Intercept(_path, obs, _actions, _hashes) ? new(double.NegativeInfinity, [], 0, []) : Best(obs);
        }

        /// <summary>The best line from the current state: where the player is still on this turn, or at a card prompt.</summary>
        /// <param name="shareTo">Where the state's share of the nodes ends, when its actions share it evenly; null where
        /// they are tried depth first.</param>
        private PlannedLine Best(CombatObservation obs, int? shareTo = null)
        {
            ulong hash = world.Hash(obs);
            var key = (world.StateKey(obs), world.HiddenKey());
            if (_memo.TryGetValue(key, out var known)) return known;
            var here = world.Snapshot();
            // A prompt stands in the middle of an action: a cycle is a move between states where the player can play.
            bool plays = obs.Prompt == null;
            if (plays) _path.Add(new(obs, _actions.Count, here));
            var best = new PlannedLine(double.NegativeInfinity, [], 0, []);
            bool moved = false;
            int cuts = _cuts, from = _nodes;
            var todo = Actions(world, obs, (shareTo ?? _limit) - _nodes, out bool left);
            if (left) _cuts++;
            for (int i = 0; i < todo.Count; i++)
            {
                var action = todo[i];
                int? to = null;
                if (shareTo is { } end) _limit = (to = Math.Max(_nodes + 1, from + (int)((long)(end - from) * (i + 1) / todo.Count))).Value;
                else if (moved && _nodes >= _limit)
                {
                    _cuts++;
                    break;
                }
                if (moved) world.Restore(here);
                moved = true;
                var after = After(action, hash, to);
                if (after.Value > best.Value) best = new(after.Value, [action, .. after.Actions], 0, [hash, .. after.Hashes!]);
            }
            if (plays) _path.RemoveAt(_path.Count - 1);
            if (_cuts == cuts) _memo[key] = best;
            return best;
        }
    }
}
