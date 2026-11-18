using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>
/// The SL planner as a solver (plans/2026-11-18-1412-max-solver.md): the rest of the combat searched with no state
/// dropped, a little at a time (<see cref="Run"/>), so that its best line so far can be played whenever it is asked for,
/// and only gets better the longer it searches.
///
/// Its states are the turns' starts. Each is searched to the end of its turn with no limit
/// (<see cref="SlPlanner.Unlimited"/>): every state of the turn, every answer of every prompt, every end kept. The ends
/// are new states, and none is dropped: the combat net only orders them, the state it values highest (by worth, as the
/// pick compares lines) searched next, the rest waiting their turn. One table over the combat holds every state reached,
/// by its key: the observation and the full hidden state (<see cref="TurnEnd.Key"/>), and the record of enemy moves, which
/// the leaf reads. A state is searched once however many lines reach it. The states that wait are most of them, so a
/// record of enemy moves is held once, however many states share it: the ends of a turn mostly do.
///
/// The pick is the beam's (<see cref="SlPlanner"/>): the best end of the combat by worth, wins worth the same by fewer
/// potions drunk and then fewer turns, and losses, all 0, by the value of the turn start they died from. Until a line
/// ends the combat, the line to the state valued highest. A line another search found (the beam's, <see cref="Seed"/>) is
/// a candidate too, so the solver's pick is never worse than it. When no state is left and no turn's search was cut (a
/// cycle, <see cref="LoopSearch"/>, is), the pick is proved: there is no better line under these rules.
///
/// The combat goes on under it (<see cref="Advance"/>): the root moves to where it went, the states that can still be
/// reached stay with all that was found of them, and the rest go.
///
/// The one rule written by hand prunes it too (<see cref="LifeBound"/>): a state that cannot end the combat with the HP plus
/// max HP of a win found with the same potions is dropped, in a turn's search as soon as a line reaches it
/// (<see cref="PlanJob.LifeBounds"/>), and among the states waiting when a win raises its potions' bound.
/// </summary>
/// <param name="decay">The turn decay of the pick (<see cref="Budget.TurnDecay"/>).</param>
/// <param name="batch">The states searched at a time, a turn's search each, which a <see cref="TurnSearcher"/> can run
/// apart. The search is the same for the same nodes and batch, wherever it runs.</param>
public sealed class SlSolver(IReadOnlyList<EnemyTurns> history, double decay = 1, int batch = 16)
{
    /// <summary>A state the solver reached: a turn's start, or the root, where the combat stands.</summary>
    private sealed class Node(ulong key, double value, int turns, IReadOnlyList<EnemyTurns> history, ulong moves, long order, int reach, string potions,
        ulong shown = 0)
    {
        public readonly ulong Key = key;
        /// <summary>The observation's hash where its line reaches it (<see cref="TurnEnd.Shown"/>): what it found holds on a
        /// line that shows the same, the same copies of each card under the same uids.</summary>
        public readonly ulong Shown = shown;
        /// <summary>The most HP plus max HP the combat can end with from the state (<see cref="LifeBound.Reach"/>).</summary>
        public readonly int Reach = reach;
        /// <summary>The potions held, by which the pruning compares the state (<see cref="LifeBound.Potions"/>).</summary>
        public readonly string Potions = potions;
        /// <summary>The leaf's value of the state; 0 at the root, as a turn start the beam has not valued.</summary>
        public readonly double Value = value;
        /// <summary>The turns ended to the state since the solver began, for its worth.</summary>
        public readonly int Turns = turns;
        public readonly IReadOnlyList<EnemyTurns> History = history;
        /// <summary>The hash of <see cref="History"/>, by which states share it.</summary>
        public readonly ulong Moves = moves;
        /// <summary>When the state was reached: states worth the same are searched in that order.</summary>
        public readonly long Order = order;
        /// <summary>The state the line to this one comes from, null at the root, and the actions from there.</summary>
        public Node? Parent;
        public Edge? From;
        /// <summary>Where the state's turn ends at the next turn's start, once searched; null until then.</summary>
        public List<Edge>? Edges;
        /// <summary>The best end of the combat the state's turn reaches, by the actions from the state, if any.</summary>
        public TurnEnd? Over;
        public bool Cut;
        /// <summary>Whether the pruning dropped any of its turn's lines or ends.</summary>
        public bool Dropped;
    }

