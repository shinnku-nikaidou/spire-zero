using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>A beam state as a turn's search needs it: the actions from the decision to it, and the record of enemy
/// moves up to it, for the leaves.</summary>
/// <param name="LifeBounds">By the potions held, the most HP plus max HP a win found ends the combat with: a line of the
/// turn stops at a state holding those potions that cannot end it with as much (<see cref="LifeBound"/>).</param>
public sealed record PlanJob(IReadOnlyList<CombatAction> Path, IReadOnlyList<EnemyTurns> History, IReadOnlyDictionary<string, int>? LifeBounds = null);

/// <summary>Searches one turn from each job's state (<see cref="SlPlanner.SearchTurn"/>), in job order, wherever the
/// world lives (this process, or a worker), each with <paramref name="nodes"/> nodes, keeping
/// <paramref name="keep"/> states.</summary>
public delegate IReadOnlyList<TurnSearch> TurnSearcher(IReadOnlyList<PlanJob> jobs, int nodes, int keep);

/// <summary>The SL planner's answer, and how its search went. A losing line's plan stops at the end of its
/// <see cref="Budget.Replan"/>th turn, unless the loss is proved.</summary>
/// <param name="Value">The value of the line's end: the leaf at the end of the combat, or at the last state searched
/// when the line stops before it.</param>
/// <param name="End">How the line ends: won, lost, or still in progress where the search stopped.</param>
/// <param name="Nodes">The actions simulated, over all the turns' searches.</param>
/// <param name="Turns">How many turns the beams searched, the dive's included.</param>
/// <param name="Proved">Whether the line is the best there is: the full beam left nothing out, with no turn's search out
/// of nodes or keeping fewer states than it found, no state dropped from the beam, and every line at the end of the
/// combat.</param>
/// <param name="Line">The whole line picked, from the decision, past the turns its plan stops at.</param>
/// <param name="From">For a losing line, the value of the turn start it died from, which the pick compares losses by.</param>
public sealed record SlPlan(CombatDecisionResult Result, double Value, CombatStatus End, int Nodes, int Turns, bool Proved,
    TurnEnd? Line = null, double From = 0);

/// <summary>
/// The SL planner as a combat route plays it, one per run: a plan is followed step by step with a
/// <see cref="TurnPlan"/>, and planned again only where it does not hold or has run out. An unproved losing plan runs out
/// after its <see cref="Budget.Replan"/> turns; after <see cref="Budget.LossPlans"/> of them in a row, the last is played
/// to its end. A combat ends with its first plan that is not an unproved loss, or with the run, so the plans in a row are
/// the combat's. Each plan has the budget of the combat's room (<see cref="Budget.For"/>).
/// </summary>
public sealed class SlRoute(Budget budget)
{
    private readonly TurnPlan _plan = new();
    private int _losses;

    /// <param name="plan">Plans the rest of the combat at the decision with the budget it is given.</param>
    public CombatDecisionResult Decide(CombatObservation observation, IReadOnlyList<CombatAction> legal, Func<Budget, SlPlan> plan) =>
        _plan.Decide(observation, legal, () =>
        {
            if (PimcBrain.Forced(observation, legal) is { } forced) return PimcBrain.Only(forced);
            var fight = budget.For(observation.Room);
            var made = plan(fight.LossPlans > 0 && _losses + 1 >= fight.LossPlans ? fight with { Replan = 0 } : fight);
            _losses = made.End == CombatStatus.Lost && !made.Proved ? _losses + 1 : 0;
            return made.Result;
        });
}

