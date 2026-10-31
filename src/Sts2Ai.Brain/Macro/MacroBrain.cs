using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The macro brain's L0 (docs: macro-brain/search): it values each option's afterstate with V and takes the best, the first
/// legal one on a tie. In SL, with the node's clone (<see cref="MacroDecision.Clone"/>), each option is played in the
/// real game and searched on through the node's screens (<see cref="CloneSearch"/>); only the map's moves, which leave
/// the node, are predicted, as the point added to the path. Without it, version 0: the afterstate is predicted from what
/// the screen shows (the card a reward adds, the relic, the gold a purchase costs, the HP a rest heals, a card's upgrade
/// preview), not applied in a world. An option whose result the screen does not show (most event options, a reroll, a transform) is valued as the state before it. One that opens a
/// choice (a card reward, a removal, smithing) is valued as the best pick in it, so a choice that would be skipped is not opened.
/// It also answers the card prompts outside combat (upgrade, remove, transform, choose a card).
///
/// With <paramref name="explore"/>, for self-play, it draws each answer by the same values instead of taking the best
/// (docs: learning/loop §9); at a card prompt, each pick and whether to stop. With <paramref name="uniform"/>, for self-play
/// (plan v6 §3), some answers are drawn evenly whatever V says (<see cref="Uniform"/>): a card reward's outcome among
/// its cards and skipping it, a card prompt's picks, and on every other screen its options but a potion's (the belt,
/// which every screen offers) and those that would come back to a state already decided.
/// </summary>
public sealed class MacroBrain(IRunValue value, Softmax? explore = null, Uniform? uniform = null) : IMacroPolicy, IChoicePolicy
{
    /// <summary>Each card reward's draw by <c>uniform</c> (null: none), by its floor and cards, made when it is first seen.</summary>
    private readonly Dictionary<string, int?> _draws = [];

    /// <summary>Whether the last answer was drawn: by <c>explore</c> and valued below the best (<see cref="Softmax.Explored"/>;
    /// at a card prompt, any of its steps), or by <c>uniform</c>.</summary>
    public bool Explored { get; private set; }

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

    public string Choose(MacroDecision decision)
    {
        if (decision.Legal.Count == 0) throw new InvalidOperationException("no legal action");
        Explored = false;
        var options = Weigh(decision);
        var values = options.Select(o => o.Value).ToList();
        int pick;
        if (Drawn(decision.Observation, options) is { } drawn)
        {
            pick = Softmax.Best(drawn);
            Explored = double.IsPositiveInfinity(drawn[pick]) || values[pick] < values[Softmax.Best(values)];
        }
        else if (Even(decision.Observation, options) is { } even)
        {
            pick = even;
            Explored = true;
        }
        else pick = Pick(values);
        var option = options[pick];
        _opened = option.Opens;
        if (decision.Clone != null) _decided.Add(CloneSearch.Key(Root(decision)));
        return option.Action;
    }

    /// <summary>
    /// With <c>uniform</c>, the options' values with a card reward's drawn outcome first (+∞) and its others last (−∞);
    /// null where nothing is drawn. On the rewards screen, a card reward drawn to be skipped is never opened and one drawn
    /// to be taken is opened now; on the card reward screen, the drawn card, or skipping, is taken.
    /// </summary>
    private List<double>? Drawn(Observation obs, List<Option> options)
    {
        if (uniform == null) return null;
        var values = options.Select(o => o.Value).ToList();
        bool any = false;
        switch (obs.Screen)
        {
            case RewardsScreen screen:
                for (int k = 0; k < options.Count; k++)
                {
                    var (verb, arg) = Split(options[k].Action);
                    if (verb != "reward" || screen.Rewards.FirstOrDefault(r => r.Index == int.Parse(arg)) is not { Kind: "card", Cards: { } offered }) continue;
                    if (Draw(obs.Run, offered) is not { } drawn) continue;
                    values[k] = drawn == offered.Count ? double.NegativeInfinity : double.PositiveInfinity;
                    any = true;
                }
                break;
            case CardRewardScreen screen when Draw(obs.Run, screen.Cards) is { } drawn:
                string wanted = drawn < screen.Cards.Count ? $"card:{drawn}" : "card_alt:Skip";
                int at = options.FindIndex(o => o.Action == wanted);
                if (at < 0) break;
                for (int k = 0; k < options.Count; k++) values[k] = k == at ? double.PositiveInfinity : double.NegativeInfinity;
                any = true;
                break;
        }
        return any ? values : null;
    }