    /// <summary>The actions from a state to a state where its turn ends, with the hash of each one's observation and of
    /// the observation where it ends.</summary>
    private sealed record Edge(IReadOnlyList<CombatAction> Actions, IReadOnlyList<ulong> Hashes, ulong Key, ulong Shown);

    private Dictionary<ulong, Node> _table = [];
    /// <summary>The records of enemy moves the states hold, each once, by hash.</summary>
    private Dictionary<ulong, IReadOnlyList<EnemyTurns>> _moves = [];
    private readonly PriorityQueue<Node, (double Worth, long Order)> _queue = new();
    private Node _root = new(0, 0, 0, history, 0, 0, int.MaxValue, "");
    private long _order, _nodes;
    private int _searched, _cuts;
    private (TurnEnd Line, SlPlanner.Rank Rank)? _best;
    private (TurnEnd Line, double From)? _seed;
    private Dictionary<CombatAction, TurnEnd> _byFirst = [];
    private bool _started;
    private Dictionary<string, int> _bounds = [];
    /// <summary>How many times a win raised a bound, and how many the waiting states were last checked after.</summary>
    private int _raised, _purged;

    /// <summary>The actions simulated so far, over every turn's search.</summary>
    public long Nodes => _nodes;

    /// <summary>The turns searched so far.</summary>
    public int Searched => _searched;

    /// <summary>The states reached from the root, searched or not.</summary>
    public int States => _table.Count;

    /// <summary>The states reached and not searched yet.</summary>
    public int Left => _started ? _queue.Count : 1;

    /// <summary>By the potions held at its end, the most HP plus max HP a win found from the root ends the combat with: a
    /// state holding those potions that cannot end it with as much is dropped (<see cref="LifeBound"/>).</summary>
    public IReadOnlyDictionary<string, int> Bounds => _bounds;

    /// <summary>Whether the pick is the best line there is: nothing is left to search, and no turn's search was cut.</summary>
    public bool Proved => _started && _queue.Count == 0 && _cuts == 0 && _best != null;

    /// <summary>A line another search found from the root, such as the beam's (<see cref="SlPlan.Line"/>): a candidate
    /// for the pick, if it ends the combat.</summary>
    public void Seed(SlPlan plan)
    {
        if (plan.Line is not { Status: not CombatStatus.InProgress } line) return;
        _seed = (line, plan.From);
        Consider(line, plan.From);
    }

    /// <summary>
    /// Searches on for at least <paramref name="nodes"/> more actions, or until nothing is left: the states most worth
    /// first, <c>batch</c> at a time, each turn's search by <paramref name="search"/> from where the combat stands. The
    /// root's turn is searched first, alone.
    /// </summary>
    public void Run(TurnSearcher search, long nodes)
    {
        if (!_started) Start(search);
        long until = nodes >= long.MaxValue - _nodes ? long.MaxValue : _nodes + nodes;
        while (_queue.Count > 0 && _nodes < until)
        {
            var states = new List<Node>();
            while (states.Count < batch && _queue.TryDequeue(out var node, out _))
                if (!LifeBound.Below(_bounds, node.Potions, node.Reach)) states.Add(node);
                else _table.Remove(node.Key);
            if (states.Count == 0) continue;
            var bounds = new Dictionary<string, int>(_bounds);
            var found = search([.. states.Select(n => new PlanJob(Path(n).Actions, n.History, bounds))], SlPlanner.Unlimited, int.MaxValue);
            for (int i = 0; i < states.Count; i++) Expand(states[i], found[i]);
            if (_raised > _purged) Purge();
        }
    }

    /// <summary>The waiting states below their potions' bound go (<see cref="Bounds"/>).</summary>
    private void Purge()
    {
        _purged = _raised;
        var left = _queue.UnorderedItems.ToList();
        _queue.Clear();
        foreach (var (node, priority) in left)
            if (!LifeBound.Below(_bounds, node.Potions, node.Reach)) _queue.Enqueue(node, priority);
            else _table.Remove(node.Key);
    }