/// <summary>
/// The SL combat brain: deterministic planning over the whole combat in the one exact world,
/// the node's clone. Nothing is hidden there and nothing is random, so the best line is the answer, with no average
/// over worlds; it is planned once and then played as planned (Harness's route follows it with a <see cref="TurnPlan"/>).
///
/// A beam over turns. Each state kept is searched to the end of its turn (<see cref="SearchTurn"/>): a depth-first
/// search over the turn's actions, ending the turn an option at every step, one action of each that are the same and
/// transpositions merged by the state as search tells it (<see cref="ICombatWorld.StateKey"/>: the same cards in another
/// order, or another copy of one, are one state) and the world's full hidden state (<see cref="ICombatWorld.HiddenKey"/>).
/// Each distinct state where the turn ends, after the enemies' turn, is scored by the leaf (<see cref="ICombatLeaf"/>,
/// which reads the observation alone); the best <see cref="Budget.Beam"/> of all the states' ends, distinct by key, are the next turn's states. A
/// line that ends the combat is scored by the leaf at its end. The beam stops when no state is left, or after
/// <see cref="MaxTurns"/> turns. The leaf prices a win by V on the run the combat leaves, which a line can still raise
/// past winning now with what the state holds (max HP or gold gained, a card grown), so no state's value caps its lines';
/// one rule written by hand does, for speed: a line stops at a state that cannot end the combat with the HP plus max HP of
/// a win found with the same potions (<see cref="LifeBound"/>). A greedy dive, a beam of one, goes first.
/// Every loss is worth the same, so among losing lines the one that died from the turn
/// start the leaf valued highest is the pick. Among wins worth exactly the same, the one that drinks the fewest potions
/// is, then the one with the fewest turns ended: a combat no line loses HP in has many, and the dive, which goes first,
/// often finds a slow one. A turn costs nothing in the game, so a win worth less is never taken for being sooner, unless
/// the budget has a turn decay (<see cref="Budget.TurnDecay"/>, <see cref="PlayerTurnDecay"/>): then the pick compares each
/// line's value times the decay to the number of turns it ended.
///
/// A potion is not drunk where not drinking it loses no more HP (the user, 2026-11-16). The game lets a potion be thrown
/// away at any time for nothing, so a belt is never worth less than the belt with fewer potions: V prices a win so
/// (<see cref="FreeDisposal"/>, in the combat net), and a turn's end takes the value of any end of that turn that is the
/// same state with only fewer potions (<see cref="TurnDfs"/>). A line that drank a potion for nothing is then worth no
/// more than the line that kept it, which is searched first (the potions come last, <see cref="PimcBrain.Ordered"/>) and
/// wins the tie. A losing plan
/// that is not proved is played for its first <see cref="Budget.Replan"/> turns only, then planned again, from where a beam reaches deeper (or to its end, at 0).
///
/// Only one turn's transpositions are merged, so each state kept is the end of a concrete line of actions, and the
/// plan replays exactly in the world it was searched in. A card prompt is a state like any other, and its answers are
/// its actions (<see cref="PromptAnswers"/>): every prompt of a line is a branch, and a line ends only where the player
/// can play again. A cycle goes to <see cref="LoopSearch"/>. The turns' searches are
/// independent, so a turn is one <see cref="TurnSearcher"/> call that can run them apart: <see cref="Plan"/> runs them
/// here, Harness's pooled planner in workers.
/// </summary>
public sealed class SlPlanner(ICombatLeaf leaf)
{
    /// <summary>How many turns a plan may look ahead; a line still going after them ends there, and is planned again.</summary>
    public const int MaxTurns = 40;

    /// <summary>
    /// The nodes of a turn's search with no limit (<see cref="SearchTurn"/>, <see cref="Budget.NodesPerWorld"/>): every
    /// state of the turn is tried, every answer of every prompt, and every end kept; nothing is cut. On the turns measured
    /// (plans/2026-11-18-1412-max-solver.md), a whole turn took 1,158 to 72,512 nodes, where a budget of 300 reached 19
    /// of 226 ways to play one.
    /// </summary>
    public const int Unlimited = int.MaxValue;