    private int? Draw(RunView run, IReadOnlyList<CardView> offered)
    {
        string key = $"{run.Floor}:{string.Join(",", offered.Select(c => $"{c.Id}+{c.Up ?? 0}"))}";
        if (!_draws.TryGetValue(key, out var drawn)) _draws[key] = drawn = uniform!.Draw(offered.Count + 1);
        return drawn;
    }

    /// <summary>
    /// With <c>uniform</c>, an option drawn evenly, or null to leave it to V: on every screen but the card reward's (its
    /// outcome is drawn as a whole, <see cref="Drawn"/>), among the options that are not a potion's and not worth −∞
    /// (they would come back to a state already decided), when there are two or more.
    /// </summary>
    private int? Even(Observation obs, List<Option> options)
    {
        if (uniform == null || obs.Screen is CardRewardScreen) return null;
        var open = Enumerable.Range(0, options.Count)
            .Where(k => !double.IsNegativeInfinity(options[k].Value) && Split(options[k].Action).Verb is not ("potion" or "discard_potion"))
            .ToList();
        return open.Count >= 2 && uniform.Draw(open.Count) is { } drawn ? open[drawn] : null;
    }

    /// <summary>With <c>uniform</c>, a card prompt's picks drawn evenly (<see cref="Uniform.Picks"/>), at least <paramref name="min"/>; or null.</summary>
    private IReadOnlyList<int>? Even(CardChoice choice, int min)
    {
        int n = choice.Options.Count;
        return uniform?.Picks(n, Math.Min(min, n), Math.Min(choice.Max, n));
    }

    /// <summary>Each legal action, in order, with its afterstate and its value: what <see cref="Choose(MacroDecision)"/> picks by.</summary>
    public IReadOnlyList<(string Action, RunView After, double Value)> Options(MacroDecision decision) =>
        Weigh(decision).Select(o => (o.Action, o.After, o.Value)).ToList();

    /// <param name="Opens">Whether the option opens a card prompt it was valued by.</param>
    private sealed record Option(string Action, RunView After, double Value, bool Opens);

    private List<Option> Weigh(MacroDecision decision)
    {
        var obs = decision.Observation;
        if (decision.Clone is not { } clone)
            return decision.Legal.Select(a => { var after = After(obs, a); return new Option(a, after, value.Value(after), OpensPrompt(obs, a)); }).ToList();
        var played = decision.Legal.Where(a => !Travels(obs, a)).ToList();
        var found = new CloneSearch(value, clone, _decided).Search(Root(decision), played.Select(a => (IReadOnlyList<string>)[a]).ToList());
        var options = new List<Option>();
        int k = 0;
        foreach (var a in decision.Legal)
        {
            if (Travels(obs, a))
            {
                var after = After(obs, a);
                options.Add(new(a, after, value.Value(after), false));
                continue;
            }
            var (state, worth) = found[k++];
            options.Add(new(a, state.Run, worth, state.Surface == Surfaces.CardChoice));
        }
        return options;
    }

    /// <summary>The decision as a probe of no answers shows it.</summary>
    private static Afterstate Root(MacroDecision decision) =>
        new(decision.Observation.Run, decision.Surface, decision.Observation.Screen, decision.Legal);

    /// <summary>A map move: the node's exit, which its clone cannot play past.</summary>
    private static bool Travels(Observation obs, string action) => obs.Screen is MapScreen && Split(action).Verb == "map";

