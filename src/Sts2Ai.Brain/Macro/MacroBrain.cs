using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// The macro brain's L0: it values each option's afterstate with V and takes the best, the first
/// legal one on a tie. In SL, with the node's clone (<see cref="MacroDecision.Clone"/>), each option is played in the
/// real game and searched on through the node's screens (<see cref="CloneSearch"/>); only the map's moves, which leave
/// the node, are predicted, as the point added to the path. Without it, version 0: every afterstate is predicted from
/// what the screen shows (<see cref="ScreenAfterstates"/>). It also answers the card prompts outside combat (upgrade,
/// remove, transform, choose a card).
///
/// How an answer is taken from the valued options is <see cref="MacroDraws"/>'s: the best in play; in self-play, with
/// <paramref name="uniform"/> or <paramref name="softmax"/>, some are drawn.
///
/// What an option's afterstate is worth, and at which kind of state the options of one decision are compared, is
/// <see cref="MacroLeaf"/>'s: V, but for a run that is over (0 for an event option the screen says kills, and on the
/// clone 1 or 0 as the game ends it) and a combat an option starts (an event's fight), valued by
/// <paramref name="fights"/>, the combat leaf.
/// </summary>
public sealed class MacroBrain(IRunValue value, Uniform? uniform = null, ICombatLeaf? fights = null, Softmax? softmax = null)
    : IMacroPolicy, IChoicePolicy
{
    private readonly ScreenAfterstates _screen = new(value);
    private readonly MacroLeaf _leaf = new(value, fights);
    private readonly MacroDraws _draws = new(uniform, softmax);

    /// <summary>
    /// Whether the last macro answer opens a card prompt it was valued by (smithing, a removal). Stopping that prompt
    /// without a pick would not leave things as they are: it cancels back to the screen that opened it, where the same
    /// answer comes again (at a rest site, which must be used, forever). So the prompt takes at least the pick the
    /// answer was valued by.
    /// </summary>
    private bool _opened;

    /// <summary>
    /// SL: the states of this run's decisions on the node's clone (<see cref="CloneSearch.Key"/>), macro decisions and
    /// card prompts. No answer goes back to one (<see cref="CloneSearch"/>): skipping a card reward returns to the rewards
    /// screen that opened it, which would open it again, forever.
    /// </summary>
    private readonly HashSet<ulong> _decided = [];

    public MacroAnswer Choose(MacroDecision decision)
    {
        if (decision.Legal.Count == 0) throw new InvalidOperationException("no legal action");
        var obs = decision.Observation;
        var options = Weigh(decision);
        var values = options.Select(o => o.Value).ToList();
        bool explored = false;
        int pick;
        if (_draws.CardReward(obs, [.. options.Select(o => o.Action)], values) is { } drawn)
        {
            pick = MacroDraws.Best(drawn);
            explored = double.IsPositiveInfinity(drawn[pick]) || values[pick] < values[MacroDraws.Best(values)];
        }
        else if (_draws.Even(obs, values) is { } even)
        {
            pick = even;
            explored = true;
        }
        else pick = _draws.Pick(values, ref explored);
        var option = options[pick];
        _opened = option.Opens;
        if (decision.Clone != null) _decided.Add(CloneSearch.Key(Root(decision)));
        return new(option.Action, explored, explored ? [] : Rank(options.Select(o => (o.Action, o.Value, o.Line)), option.Action));
    }

    /// <summary>Each legal action, in order, with its afterstate and its value: what <see cref="Choose(MacroDecision)"/> picks by.</summary>
    public IReadOnlyList<(MacroAction Action, RunView After, double Value)> Options(MacroDecision decision) =>
        [.. Weigh(decision).Select(o => (o.Action, o.After, o.Value))];

    /// <param name="Opens">Whether the option opens a card prompt it was valued by.</param>
    /// <param name="Line">SL: the answers after it within the node along which it was valued (<see cref="CloneSearch"/>).</param>
    private sealed record Option(MacroAction Action, RunView After, double Value, bool Opens, IReadOnlyList<MacroAction>? Line = null);

    private List<Option> Weigh(MacroDecision decision)
    {
        var obs = decision.Observation;
        var legal = Offered(obs.Run, obs.Screen, decision.Legal);
        var moves = legal.Where(a => ScreenAfterstates.Travels(obs, a)).ToList();
        List<Option> options;
        if (decision.Clone is not { } clone)
        {
            options = [.. legal.Select(a =>
            {
                var after = _screen.After(obs, a);
                return new Option(a, after, ScreenAfterstates.Kills(obs, a) ? MacroLeaf.Lost : _leaf.Value(after), ScreenAfterstates.OpensPrompt(obs, a));
            })];
        }
        else
        {
            // A move leaves the node, which the clone cannot play past: its afterstate is predicted, the point entered.
            var played = legal.Where(a => !moves.Contains(a)).ToList();
            var found = new CloneSearch(_leaf, clone, _decided).Search(Root(decision), [.. played.Select(a => (IReadOnlyList<MacroAction>)[a])]);
            options = [];
            int k = 0;
            foreach (var a in legal)
            {
                if (moves.Contains(a))
                {
                    var after = _screen.After(obs, a);
                    options.Add(new(a, after, _leaf.Value(after), false));
                    continue;
                }
                var (state, worth, line) = found[k++];
                options.Add(new(a, state.Run, worth, state.Surface == Surfaces.CardChoice, line));
            }
        }
        // On the map every option is valued entering a point (MacroLeaf): one that stays on the map, as its best move.
        // One worth −∞ (it would come back to a state already decided) stays so.
        if (moves.Count == 0) return options;
        return [.. options.Select(o => moves.Contains(o.Action) || double.IsNegativeInfinity(o.Value) ? o : o with { Value = _leaf.BestMove(o.After, moves) })];
    }

    /// <summary>
    /// The options weighed: the legal actions but a potion's discard, unless the belt is full and the screen offers a
    /// potion (a reward, the shop's) for the discard to make room for. Otherwise a discard can only lose the potion; weighed
    /// by V all the same, it was taken wherever V rated a potion below an empty slot, and the data, holding few potions
    /// kept, could not show V otherwise (self-play threw away 69-79 % of its potions in v9g0 and v9g1). The same at every
    /// state the clone's search reaches (<see cref="CloneSearch"/>).
    ///
    /// This is a rule about play, which is otherwise the nets' to learn, so it has a way out. Each new V's report says how
    /// often it would still throw a potion away (<c>potion_below_empty</c>, <c>learn/v/potions.py</c>: of the held-out
    /// decisions that hold a potion, the share V values higher with one of them gone). Under that file's limit this
    /// rule changes next to nothing, and it is deleted.
    ///
    /// And a potion reward the belt has room for is taken: while one is on the rewards screen (the game lists it only
    /// with a free slot, <c>RewardActions.Legal</c>), proceeding is not offered. The user's rule (2026-11-16: never leave
    /// a potion while the belt has room). V rated many potions below an empty slot, a bias it inherited from the discards
    /// above, and left 52-61 % of them on the screen (the A10 tests of v10-2 and v10-2-2); the user takes 98 % of them.
    ///
    /// And at a merchant a Foul Potion the belt holds is thrown first, the only option while one is left: 100 gold each
    /// (the user's rule, 2026-11-16). It is never thrown in a fight (<see cref="PimcBrain.Ordered"/>) and never discarded,
    /// and a map move into a shop is valued with its gold (<see cref="ScreenAfterstates.Entered"/>).
    /// </summary>
    public static List<MacroAction> Offered(RunView run, ScreenView? screen, IReadOnlyList<MacroAction> legal)
    {
        bool room = run.Potions.All(p => p != null) && screen switch
        {
            RewardsScreen rewards => rewards.Rewards.Any(r => r.Kind == "potion"),
            ShopScreen shop => shop.Entries.Any(e => e.Kind == "potion"),
            _ => false,
        };
        bool potionWaits = screen is RewardsScreen screenRewards && legal.Any(a =>
            a.Verb == MacroAction.Reward && screenRewards.Rewards.Any(r => r.Index == a.Index && r.Kind == "potion"));
        // At a merchant a Foul Potion is thrown before anything else: 100 gold each (the user's rule, 2026-11-16). The game
        // lists the throw only while the merchant's inventory is closed (FoulPotion.PassesCustomUsabilityCheck).
        if (screen is ShopScreen && legal.Where(a => a.Verb == MacroAction.Potion && run.Potions[a.Index] == PimcBrain.Foul).Take(1).ToList() is [var foul])
            return [foul];
        // A Foul Potion is kept for a merchant: never discarded (the user's rule, 2026-11-16; in their own runs one of 41).
        var offered = legal.Where(a => (room || a.Verb != MacroAction.DiscardPotion) && !(potionWaits && a == MacroAction.RewardsDone)
            && !(a.Verb == MacroAction.DiscardPotion && run.Potions[a.Index] == PimcBrain.Foul)).ToList();
        return offered.Count > 0 ? offered : [.. legal];
    }

    /// <summary>The decision as a probe of no answers shows it.</summary>
    private static Afterstate Root(MacroDecision decision) =>
        new(decision.Observation.Run, decision.Surface, decision.Observation.Screen, decision.Legal);

    /// <summary>
    /// On the map, the rest of the act's route after <paramref name="action"/>: each next point along the map's paths as
    /// <see cref="Choose(MacroDecision)"/> would take it if nothing else changed, up to the boss.
    /// </summary>
    public IReadOnlyList<MacroAction> Plan(MacroDecision decision, MacroAction action)
    {
        var obs = decision.Observation;
        if (!ScreenAfterstates.Travels(obs, action)) return [];
        var points = obs.Run.Map.Points.ToDictionary(p => new Coord(p.Col, p.Row));
        var route = new List<MacroAction>();
        while (true)
        {
            obs = obs with { Run = _screen.After(obs, action) };
            var next = points[obs.Run.Map.Visited[^1]].Next;
            if (next.Count == 0) return route;
            action = next.Select(c => MacroAction.Of(MacroAction.Map, c.Col, c.Row)).MaxBy(a => _leaf.Value(_screen.After(obs, a)));
            route.Add(action);
        }
    }

    public ChoiceAnswer Choose(ChoiceDecision decision)
    {
        if (decision.Clone is { } clone) return Choose(decision, clone);
        var choice = decision.Choice;
        var run = decision.Observation?.Run ?? throw new InvalidOperationException("a card prompt outside combat without its run");
        // Greedy: add the best pick while it helps, or while the prompt wants more.
        int min = _opened ? Math.Max(choice.Min, 1) : choice.Min;
        _opened = false;
        if (_draws.Even(choice, min) is { } even) return new(even, Explored: true);
        bool explored = false;
        var picks = new List<int>();
        var state = run;
        double current = _leaf.Value(state);
        // A prompt asked in a fight the run's rule wins picks among the fight's cards: nothing of the run changes by it.
        RunView Picked(RunView from, int i) => choice.Context.InCombat ? from : ScreenAfterstates.Picked(from, choice.Options[i], choice.Context.Purpose);
        // The first step's answers, one pick or none, to show beside the answer.
        var first = new List<(MacroAction Action, double Value)>();
        while (picks.Count < Math.Min(choice.Max, choice.Options.Count))
        {
            var options = Enumerable.Range(0, choice.Options.Count).Where(i => !picks.Contains(i))
                .Select(i => { var next = Picked(state, i); return (Index: i, Value: _leaf.Value(next), State: next); })
                .ToList();
            // Stopping is an option once the prompt has its minimum, listed first so that it wins a tie.
            if (picks.Count >= min) options.Insert(0, (-1, current, state));
            if (picks.Count == 0) first.AddRange(options.Select(o => (MacroAction.Pick(o.Index < 0 ? [] : [o.Index]), o.Value)));
            var taken = options[_draws.Pick([.. options.Select(o => o.Value)], ref explored)];
            if (taken.Index < 0) break;
            picks.Add(taken.Index);
            (current, state) = (taken.Value, taken.State);
        }
        var answer = MacroAction.Pick(picks.Order());
        return new(picks, explored, explored ? [] : Rank(first.Where(o => o.Action != answer).Append((answer, current)).Select(o => (o.Item1, o.Item2, (IReadOnlyList<MacroAction>?)null)), answer));
    }

    /// <summary>Options to show: the chosen first, then best first; a value that is not finite (a state already decided) left
    /// out. Each carries its line, if it has one, as its plan.</summary>
    private static List<Candidate> Rank(IEnumerable<(MacroAction Action, double Value, IReadOnlyList<MacroAction>? Line)> options, MacroAction chosen) =>
        [.. options.Where(o => double.IsFinite(o.Value)).OrderByDescending(o => o.Action == chosen).ThenByDescending(o => o.Value)
            .Select(o => new Candidate(o.Action.ToString(), o.Value, 0, 1, Plan: o.Line is { Count: > 0 } line ? [.. line.Select(a => a.ToString())] : null))];

    /// <summary>
    /// SL: a card prompt answered on the node's clone. A local search over the answers the prompt accepts, from no pick
    /// (or the first ones it needs): each step weighs the answer one pick away (one more, one fewer, one swapped; the
    /// same card twice only if the deck has it twice), each searched on (<see cref="CloneSearch"/>), and moves to the best
    /// while it beats the answer it has, which is listed first so that it wins a tie.
    /// </summary>
    private ChoiceAnswer Choose(ChoiceDecision decision, IMacroClone clone)
    {
        var choice = decision.Choice;
        var obs = decision.Observation ?? throw new InvalidOperationException("a card prompt outside combat without its run");
        int min = _opened ? Math.Max(choice.Min, 1) : choice.Min;
        _opened = false;
        var root = new Afterstate(obs.Run, Surfaces.CardChoice, obs.Screen, []);
        if (_draws.Even(choice, min) is { } even)
        {
            _decided.Add(CloneSearch.Key(root));
            return new(even, Explored: true);
        }
        bool explored = false;
        var groups = choice.Groups();
        // An answer is how many of each group it picks.
        var counts = new int[groups.Count];
        for (int left = Math.Min(min, choice.Options.Count), g = 0; left > 0; g++)
        {
            counts[g] = Math.Min(left, groups[g].Count);
            left -= counts[g];
        }
        var worth = new Dictionary<MacroAction, double>();
        // In the prompt's canonical order, so that the same picks are the same answer.
        MacroAction Answer(int[] c) => MacroAction.Pick(c.SelectMany((n, g) => groups[g].Take(n)).Order());
        // Without exploring, each step is better than the last; exploring could wander, so the steps are capped.
        for (int step = 0; step < 4 * Math.Max(choice.Max, 1); step++)
        {
            var current = counts;
            var near = Near(current, groups, min, choice.Max).Prepend(current).ToList();
            var fresh = near.Select(Answer).Where(a => !worth.ContainsKey(a)).Distinct().ToList();
            if (fresh.Count > 0)
            {
                var found = new CloneSearch(_leaf, clone, _decided).Search(root, [.. fresh.Select(a => (IReadOnlyList<MacroAction>)[a])]);
                for (int i = 0; i < fresh.Count; i++) worth[fresh[i]] = found[i].Value;
            }
            var taken = near[_draws.Pick([.. near.Select(c => worth[Answer(c)])], ref explored)];
            if (taken == current) break;
            counts = taken;
        }
        _decided.Add(CloneSearch.Key(root));
        return new([.. counts.SelectMany((n, g) => groups[g].Take(n)).Order()], explored,
            explored ? [] : Rank(worth.Select(kv => (kv.Key, kv.Value, (IReadOnlyList<MacroAction>?)null)), Answer(counts)));
    }

    /// <summary>The answers one pick away from <paramref name="counts"/> that the prompt accepts.</summary>
    private static IEnumerable<int[]> Near(int[] counts, IReadOnlyList<IReadOnlyList<int>> groups, int min, int max)
    {
        int total = counts.Sum();
        for (int g = 0; g < counts.Length; g++)
        {
            if (total < max && counts[g] < groups[g].Count) yield return Moved(counts, g, -1);
            if (total > min && counts[g] > 0) yield return Moved(counts, -1, g);
            if (counts[g] == 0) continue;
            for (int h = 0; h < counts.Length; h++)
                if (h != g && counts[h] < groups[h].Count) yield return Moved(counts, h, g);
        }
    }

    /// <summary><paramref name="counts"/> with one more pick of group <paramref name="add"/> and one fewer of <paramref name="drop"/> (-1: none).</summary>
    private static int[] Moved(int[] counts, int add, int drop)
    {
        var moved = counts.ToArray();
        if (add >= 0) moved[add]++;
        if (drop >= 0) moved[drop]--;
        return moved;
    }
}