    /// <summary>
    /// The turn decay of a plan made for a player (<see cref="Hello.TurnDecay"/>, the mod's option; the user, 2026-11-16);
    /// self-play has none. A fight of a Silent with Nunchaku was planned for 35 turns where 8 won with the same HP: the
    /// slow win played one attack fewer, and V valued the run with the relic's counter at 7 above the one with it at 8 by
    /// 0.25 %. With this a turn takes 0.1 % of a line's value; 1 HP was worth 1.3 % of V there, so a line still waits
    /// about 13 turns for it. Times, not less: a loss stays 0 below every win, and the cost is the same share of V
    /// wherever V stands.
    /// </summary>
    public const double PlayerTurnDecay = 0.999;

    /// <summary>Plans the rest of the combat in <paramref name="world"/>, the SL world at the decision, searching here.</summary>
    public SlPlan Plan(ICombatWorld world, Budget budget, IReadOnlyList<EnemyTurns>? history = null)
    {
        var root = world.Snapshot();
        var plan = Search(budget, history ?? [], (jobs, nodes, keep) => [.. jobs.Select(job =>
        {
            world.Restore(root);
            foreach (var action in job.Path) world.Execute(action);
            return SearchTurn(world, job.History, nodes, keep, job.LifeBounds);
        })]);
        world.Forget(root);
        return plan;
    }

    /// <summary>
    /// The beam over turns, from the decision's state. A greedy dive comes first, a beam of one, each of its turns
    /// searched in full and kept for the full beam, which takes them from there. When the dive wins, the full beam keeps
    /// <see cref="Budget.EasyBeam"/> states, if set: it only improves a win already found.
    /// </summary>
    public static SlPlan Search(Budget budget, IReadOnlyList<EnemyTurns> history, TurnSearcher search)
    {
        var beam = new Beam(budget.NodesPerWorld, budget.TurnDecay, history, search);
        if (budget.Beam > 1) beam.Run(1);
        if (!beam.Proved) beam.Run(beam.Wins && budget.EasyBeam > 0 ? budget.EasyBeam : budget.Beam);
        return beam.Result(budget.Replan);
    }

    private sealed class Beam(int nodesPerState, double decay, IReadOnlyList<EnemyTurns> history, TurnSearcher search)
    {
        /// <summary>The dive's turns, each searched in full (every end), by the path to the turn's state.</summary>
        private readonly Dictionary<string, TurnSearch> _searched = [];
        private readonly Dictionary<CombatAction, TurnEnd> _byFirst = [];
        private TurnEnd? _best;
        private Rank _bestRank;
        /// <summary>The pruning's bounds, by the potions the wins found end with (<see cref="LifeBound"/>).</summary>
        private readonly Dictionary<string, int> _bounds = [];
        private List<TurnEnd> _left = [];
        private int _nodes, _turns;
        private bool _complete;

        /// <summary>Whether the best line is the best there is: the last beam left nothing out.</summary>
        public bool Proved => _best != null && _complete;

        /// <summary>Whether the best line so far wins the combat.</summary>
        public bool Wins => _best?.Status == CombatStatus.Won;

        /// <summary>A beam of <paramref name="width"/> states from the decision.</summary>
        public void Run(int width)
        {
            var states = new List<TurnEnd> { new([], [], CombatStatus.InProgress, 0, 0, history) };
            _complete = true;
            for (int turn = 0; states.Count > 0 && turn < MaxTurns; turn++)
            {
                var found = Search(states, width);
                _turns++;
                var next = new List<TurnEnd>();
                for (int i = 0; i < states.Count; i++)
                {
                    _nodes += found[i].Nodes;
                    _complete &= !found[i].Cut;
                    foreach (var end in found[i].Ends)
                    {
                        var line = end with { Actions = [.. states[i].Actions, .. end.Actions], Hashes = [.. states[i].Hashes, .. end.Hashes] };
                        if (line.Status == CombatStatus.InProgress)
                        {
                            next.Add(line);
                            continue;
                        }
                        Note(line);
                        LifeBound.Note(_bounds, line);
                        var rank = Rank.Of(line, decay, states[i].Value);
                        if (_best == null || rank.Beats(_bestRank)) (_best, _bestRank) = (line, rank);
                    }
                }
                var seen = new HashSet<ulong>();
                states = [];
                // A stable sort: ties stay in the order the states were searched in, so the beam does not depend on timing.
                foreach (var line in next.OrderByDescending(l => l.Value))
                {
                    if (!seen.Add(line.Key) || LifeBound.Below(_bounds, line.Potions, line.Reach)) continue;
                    if (states.Count == width)
                    {
                        _complete = false;
                        break;
                    }
                    states.Add(line);
                }
            }
            _complete &= states.Count == 0;
            _left = states;
        }