    /// <summary>
    /// On the map, the rest of the act's route after <paramref name="action"/>: each next point along the map's paths as
    /// <see cref="Choose(MacroDecision)"/> would take it if nothing else changed, up to the boss.
    /// </summary>
    public IReadOnlyList<string> Plan(MacroDecision decision, string action)
    {
        var obs = decision.Observation;
        if (obs.Screen is not MapScreen || Split(action).Verb != "map") return [];
        var points = obs.Run.Map.Points.ToDictionary(p => new Coord(p.Col, p.Row));
        var route = new List<string>();
        while (true)
        {
            obs = obs with { Run = After(obs, action) };
            var next = points[obs.Run.Map.Visited[^1]].Next;
            if (next.Count == 0) return route;
            action = next.Select(c => $"map:{c.Col},{c.Row}").MaxBy(a => Value(obs, a))!;
            route.Add(action);
        }
    }

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        if (decision.Clone is { } clone) return Choose(decision, clone);
        var choice = decision.Choice;
        var run = decision.Observation?.Run ?? throw new InvalidOperationException("a card prompt outside combat without its run");
        // A transform is valued as the removal it starts with: what the new card will be is not shown.
        Func<RunView, CardView, RunView> pick = choice.Context.Purpose switch
        {
            "deck_for_removal" or "deck_for_transformation" => (s, card) => s with { Deck = Without(s.Deck, card) },
            "deck_for_upgrade" => Upgrade,
            "choose_a_card_screen" or "simple_grid_for_rewards" => (s, card) => s with { Deck = [.. s.Deck, card] },
            _ => (s, _) => s,
        };
        // Greedy: add the best pick while it helps, or while the prompt wants more.
        int min = _opened ? Math.Max(choice.Min, 1) : choice.Min;
        _opened = false;
        Explored = false;
        if (Even(choice, min) is { } even)
        {
            Explored = true;
            return even;
        }
        var picks = new List<int>();
        var state = run;
        double current = value.Value(state);
        while (picks.Count < Math.Min(choice.Max, choice.Options.Count))
        {
            var options = Enumerable.Range(0, choice.Options.Count).Where(i => !picks.Contains(i))
                .Select(i => { var next = pick(state, choice.Options[i]); return (Index: i, Value: value.Value(next), State: next); })
                .ToList();
            // Stopping is an option once the prompt has its minimum, listed first so that it wins a tie.
            if (picks.Count >= min) options.Insert(0, (-1, current, state));
            var taken = options[Pick(options.Select(o => o.Value).ToList())];
            if (taken.Index < 0) break;
            picks.Add(taken.Index);
            (current, state) = (taken.Value, taken.State);
        }
        return picks;
    }

    /// <summary>
    /// SL: a card prompt answered on the node's clone. A local search over the answers the prompt accepts, from no pick
    /// (or the first ones it needs): each step weighs the answer one pick away (one more, one fewer, one swapped; the
    /// same card twice only if the deck has it twice), each searched on (<see cref="CloneSearch"/>), and moves to the best
    /// while it beats the answer it has, which is listed first so that it wins a tie.
    /// </summary>
    private IReadOnlyList<int> Choose(ChoiceDecision decision, IMacroClone clone)
    {
        var choice = decision.Choice;
        var obs = decision.Observation ?? throw new InvalidOperationException("a card prompt outside combat without its run");
        int min = _opened ? Math.Max(choice.Min, 1) : choice.Min;
        _opened = false;
        Explored = false;
        var root = new Afterstate(obs.Run, Surfaces.CardChoice, obs.Screen, []);
        if (Even(choice, min) is { } even)
        {
            _decided.Add(CloneSearch.Key(root));
            Explored = true;
            return even;
        }
        var groups = CloneSearch.Groups(choice);
        // An answer is how many of each group it picks.
        var counts = new int[groups.Count];
        for (int left = Math.Min(min, choice.Options.Count), g = 0; left > 0; g++)
        {
            counts[g] = Math.Min(left, groups[g].Count);
            left -= counts[g];
        }
        var worth = new Dictionary<string, double>();
        string Answer(int[] c) => CloneSearch.Pick(c.SelectMany((n, g) => groups[g].Take(n)));
        // Without exploring, each step is better than the last; exploring could wander, so the steps are capped.
        for (int step = 0; step < 4 * Math.Max(choice.Max, 1); step++)
        {
            var current = counts;
            var near = Near(current, groups, min, choice.Max).Prepend(current).ToList();
            var fresh = near.Select(Answer).Where(a => !worth.ContainsKey(a)).Distinct().ToList();
            if (fresh.Count > 0)
            {
                var found = new CloneSearch(value, clone, _decided).Search(root, fresh.Select(a => (IReadOnlyList<string>)[a]).ToList());
                for (int i = 0; i < fresh.Count; i++) worth[fresh[i]] = found[i].Value;
            }
            var taken = near[Pick(near.Select(c => worth[Answer(c)]).ToList())];
            if (taken == current) break;
            counts = taken;
        }
        _decided.Add(CloneSearch.Key(root));
        return counts.SelectMany((n, g) => groups[g].Take(n)).Order().ToList();
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

    /// <summary>
    /// The first best of <paramref name="values"/>, or, when exploring, a draw by their logs: V is a chance that spans
    /// orders of magnitude within a run (at A10 from 1e-3 down to 1e-7), so the temperature is in its ratios, the same at
    /// any V. An option worth nothing is never drawn, unless all are.
    /// </summary>
    private int Pick(IReadOnlyList<double> values)
    {
        int best = Softmax.Best(values);
        if (explore == null || values[best] <= 0) return best;
        var logs = values.Select(v => v > 0 ? Math.Log(v) : double.NegativeInfinity).ToList();
        int drawn = explore.Draw(logs);
        Explored |= explore.Explored(logs[drawn], logs[best]);
        return drawn;
    }

    private double Value(Observation obs, string action) => value.Value(After(obs, action));

    /// <summary>The predicted afterstate.</summary>
    private RunView After(Observation obs, string action)
    {
        var run = obs.Run;
        var (verb, arg) = Split(action);
        if (verb is "potion" or "discard_potion")
        {
            int slot = int.Parse(arg);
            return run with { Potions = run.Potions.Select((p, i) => i == slot ? null : p).ToList() };
        }
        switch (obs.Screen)
        {
            case MapScreen when verb == "map":
                var at = arg.Split(',');
                return run with { Map = run.Map with { Visited = [.. run.Map.Visited, new Coord(int.Parse(at[0]), int.Parse(at[1]))], Entering = true } };
            case RewardsScreen screen when verb == "reward":
                var reward = screen.Rewards.First(r => r.Index == int.Parse(arg));
                return reward.Kind switch
                {
                    "gold" => run with { Gold = run.Gold + (reward.Amount ?? 0) },
                    "potion" => WithPotion(run, reward.Id!),
                    "relic" => WithRelic(run, reward.Id!),
                    "special_card" when reward.Card != null => run with { Deck = [.. run.Deck, reward.Card] },
                    "card" => Best((reward.Cards ?? []).Select(c => run with { Deck = [.. run.Deck, c] })) ?? run,
                    "remove_card" => BestRemoval(run) ?? run,
                    _ => run,
                };
            case CardRewardScreen screen when verb == "card":
                return run with { Deck = [.. run.Deck, screen.Cards[int.Parse(arg)]] };
            case BundleScreen screen when verb == "bundle":
                return run with { Deck = [.. run.Deck, .. screen.Bundles[int.Parse(arg)]] };
            case EventScreen screen when verb == "event":
                var option = screen.Options.First(o => o.Index == int.Parse(arg));
                if (option.Kills == true) return run with { Hp = 0 };
                return option.Relic != null ? WithRelic(run, option.Relic) : run;
            case ShopScreen screen when verb == "buy":
                var entry = screen.Entries.First(e => e.Slot == int.Parse(arg));
                var paid = run with { Gold = run.Gold - entry.Cost };
                return entry.Kind switch
                {
                    "card" => paid with { Deck = [.. run.Deck, entry.Card!] },
                    "relic" => WithRelic(paid, entry.Id!),
                    "potion" => WithPotion(paid, entry.Id!),
                    _ => paid,
                };
            case ShopScreen screen when verb == "remove":
                // The removal prompt follows; the best removal is what this option is worth.
                var gold = run with { Gold = run.Gold - (screen.RemovalCost ?? 0) };
                return BestRemoval(gold) ?? gold;
            case RestScreen screen when verb == "rest":
                var rest = screen.Options.First(o => o.Id == arg);
                if (rest.Heal is { } heal) return run with { Hp = Math.Min(run.MaxHp, run.Hp + heal) };
                // Smithing opens the upgrade prompt; the best upgrade is what this option is worth.
                if (arg == "SMITH") return BestUpgrade(run) ?? run;
                return run;
            case TreasureScreen screen when verb == "relic":
                return WithRelic(run, screen.Relics[int.Parse(arg)]);
            default:
                return run;
        }
    }

    private RunView? Best(IEnumerable<RunView> runs) => runs.MaxBy(value.Value);

    /// <summary>Whether <see cref="After"/> values <paramref name="action"/> as the best pick of the card prompt it opens.</summary>
    private static bool OpensPrompt(Observation obs, string action)
    {
        var (verb, arg) = Split(action);
        return obs.Screen switch
        {
            RestScreen when verb == "rest" => arg == "SMITH",
            ShopScreen when verb == "remove" => true,
            RewardsScreen screen when verb == "reward" => screen.Rewards.First(r => r.Index == int.Parse(arg)).Kind == "remove_card",
            _ => false,
        };
    }

    /// <summary>The best of the removals the game offers: an Eternal card cannot be removed.</summary>
    private RunView? BestRemoval(RunView run) =>
        Best(Distinct(run.Deck).Where(c => c.Keywords?.Contains("Eternal") != true).Select(c => run with { Deck = Without(run.Deck, c) }));

    private RunView? BestUpgrade(RunView run) => Best(Distinct(run.Deck).Where(c => c.Upgraded != null).Select(c => Upgrade(run, c)));

    /// <summary>The run with <paramref name="card"/> upgraded as its upgrade preview shows (<see cref="CardView.Upgraded"/>);
    /// unchanged for a card that has none, which the game does not let be upgraded.</summary>
    private static RunView Upgrade(RunView run, CardView card) =>
        card.Upgraded is { } upgraded ? run with { Deck = [.. Without(run.Deck, card), upgraded] } : run;

    /// <summary>One of each card the player can tell apart (<see cref="ProtocolJson.CardKey"/>).</summary>
    private static IEnumerable<CardView> Distinct(IReadOnlyList<CardView> deck) => deck.DistinctBy(ProtocolJson.CardKey);

    private static (string Verb, string Arg) Split(string action)
    {
        int colon = action.IndexOf(':');
        return colon < 0 ? (action, "") : (action[..colon], action[(colon + 1)..]);
    }

    private static RunView WithRelic(RunView run, string id) => run with { Relics = [.. run.Relics, new RelicView { Id = id }] };

    private static RunView WithPotion(RunView run, string id)
    {
        int slot = run.Potions.ToList().IndexOf(null);
        return slot < 0 ? run : run with { Potions = run.Potions.Select((p, i) => i == slot ? id : p).ToList() };
    }

    private static List<CardView> Without(IReadOnlyList<CardView> deck, CardView card) => Minus(deck, [card]);

    /// <summary>The cards of <paramref name="deck"/> left after taking out each of <paramref name="cards"/> once.</summary>
    private static List<CardView> Minus(IReadOnlyList<CardView> deck, IReadOnlyList<CardView> cards)
    {
        var left = deck.ToList();
        foreach (var card in cards)
        {
            string key = ProtocolJson.CardKey(card);
            int i = left.FindIndex(c => ProtocolJson.CardKey(c) == key);
            if (i >= 0) left.RemoveAt(i);
        }
        return left;
    }
}