    /// <summary>
    /// The combat went on from the root by <paramref name="played"/>, to where its record of enemy moves is
    /// <paramref name="moves"/>: the root moves there, and its turn is searched again by <paramref name="search"/>, from
    /// there. What it reaches stays with all that was found of it, each state with a line from the new root that holds
    /// the same copies of its cards (a state reached only otherwise is searched again); the rest goes. The seeded line stays if the combat went its way. The bound is the wins' the new root still reaches; where
    /// that is lower than before, a state whose turn was searched with a higher one is searched again, as one whose ends
    /// went since.
    /// </summary>
    public void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, TurnSearcher search)
    {
        if (played.Count == 0) return;
        _root = new(0, 0, _root.Turns + EndTurns(played), moves, 0, ++_order, int.MaxValue, "");
        if (_seed is { } seed)
            _seed = seed.Line.Actions.Count > played.Count && seed.Line.Actions.Take(played.Count).SequenceEqual(played)
                ? (seed.Line with { Actions = [.. seed.Line.Actions.Skip(played.Count)], Hashes = [.. seed.Line.Hashes.Skip(played.Count)] }, seed.From)
                : null;
        Start(search);
    }

    /// <summary>The pick so far, as a plan (<see cref="SlPlanner.Picked"/>): a losing one not proved stops at the end of
    /// its <paramref name="replan"/>th turn.</summary>
    public SlPlan Result(int replan)
    {
        if (!_started) throw new InvalidOperationException("the solver has not searched the root's turn");
        TurnEnd pick;
        double from = 0;
        if (_best is { } best) (pick, from) = (best.Line, best.Rank.From);
        else if (_queue.TryPeek(out var top, out _))
        {
            var (actions, hashes) = Path(top);
            pick = new(actions, hashes, CombatStatus.InProgress, top.Value, top.Key, top.History);
        }
        else throw new InvalidOperationException("the solver found no line");
        return SlPlanner.Picked(pick, from, _byFirst, Proved, replan, (int)Math.Min(_nodes, int.MaxValue), _searched);
    }

    /// <summary>Searches the root's turn, then keeps the states it reaches, by the table, and lets the rest go.</summary>
    private void Start(TurnSearcher search)
    {
        _started = true;
        // The pick and its bound come from lines the root still reaches: the seed's, then what its turn and the states
        // kept find.
        var before = _bounds;
        (_best, _byFirst, _bounds) = (null, [], []);
        if (_seed is { } carried) Consider(carried.Line, carried.From);
        Expand(_root, search([new PlanJob([], _root.History, new Dictionary<string, int>(_bounds))], SlPlanner.Unlimited, int.MaxValue)[0]);
        // A state a turn reached may have been dropped since, by the pick's HP (Purge). A state is one key, the same cards
        // whichever copy is where, but what was found of it plays the cards its own line left it with, by their uids: it
        // stays on its line, or takes another that shows the same (Edge.Shown), in the order reached from the root; the
        // turns only go forward, so every line goes back to the root. Reached only by lines that hold other copies, it is
        // a state found anew.
        var kept = new Dictionary<ulong, Node>();
        var order = new List<Node> { _root };
        var reached = new HashSet<Node> { _root };
        for (int i = 0; i < order.Count; i++)
            foreach (var edge in order[i].Edges ?? [])
            {
                if (kept.ContainsKey(edge.Key) || !_table.TryGetValue(edge.Key, out var next)) continue;
                bool own = next.Parent != null && reached.Contains(next.Parent) && next.Parent.Edges!.Contains(next.From!);
                if (!own && next.Shown != edge.Shown)
                    next = new(next.Key, next.Value, next.Turns, next.History, next.Moves, ++_order, next.Reach, next.Potions, edge.Shown);
                if (!own) (next.Parent, next.From) = (order[i], edge);
                kept[edge.Key] = next;
                order.Add(next);
                reached.Add(next);
            }
        _table = kept;
        _moves = order.Skip(1).GroupBy(n => n.Moves).ToDictionary(g => g.Key, g => g.First().History);
        _queue.Clear();
        (_cuts, _best, _byFirst, _bounds) = (0, null, [], []);
        if (_seed is { } seed) Consider(seed.Line, seed.From);
        foreach (var node in order)
            if (node.Over is { } over) Consider(Line(node, over), node.Value);
        // Where a bound fell or went, with wins the root no longer reaches, a turn the pruning took lines or ends from is
        // searched again: the bounds keep more of it now.
        if (before.Any(b => !_bounds.TryGetValue(b.Key, out int now) || now < b.Value))
            foreach (var node in order.Skip(1))
                if (node.Edges is { } edges && (node.Dropped || edges.Any(e => !kept.ContainsKey(e.Key))))
                    (node.Edges, node.Over, node.Cut, node.Dropped) = (null, null, false, false);
        foreach (var node in order)
        {
            if (node.Cut) _cuts++;
            if (node.Edges == null) _queue.Enqueue(node, (-Worth(node), node.Order));
        }
        Purge();
    }

    /// <summary>A state's turn, as searched: its end of the combat a candidate for the pick, its ends at the next turn's
    /// start new states unless reached before.</summary>
    private void Expand(Node node, TurnSearch found)
    {
        _nodes += found.Nodes;
        _searched++;
        node.Edges = [];
        node.Cut = found.Cut;
        node.Dropped = found.Pruned > 0;
        if (found.Cut) _cuts++;
        foreach (var end in found.Ends)
        {
            if (end.Status != CombatStatus.InProgress)
            {
                node.Over = end;
                Consider(Line(node, end), node.Value);
                continue;
            }
            if (LifeBound.Below(_bounds, end.Potions, end.Reach))
            {
                node.Dropped = true;
                continue;
            }
            ulong moves = ProtocolJson.Hash(end.History);
            var edge = new Edge(end.Actions, end.Hashes, SlPlanner.Mix(end.Key, moves), end.Shown);
            node.Edges.Add(edge);
            if (_table.ContainsKey(edge.Key)) continue;
            if (!_moves.TryGetValue(moves, out var shared)) _moves[moves] = shared = end.History;
            var next = new Node(edge.Key, end.Value, node.Turns + EndTurns(end.Actions), shared, moves, ++_order, end.Reach, end.Potions, end.Shown)
                { Parent = node, From = edge };
            _table[edge.Key] = next;
            _queue.Enqueue(next, (-Worth(next), next.Order));
        }
    }

    private void Consider(TurnEnd line, double from)
    {
        var rank = SlPlanner.Rank.Of(line, decay, from);
        if (_best is not { } best || rank.Beats(best.Rank)) _best = (line, rank);
        if (LifeBound.Note(_bounds, line)) _raised++;
        if (!_byFirst.TryGetValue(line.Actions[0], out var first) || rank.Worth > SlPlanner.Rank.Of(first, decay, 0).Worth)
            _byFirst[line.Actions[0]] = line;
    }

    private double Worth(Node node) => node.Value * Math.Pow(decay, node.Turns);

    /// <summary>The line from the root through <paramref name="node"/> to an end of its turn.</summary>
    private TurnEnd Line(Node node, TurnEnd end)
    {
        var (actions, hashes) = Path(node);
        return end with { Actions = [.. actions, .. end.Actions], Hashes = [.. hashes, .. end.Hashes] };
    }

    /// <summary>The actions from the root to a state, and the hash of each one's observation.</summary>
    private static (List<CombatAction> Actions, List<ulong> Hashes) Path(Node node)
    {
        var edges = new List<Edge>();
        for (var n = node; n.Parent != null; n = n.Parent) edges.Add(n.From!);
        edges.Reverse();
        return ([.. edges.SelectMany(e => e.Actions)], [.. edges.SelectMany(e => e.Hashes)]);
    }

    private static int EndTurns(IEnumerable<CombatAction> actions) => actions.Count(a => a.Kind == CombatActionKind.EndTurn);
}