        /// <summary>
        /// The turn's search from each state, as <see cref="SearchTurn"/> finds it with this width. The dive asks for
        /// each turn in full and keeps it, and a later beam that comes to the same
        /// state takes its ends from there (<see cref="Narrow"/>) instead of searching the turn again: the full beam's
        /// first turn always, and the dive's later states when it keeps them.
        /// </summary>
        private List<TurnSearch> Search(List<TurnEnd> states, int width)
        {
            bool dive = width == 1;
            var found = new TurnSearch[states.Count];
            var asked = new List<int>();
            for (int i = 0; i < states.Count; i++)
                if (_searched.TryGetValue(PathKey(states[i].Actions), out var full)) found[i] = Narrow(full, width) with { Nodes = 0 };
                else asked.Add(i);
            if (asked.Count == 0) return [.. found];
            var bounds = new Dictionary<string, int>(_bounds);
            var replies = search([.. asked.Select(i => new PlanJob(states[i].Actions, states[i].History, bounds))], nodesPerState,
                dive ? int.MaxValue : width);
            for (int k = 0; k < asked.Count; k++)
            {
                int i = asked[k];
                if (dive) _searched[PathKey(states[i].Actions)] = replies[k];
                found[i] = dive ? Narrow(replies[k], width) : replies[k];
            }
            return [.. found];
        }

        private static string PathKey(IReadOnlyList<CombatAction> path) => string.Join(' ', path);

        /// <param name="replan">The turns of an unproved losing line played before planning again; 0 plays it to its end.</param>
        public SlPlan Result(int replan)
        {
            foreach (var state in _left) Note(state);
            // The best end of the combat, unless the search stopped first at a state worth more.
            bool left = _left.Count > 0 && (_best == null || Worth(_left[0]) > _bestRank.Worth);
            var pick = (left ? _left[0] : _best) ?? throw new InvalidOperationException("the planner found no line");
            return Picked(pick, left ? 0 : _bestRank.From, _byFirst, Proved, replan, _nodes, _turns);
        }

        /// <summary>What a line is worth to the pick: its value, times the budget's turn decay for each turn it ended
        /// (<see cref="Budget.TurnDecay"/>; at 1, its value).</summary>
        private double Worth(TurnEnd line) => line.Value * Math.Pow(decay, line.Actions.Count(a => a.Kind == CombatActionKind.EndTurn));

        /// <summary>Keeps the best of the lines that start with each first action, for the candidates.</summary>
        private void Note(TurnEnd line)
        {
            if (!_byFirst.TryGetValue(line.Actions[0], out var best) || Worth(line) > Worth(best)) _byFirst[line.Actions[0]] = line;
        }
    }

    /// <summary>
    /// A line's place in the pick: its worth, the value times the turn decay for each turn it ended
    /// (<see cref="Budget.TurnDecay"/>), then, at the same worth, a loss by the value of the turn start it died from (every
    /// loss is 0, however it comes) and a win by the potions it drank, then the turns it ended, fewest first.
    /// </summary>
    internal readonly record struct Rank(CombatStatus Status, double Worth, double From, int Drunk, int Turns)
    {
        /// <param name="from">For a loss, the value of the turn start it died from.</param>
        public static Rank Of(TurnEnd line, double decay, double from)
        {
            int turns = line.Actions.Count(a => a.Kind == CombatActionKind.EndTurn);
            return new(line.Status, line.Value * Math.Pow(decay, turns), from, line.Actions.Count(a => a.Kind == CombatActionKind.UsePotion), turns);
        }

        /// <summary>Whether a line so ranked is picked over <paramref name="best"/>, the best so far.</summary>
        public bool Beats(Rank best) => Worth > best.Worth || (Worth == best.Worth && (Status == CombatStatus.Lost
            ? From > best.From : Status == CombatStatus.Won && (Drunk, Turns).CompareTo((best.Drunk, best.Turns)) < 0));
    }

