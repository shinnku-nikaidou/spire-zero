using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// L0's search of a decision on the node's clone in SL (docs: macro-brain/search §3). Each option is played in the real game,
/// and what the game asks next is its afterstate. While that is still a screen of the node (not the map, not a combat,
/// not the run's end), the search goes on: a beam over the screen's own actions (a potion's, which every screen
/// offers, are left out), level by level, keeping the <see cref="Beam"/> states V values best, up to
/// <see cref="MaxProbes"/> probes in all. A state reached twice is searched once. A state is worth the best state after
/// it, or its own value where the search stops (<see cref="Value"/>); an option, its afterstate.
/// A card prompt that needs two picks or more is not searched into: its own decision searches it.
/// The decision itself and the states <paramref name="decided"/> before it are never searched: an answer that comes back
/// to one goes round in a cycle (a card reward skipped returns to the rewards screen that opened it, where the same
/// answer comes again). Such an option is worth −∞, so it is never taken; deeper, such an answer adds nothing.
/// </summary>
/// <param name="decided">The keys (<see cref="Key"/>) of the states decided before this decision.</param>
/// <param name="fights">Values a combat an answer starts (an event's fight); a search that meets one without it throws.</param>
public sealed class CloneSearch(IRunValue value, IMacroClone clone, IReadOnlySet<ulong>? decided = null, ICombatLeaf? fights = null)
{
    public const int Beam = 16;
    public const int MaxProbes = 1024;

    /// <summary>The probes the last search played.</summary>
    public int Probes { get; private set; }

    /// <param name="root">The decision itself, as a probe of no answers would show it.</param>
    /// <param name="options">Each option, as the answers it plays from the decision.</param>
    /// <returns>Each option's afterstate and worth, in order.</returns>
    public IReadOnlyList<(Afterstate After, double Value)> Search(Afterstate root, IReadOnlyList<IReadOnlyList<string>> options)
    {
        var seen = new Dictionary<ulong, Node?> { [Key(root)] = null };
        foreach (var key in decided ?? new HashSet<ulong>()) seen.TryAdd(key, null);
        var firsts = new List<Node>();
        var frontier = new List<Node>();
        var afters = clone.After(options);
        Probes = options.Count;
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
            frontier.Add(node);
        }
        for (int level = 2; frontier.Count > 0 && Probes < MaxProbes; level++)
        {
            var parents = frontier.Where(n => Inside(n.After)).OrderByDescending(n => n.Value).Take(Beam).ToList();
            var jobs = parents.SelectMany(p => Answers(p.After).Select(a => (Parent: p, Path: (IReadOnlyList<string>)[.. p.Path, a]))).ToList();
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
                    if (same?.Level == level) jobs[i].Parent.Children.Add(same);
                    continue;
                }
                var node = new Node(jobs[i].Path, next[i], Value(next[i]), level);
                seen[key] = node;
                jobs[i].Parent.Children.Add(node);
                frontier.Add(node);
            }
        }
        return firsts.Select(n => (n.After, n.Worth())).ToList();
    }

    /// <summary>
    /// What a state is worth where the search stops: a run that is over, 1 if won and 0 if lost; a combat about to start,
    /// the combat leaf at its first round's start, the HP it will cost and its end priced by V (V reads a run in a room
    /// as the state after that room, so V alone would take the fight as won at no cost); any other, V.
    /// </summary>
    private double Value(Afterstate after) => after switch
    {
        { Surface: null } => (after.Won ?? throw new InvalidDataException("a run over with no outcome")) ? 1 : 0,
        { Screen: CombatScreen { Combat: var combat } } =>
            (fights ?? throw new InvalidOperationException("an answer starts a combat, and the search has no combat leaf to value it"))
            .Value(CombatStatus.InProgress, combat, after.Run, []),
        _ => value.Value(after.Run),
    };

    /// <summary>Whether the search goes on from <paramref name="after"/>: a screen of the node.</summary>
    private static bool Inside(Afterstate after) => after.Surface is not (null or Surfaces.Map or Surfaces.Combat);

    /// <summary>The answers searched at a screen of the node: its own actions, or a card prompt's single picks.</summary>
    private static IEnumerable<string> Answers(Afterstate after)
    {
        if (after.Screen is not CardChoiceScreen { Choice: var choice })
        {
            foreach (var action in after.Legal)
                if (!IsPotion(action)) yield return action;
            yield break;
        }
        if (choice.Min > 1) yield break;
        if (choice.Min == 0) yield return Pick([]);
        if (choice.Max < 1) yield break;
        foreach (var group in Groups(choice)) yield return Pick([group[0]]);
    }

    public static bool IsPotion(string action) => action.StartsWith("potion:", StringComparison.Ordinal) || action.StartsWith("discard_potion:", StringComparison.Ordinal);

    /// <summary>A card prompt's answer in a path.</summary>
    public static string Pick(IEnumerable<int> picks) => "pick:" + string.Join(",", picks.Order());

    /// <summary>The prompt's options grouped by what the player can tell apart (<see cref="ProtocolJson.CardKey"/>), each
    /// group's indexes in order: picking k of a group picks its first k.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Groups(CardChoice choice) =>
        [.. Enumerable.Range(0, choice.Options.Count).GroupBy(i => ProtocolJson.CardKey(choice.Options[i])).Select(g => (IReadOnlyList<int>)[.. g])];

    public static ulong Key(Afterstate after) => ProtocolJson.Hash(after);

    private sealed class Node(IReadOnlyList<string> path, Afterstate after, double value, int level)
    {
        private double? _worth;

        public IReadOnlyList<string> Path { get; } = path;
        public Afterstate After { get; } = after;
        public double Value { get; } = value;
        public int Level { get; } = level;
        public List<Node> Children { get; } = [];

        /// <summary>Some of its answers were never probed, the probes having run out.</summary>
        public bool Cut { get; set; }

        public double Worth() => _worth ??= Children.Count == 0 ? Value
            : Math.Max(Children.Max(c => c.Worth()), Cut ? Value : double.NegativeInfinity);
    }
}
