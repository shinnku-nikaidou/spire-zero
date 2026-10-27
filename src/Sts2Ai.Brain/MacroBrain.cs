using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The macro brain's L0 (docs: macro brain §2), version 0: it values each option's afterstate with V and takes the
/// best, the first legal one on a tie. The afterstate is predicted from what the screen shows (the card a reward adds,
/// the relic, the gold a purchase costs, the HP a rest heals), not applied in a world. An option whose result the
/// screen does not show (most event options, a reroll, a transform) is valued as the state before it. One that opens a
/// choice (a card reward, a removal) is valued as the best pick in it, so a choice that would be skipped is not opened.
/// It also answers the card prompts outside combat (upgrade, remove, transform, choose a card).
/// </summary>
public sealed class MacroBrain(RunValue value) : IMacroPolicy, IChoicePolicy
{
    public string Choose(MacroDecision decision)
    {
        var obs = decision.Observation;
        return decision.Legal.MaxBy(a => Value(obs, a)) ?? throw new InvalidOperationException("no legal action");
    }

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        var choice = decision.Choice;
        var run = decision.Observation?.Run ?? throw new InvalidOperationException("a card prompt outside combat without its run");
        // A transform is valued as the removal it starts with: what the new card will be is not shown.
        Func<RunView, CardView, RunView> pick = choice.Context.Purpose switch
        {
            "deck_for_removal" or "deck_for_transformation" => (s, card) => s with { Deck = Without(s.Deck, card) },
            "deck_for_upgrade" => (s, card) => s with { Deck = [.. Without(s.Deck, card), card with { Up = (card.Up ?? 0) + 1 }] },
            "choose_a_card_screen" or "simple_grid_for_rewards" => (s, card) => s with { Deck = [.. s.Deck, card] },
            _ => (s, _) => s,
        };
        // Greedy: add the best pick while it helps, or while the prompt wants more.
        var picks = new List<int>();
        var state = run;
        double current = value.Value(state);
        while (picks.Count < choice.Max)
        {
            var (best, bestValue, bestState) = Enumerable.Range(0, choice.Options.Count).Where(i => !picks.Contains(i))
                .Select(i => { var next = pick(state, choice.Options[i]); return (i, value.Value(next), next); })
                .MaxBy(t => t.Item2);
            if (picks.Count >= choice.Min && bestValue <= current) break;
            picks.Add(best);
            (current, state) = (bestValue, bestState);
        }
        return picks;
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
                // Smithing upgrades one card; which one is decided at the prompt.
                if (arg == "SMITH" && run.Deck.FirstOrDefault(c => c.Up is null or 0) is { } card)
                    return run with { Deck = [.. Without(run.Deck, card), card with { Up = 1 }] };
                return run;
            case TreasureScreen screen when verb == "relic":
                return WithRelic(run, screen.Relics[int.Parse(arg)]);
            default:
                return run;
        }
    }

    private RunView? Best(IEnumerable<RunView> runs) => runs.MaxBy(value.Value);

    private RunView? BestRemoval(RunView run) => Best(run.Deck.Select(c => run with { Deck = Without(run.Deck, c) }));

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