    /// <summary>
    /// The plan of the line picked, with the best line after each first action as the candidates. A loss the search may
    /// have been wrong about (not <paramref name="proved"/>) is played <paramref name="replan"/> turns at a time; a proved
    /// one has nothing better.
    /// </summary>
    internal static SlPlan Picked(TurnEnd pick, double from, IReadOnlyDictionary<CombatAction, TurnEnd> byFirst, bool proved, int replan,
        int nodes, int turns)
    {
        var candidates = byFirst.OrderByDescending(kv => kv.Key == pick.Actions[0]).ThenByDescending(kv => kv.Value.Value)
            .Select(kv => new Candidate(kv.Key.ToString(), kv.Value.Value, 0, 1, [Math.Round(kv.Value.Value, 4)],
                Math.Round(kv.Value.Win, 4), Math.Round(kv.Value.Hp, 1))).ToList();
        int played = pick.Actions.Count;
        if (pick.Status == CombatStatus.Lost && !proved && replan > 0)
            for (int i = 0, ends = 0; i < pick.Actions.Count; i++)
                if (pick.Actions[i].Kind == CombatActionKind.EndTurn && ++ends == replan)
                {
                    played = i + 1;
                    break;
                }
        var result = new CombatDecisionResult(pick.Actions[0], [.. pick.Actions.Take(played).Skip(1)], candidates,
            [.. pick.Hashes.Take(played).Skip(1)], Nodes: nodes, Turns: turns);
        return new(result, pick.Value, pick.Status, nodes, turns, proved, pick, pick.Status == CombatStatus.Lost ? from : 0);
    }

