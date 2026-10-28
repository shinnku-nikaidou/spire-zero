using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The macro brain's L0 (docs: macro brain §2), version 0: it values each option's afterstate with V and takes the
/// best, the first legal one on a tie. The afterstate is predicted from what the screen shows (the card a reward adds,
/// the relic, the gold a purchase costs, the HP a rest heals, a card's upgrade preview), not applied in a world. An option whose result the
/// screen does not show (most event options, a reroll, a transform) is valued as the state before it. One that opens a
/// choice (a card reward, a removal, smithing) is valued as the best pick in it, so a choice that would be skipped is not opened.
/// It also answers the card prompts outside combat (upgrade, remove, transform, choose a card).
///
/// With <paramref name="explore"/>, for self-play, it draws each answer by the same values instead of taking the best
/// (docs: learning §5); at a card prompt, each pick and whether to stop.
/// </summary>
public sealed class MacroBrain(IRunValue value, Softmax? explore = null) : IMacroPolicy, IChoicePolicy
{
    /// <summary>Whether the last answer was drawn and valued below the best (<see cref="Softmax.Explored"/>); at a card
    /// prompt, any of its steps.</summary>
    public bool Explored { get; private set; }

    public string Choose(MacroDecision decision)
    {
        if (decision.Legal.Count == 0) throw new InvalidOperationException("no legal action");
        Explored = false;
        return decision.Legal[Pick(Options(decision).Select(o => o.Value).ToList())];
    }

    /// <summary>Each legal action, in order, with its predicted afterstate and V's value of it: what <see cref="Choose(MacroDecision)"/> picks by.</summary>
    public IReadOnlyList<(string Action, RunView After, double Value)> Options(MacroDecision decision) =>
        decision.Legal.Select(a => { var after = After(decision.Observation, a); return (a, after, value.Value(after)); }).ToList();

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
        var picks = new List<int>();
        var state = run;
        double current = value.Value(state);
        Explored = false;
        while (picks.Count < Math.Min(choice.Max, choice.Options.Count))
        {
            var options = Enumerable.Range(0, choice.Options.Count).Where(i => !picks.Contains(i))
                .Select(i => { var next = pick(state, choice.Options[i]); return (Index: i, Value: value.Value(next), State: next); })
                .ToList();
            // Stopping is an option once the prompt has its minimum, listed first so that it wins a tie.
            if (picks.Count >= choice.Min) options.Insert(0, (-1, current, state));
            var taken = options[Pick(options.Select(o => o.Value).ToList())];
            if (taken.Index < 0) break;
            picks.Add(taken.Index);
            (current, state) = (taken.Value, taken.State);
        }
        return picks;
    }

    /// <summary>The first best of <paramref name="values"/>, or a draw by them when exploring.</summary>
    private int Pick(IReadOnlyList<double> values)
    {
        int best = Softmax.Best(values);
        if (explore == null) return best;
        int drawn = explore.Draw(values);
        Explored |= explore.Explored(values[drawn], values[best]);
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
                return run with { Map = run.Map with { Visited = [.. run.Map.Visited, new Coord(int.Parse(at[0]), int.Parse(at[1]))] } };
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
