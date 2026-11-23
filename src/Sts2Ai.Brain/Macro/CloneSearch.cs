using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// L0's search of a decision on the node's clone in SL. Each option is played in the real game,
/// and what the game asks next is its afterstate. While that is still a screen of the node (not the map, not a combat,
/// not the run's end), the search goes on: a beam over the screen's actions, the belt's as the decision itself offers
/// them (<see cref="MacroBrain.Offered"/>), level by level, keeping the <see cref="Beam"/> states V values best, up to
/// <see cref="MaxProbes"/> probes in all. A state reached twice is searched once. A state is worth the best state after
/// it, or its own value where the search stops (<paramref name="leaf"/>); an option, its afterstate. An option's line
/// is the answers after it along which its worth was found, as the decisions there would take them.
/// A card prompt that needs two picks or more is not searched into: its own decision searches it.
/// The decision itself and the states <paramref name="decided"/> before it are never searched: an answer that comes back
/// to one goes round in a cycle (a card reward skipped returns to the rewards screen that opened it, where the same
/// answer comes again). Such an option is worth −∞, so it is never taken; deeper, such an answer adds nothing.
/// </summary>
/// <param name="leaf">What a state is worth where the search stops.</param>
/// <param name="decided">The keys (<see cref="Key"/>) of the states decided before this decision.</param>
/// <param name="cards">A run's card lock (<see cref="CardLock"/>): each state weighed as it weighs the state's deck against
/// the decision's; one that breaks it is worth −∞ and not searched on. Null for none.</param>
public sealed class CloneSearch(MacroLeaf leaf, IMacroClone clone, IReadOnlySet<ulong>? decided = null, CardLock? cards = null)
{
    public const int Beam = 16;
    public const int MaxProbes = 1024;

    /// <summary>The probes the last search played.</summary>
    public int Probes { get; private set; }

    /// <param name="root">The decision itself, as a probe of no answers would show it.</param>
    /// <param name="options">Each option, as the answers it plays from the decision.</param>
    /// <returns>Each option's afterstate, worth and line, in order.</returns>
    public IReadOnlyList<(Afterstate After, double Value, IReadOnlyList<MacroAction> Line)> Search(Afterstate root, IReadOnlyList<IReadOnlyList<MacroAction>> options)
    {
        var seen = new Dictionary<ulong, Node?> { [Key(root)] = null };
        foreach (var key in decided ?? new HashSet<ulong>()) seen.TryAdd(key, null);
        var firsts = new List<Node>();
        var frontier = new List<Node>();
        var afters = clone.After(options);
        Probes = options.Count;
        bool Broken(Afterstate after) => cards?.Breaks(root.Run.Deck, after.Run.Deck) == true;
        double Value(Afterstate after) => cards?.Worth(root.Run.Deck, after.Run.Deck, leaf.Value(after)) ?? leaf.Value(after);
        for (int i = 0; i < options.Count; i++)
        {
            ulong key = Key(afters[i]);
            if (seen.TryGetValue(key, out var same))
            {
                firsts.Add(same ?? new Node(options[i], afters[i], double.NegativeInfinity, 1));
                continue;
            }
            var node = new Node(options[i], afters[i], Value(afters[i]), 1);
            firsts.Add(node);
            seen[key] = node;
            if (!Broken(afters[i])) frontier.Add(node);
        }
        for (int level = 2; frontier.Count > 0 && Probes < MaxProbes; level++)
        {
            var parents = frontier.Where(n => Inside(n.After)).OrderByDescending(n => n.Value).Take(Beam).ToList();
            var jobs = parents.SelectMany(p => Answers(p.After).Select(a => (Parent: p, Path: (IReadOnlyList<MacroAction>)[.. p.Path, a]))).ToList();
            if (jobs.Count > MaxProbes - Probes)
            {
                foreach (var job in jobs.Skip(MaxProbes - Probes)) job.Parent.Cut = true;
                jobs = [.. jobs.Take(MaxProbes - Probes)];
            }
            var next = clone.After([.. jobs.Select(j => j.Path)]);
            Probes += jobs.Count;
            frontier = [];
            for (int i = 0; i < jobs.Count; i++)
            {
                ulong key = Key(next[i]);
                if (seen.TryGetValue(key, out var same))
                {
                    // Linked only within a level, so that the states form no cycle.
                    if (same?.Level == level) jobs[i].Parent.Children.Add((jobs[i].Path[^1], same));
                    continue;
                }
                var node = new Node(jobs[i].Path, next[i], Value(next[i]), level);
                seen[key] = node;
                jobs[i].Parent.Children.Add((jobs[i].Path[^1], node));
                if (!Broken(next[i])) frontier.Add(node);
            }
        }
        return firsts.Select(n => (n.After, n.Worth(), n.Line())).ToList();
    }

    /// <summary>Whether the search goes on from <paramref name="after"/>: a screen of the node.</summary>
    private static bool Inside(Afterstate after) => after.Surface is not (null or Surfaces.Map or Surfaces.Combat);

    /// <summary>The answers searched at a screen of the node: its actions as a decision there offers them, or a card prompt's
    /// single picks, the cards that go first first (a removal's basic cards, <see cref="RemovalOrder"/>, or an upgrade's
    /// under the card lock, <see cref="CardLock.Firsts"/>).</summary>
    private IEnumerable<MacroAction> Answers(Afterstate after)
    {
        if (after.Screen is not CardChoiceScreen { Choice: var choice })
        {
            foreach (var action in MacroBrain.Offered(after.Run, after.Screen, after.Legal)) yield return action;
            yield break;
        }
        if (choice.Min > 1) yield break;
        if (choice.Min == 0) yield return MacroAction.Pick([]);
        if (choice.Max < 1) yield break;
        var firsts = CardLock.Firsts(cards, choice);
        foreach (var group in choice.Groups())
            if (RemovalOrder.Opens(firsts, [], group[0])) yield return MacroAction.Pick([group[0]]);
    }

    public static ulong Key(Afterstate after) => ProtocolJson.Hash(after);

    private sealed class Node(IReadOnlyList<MacroAction> path, Afterstate after, double value, int level)
    {
        private double? _worth;

        public IReadOnlyList<MacroAction> Path { get; } = path;
        public Afterstate After { get; } = after;
        public double Value { get; } = value;
        public int Level { get; } = level;
        /// <summary>The states after its answers, each with the answer that leads to it.</summary>
        public List<(MacroAction Answer, Node Node)> Children { get; } = [];

        /// <summary>Some of its answers were never probed, the probes having run out.</summary>
        public bool Cut { get; set; }

        public double Worth() => _worth ??= Children.Count == 0 ? Value
            : Math.Max(Children.Max(c => c.Node.Worth()), Cut ? Value : double.NegativeInfinity);

        /// <summary>The answers from this state to the one its worth is: none where that is its own value.</summary>
        public IReadOnlyList<MacroAction> Line()
        {
            if (Children.Count == 0) return [];
            var (answer, best) = Children.MaxBy(c => c.Node.Worth());
            return Cut && best.Worth() < Value ? [] : [answer, .. best.Line()];
        }
    }
}