    internal static ulong Mix(ulong a, ulong b)
    {
        ulong z = a ^ (b + 0x9E3779B97F4A7C15 + (a << 6) + (a >> 2));
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// What <see cref="SearchTurn"/> finds with <paramref name="keep"/>, from what it found with every end kept: the kept
    /// states are the best, in the same order.
    /// </summary>
    public static TurnSearch Narrow(TurnSearch full, int keep)
    {
        var over = full.Ends.Where(e => e.Status != CombatStatus.InProgress).ToList();
        var going = full.Ends.Where(e => e.Status == CombatStatus.InProgress).ToList();
        return full with { Ends = [.. over, .. going.Take(keep)], Cut = full.Cut || going.Count > keep };
    }

    /// <summary>
    /// Searches the rest of the turn from the world's state, with <paramref name="nodes"/> nodes: the best end of the
    /// combat the turn can reach, if any, and the best <paramref name="keep"/> distinct states at the next turn's start.
    /// </summary>
    /// <param name="history">The record of enemy moves up to the world's state; each end's goes on from it.</param>
    /// <param name="lifeBounds">A line stops at a state below its potions' bound (<see cref="PlanJob.LifeBounds"/>).</param>
    public TurnSearch SearchTurn(ICombatWorld world, IReadOnlyList<EnemyTurns> history, int nodes, int keep,
        IReadOnlyDictionary<string, int>? lifeBounds = null) =>
        new TurnDfs(world, leaf, history, nodes, lifeBounds).Run(keep);

    private sealed class TurnDfs(ICombatWorld world, ICombatLeaf leaf, IReadOnlyList<EnemyTurns> history, int nodes,
        IReadOnlyDictionary<string, int>? lifeBounds)
    {
        private readonly HashSet<(ulong Obs, ulong Hidden)> _seen = [];
        private readonly HashSet<ulong> _keys = [];
        private readonly List<TurnEnd> _ends = [];
        /// <summary>The lines the search has ended, in the order found, until the leaf is asked about them (<see cref="Price"/>).</summary>
        private readonly List<Ended> _ended = [];
        /// <summary>The lines the leaf has valued, in the order found, with their outcomes, until the turn's ends are made
        /// of them (<see cref="Value"/>).</summary>
        private readonly List<Priced> _priced = [];
        private readonly List<LeafOutcome> _outcomes = [];
        /// <summary>How many of <see cref="_ended"/> are at the next turn's start, the ones the net is asked about.</summary>
        private int _going;
        private readonly List<CombatAction> _actions = [];
        private readonly List<ulong> _hashes = [];
        private readonly List<LoopPoint> _path = [];
        /// <summary>Snapshots of states the search has left that a cycle found there starts from: kept until the cycles are run.</summary>
        private readonly List<WorldSnapshot> _kept = [];
        private int _nodes, _round, _limit, _pruned;
        private bool _cut;
        private LoopSearch _loops = null!;

        public TurnSearch Run(int keep)
        {
            var obs = world.Observe();
            _round = obs.Round;
            _loops = new(world, _round, End);
            Visit(obs, nodes);
            Value();
            foreach (var end in _loops.RunFound()) Add(end);
            foreach (var snapshot in _kept) world.Forget(snapshot);
            _cut |= _loops.Found;
            var over = _ends.Where(e => e.Status != CombatStatus.InProgress).MaxBy(e => e.Value);
            var going = _ends.Where(e => e.Status == CombatStatus.InProgress).OrderByDescending(e => e.Value).ToList();
            if (going.Count > keep) _cut = true;
            return new([.. over == null ? [] : new[] { over }, .. going.Take(keep)], _nodes, _cut, _pruned);
        }

        /// <summary>
        /// Tries every action from the current state, once per state: the plays where the player is on this turn
        /// (<see cref="PimcBrain.Ordered"/>), the answers where a card prompt waits. At the turn's start the nodes are
        /// shared over the actions, each with an even share plus what the ones before it left, so every first action is
        /// looked at however many states the turn has; a prompt that one of them raises shares that action's nodes over
        /// its answers the same way. Below, a subtree spends its share depth first. A line ends with the combat, or where
        /// the player can play again after the turn's end: a prompt the end of the turn or the next turn's start raises
        /// is answered first, so the leaf reads a state the combat net knows. A move back to a state on the path with
        /// something gained is a cycle, which would spend every node on its own repeats: it goes to
        /// <see cref="LoopSearch"/> instead of down.
        /// </summary>
        /// <param name="shareTo">Where the state's share of the nodes ends, when its actions share it evenly; null where
        /// they are tried depth first, up to the limit of the share they are in.</param>
        private void Visit(CombatObservation obs, int? shareTo = null)
        {
            if (!_seen.Add((world.StateKey(obs), world.HiddenKey()))) return;
            ulong hash = world.Hash(obs);
            var here = world.Snapshot();
            // A prompt stands in the middle of an action: a cycle is a move between states where the player can play.
            bool plays = obs.Prompt == null;
            if (plays) _path.Add(new(obs, _actions.Count, here));
            bool moved = false;
            var todo = PimcBrain.Actions(world, obs, (shareTo ?? _limit) - _nodes, out bool left);
            _cut |= left;
            int from = _nodes;
            for (int i = 0; i < todo.Count; i++)
            {
                var action = todo[i];
                int? to = null;
                if (shareTo is { } end) _limit = (to = Math.Max(_nodes + 1, from + (int)((long)(end - from) * (i + 1) / todo.Count))).Value;
                else if (moved && _nodes >= _limit)
                {
                    _cut = true;
                    break;
                }
                if (moved) world.Restore(here);
                moved = true;
                _nodes++;
                world.Execute(action);
                _actions.Add(action);
                _hashes.Add(hash);
                var status = world.Status;
                var after = world.Observe();
                if (lifeBounds is { Count: > 0 } && lifeBounds.TryGetValue(LifeBound.Potions(after.Potions), out int bound) && LifeBound.Reach(after) < bound)
                    _pruned++;
                else if (status != CombatStatus.InProgress) Ends(status, after);
                else if (after.Prompt != null) Visit(after, to);
                else if (action.Kind == CombatActionKind.EndTurn || after.Round != _round) Ends(status, after);
                else if (!_loops.Intercept(_path, after, _actions, _hashes)) Visit(after);
                _actions.RemoveAt(_actions.Count - 1);
                _hashes.RemoveAt(_hashes.Count - 1);
            }
            if (plays) _path.RemoveAt(_path.Count - 1);
            // The search is done with this state: the world lets go of it, unless a cycle found below starts here.
            if (_loops.StartsAt(here)) _kept.Add(here);
            else world.Forget(here);
        }

        /// <summary>A line that ended the turn or the combat, as the world stood there, before the leaf has valued it.</summary>
        /// <param name="Bare">At the next turn's start, the key of the state with every potion thrown away: the ends with
        /// the same one differ only by their potions. 0 once the combat is over.</param>
        private sealed record Ended(LeafState State, IReadOnlyList<CombatAction> Actions, IReadOnlyList<ulong> Hashes, ulong Key, ulong Shown,
            ulong Bare = 0)
        {
            public TurnEnd Valued(LeafOutcome outcome) => Light().Valued(outcome);

            /// <summary>What is kept of the line once the leaf has valued it: no observation or run.</summary>
            public Priced Light() => new(Actions, Hashes, State.Status, Key, Bare, State.Obs.Potions, State.History,
                State.Obs.Player.Hp + State.Obs.Player.MaxHp, LifeBound.Reach(State.Obs), Shown);
        }

        /// <summary>A line the leaf has valued (<see cref="Ended"/>), with the potions <see cref="Thrown"/> compares.</summary>
        private sealed record Priced(IReadOnlyList<CombatAction> Actions, IReadOnlyList<ulong> Hashes, CombatStatus Status, ulong Key, ulong Bare,
            IReadOnlyList<string?> Potions, IReadOnlyList<EnemyTurns> History, int Life, int Reach, ulong Shown)
        {
            public TurnEnd Valued(LeafOutcome outcome) => new(Actions, Hashes, Status, outcome.Value, Key, History, outcome.Win, outcome.Hp, Life, Reach,
                LifeBound.Potions(Potions), Shown);
        }

        /// <summary>
        /// The ends at the next turn's start the leaf is asked about at a time: a turn has had 169 thousand (a Regent's
        /// second turn of 9 cards and 4 energy, with Toasty Mittens' exhaust after it), each held with its observation and
        /// its run until asked, which took gigabytes. A multiple of the net's batch (<see cref="CombatNet.Batch"/>), so it
        /// is asked in the same batches as about them all together.
        /// </summary>
        private const int Chunk = 256 * CombatNet.Batch;

        /// <summary>The key of a state at the next turn's start, by which a turn's ends and the beam's states are told
        /// apart; 0 once the combat is over.</summary>
        private ulong KeyOf(CombatStatus status, CombatObservation obs) =>
            status == CombatStatus.InProgress ? Mix(world.StateKey(obs), world.HiddenKey()) : 0;

        /// <summary>The turn ended here, or the combat did: what the leaf is asked about, with the line that led here.</summary>
        private Ended Reached(CombatStatus status, CombatObservation obs, IReadOnlyList<CombatAction> actions, IReadOnlyList<ulong> hashes, ulong key)
        {
            var record = new MoveRecord(history);
            if (status == CombatStatus.InProgress) record.Witness(obs);
            return new(new(status, obs, world.Run(), record.History), [.. actions], [.. hashes], key, world.Hash(obs));
        }

        /// <summary>The search's line ended here. The search needs no end's value to go on, so the leaf is asked about
        /// a turn's ends together, <see cref="Chunk"/> at a time (<see cref="Price"/>); a state another line already ended
        /// at is not asked about again.</summary>
        private void Ends(CombatStatus status, CombatObservation obs)
        {
            if (status != CombatStatus.InProgress)
            {
                _ended.Add(Reached(status, obs, _actions, _hashes, 0));
                return;
            }
            ulong hidden = world.HiddenKey(), key = Mix(world.StateKey(obs), hidden);
            if (!_keys.Add(key)) return;
            ulong bare = obs.Potions.All(p => p == null) ? key
                : Mix(world.StateKey(obs with { Potions = [.. obs.Potions.Select(_ => (string?)null)] }), hidden);
            _ended.Add(Reached(status, obs, _actions, _hashes, key) with { Bare = bare });
            if (++_going == Chunk) Price();
        }

        /// <summary>Asks the leaf about the ends found since it was last asked, in the order found.</summary>
        private void Price()
        {
            if (_ended.Count == 0) return;
            _outcomes.AddRange(leaf.Outcomes([.. _ended.Select(e => e.State)]));
            _priced.AddRange(_ended.Select(e => e.Light()));
            _ended.Clear();
            _going = 0;
        }

        /// <summary>The turn's ends, from all the lines the search ended, valued in the order found.</summary>
        private void Value()
        {
            Price();
            var outcomes = _outcomes.ToArray();
            Thrown(outcomes);
            for (int i = 0; i < _priced.Count; i++) _ends.Add(_priced[i].Valued(outcomes[i]));
            _priced.Clear();
            _outcomes.Clear();
        }

        /// <summary>
        /// A potion can be thrown away in a fight too, for nothing: a state is worth at least the same state with fewer
        /// potions. So each end of the turn at the next turn's start takes the outcome of the best end that differs from
        /// it only by holding fewer of its potions, if the leaf values that one higher: where a potion drunk changed
        /// nothing else, the end that kept it is worth as much.
        /// </summary>
        private void Thrown(LeafOutcome[] outcomes)
        {
            foreach (var same in Enumerable.Range(0, _priced.Count).Where(i => _priced[i].Bare != 0).GroupBy(i => _priced[i].Bare))
            {
                // By how many potions each holds, so that an end's own is final before an end holding more is compared with it.
                var ends = same.OrderBy(i => _priced[i].Potions.Count(p => p != null)).ToList();
                for (int m = 1; m < ends.Count; m++)
                    for (int f = 0; f < m; f++)
                        if (outcomes[ends[f]].Value > outcomes[ends[m]].Value && Holds(_priced[ends[m]].Potions, _priced[ends[f]].Potions))
                            outcomes[ends[m]] = outcomes[ends[f]];
            }
        }

        /// <summary>Whether <paramref name="belt"/> holds every potion <paramref name="fewer"/> does, and more.</summary>
        private static bool Holds(IReadOnlyList<string?> belt, IReadOnlyList<string?> fewer)
        {
            var left = belt.OfType<string>().ToList();
            foreach (var potion in fewer.OfType<string>())
                if (!left.Remove(potion)) return false;
            return left.Count > 0;
        }

        /// <summary>A line's end valued at once: the cycles' cash-outs compare the values as they go.</summary>
        private TurnEnd End(CombatStatus status, CombatObservation obs, IReadOnlyList<CombatAction> actions, IReadOnlyList<ulong> hashes)
        {
            var ended = Reached(status, obs, actions, hashes, KeyOf(status, obs));
            return ended.Valued(leaf.Outcome(status, obs, ended.State.Run, ended.State.History));
        }

        private void Add(TurnEnd end)
        {
            if (end.Status == CombatStatus.InProgress && !_keys.Add(end.Key)) return;
            _ends.Add(end);
        }
    }
}
