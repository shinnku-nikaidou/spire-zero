using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// Afterstates predicted from what a screen shows: the run after an option, not applied in a
/// world. The card a reward adds, the relic, the gold a purchase costs, the HP a rest heals, a card's upgrade preview,
/// the point a map move enters. An event's option whose result its button does not show is the run with the option
/// pending (<see cref="RunView.Pending"/>: everything its button shows, which V reads). Another option whose result the
/// screen does not show (a reroll, a transform) leaves the run as it is. One that opens a choice (a card reward, a
/// removal, smithing) is the best pick in it by <paramref name="value"/>, so a choice that would be skipped is not opened.
///
/// NoSL's macro brain values every option by these. In SL the node's clone plays the options for real
/// (<see cref="CloneSearch"/>), and only what leaves the node is predicted here: a map move.
/// </summary>
public sealed class ScreenAfterstates(IRunValue value)
{
    /// <summary>The run after <paramref name="action"/>, as the screen lets it be predicted.</summary>
    public RunView After(Observation obs, MacroAction action)
    {
        var run = obs.Run;
        if (action.Verb is MacroAction.Potion or MacroAction.DiscardPotion)
        {
            int slot = action.Index;
            return run with { Potions = [.. run.Potions.Select((p, i) => i == slot ? null : p)] };
        }
        switch (obs.Screen)
        {
            case MapScreen when action.Verb == MacroAction.Map:
                return Entered(run, action.Coord);
            case RewardsScreen screen when action.Verb == MacroAction.Reward:
                var reward = screen.Rewards.First(r => r.Index == action.Index);
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
            case CardRewardScreen screen when action.Verb == MacroAction.Card:
                return run with { Deck = [.. run.Deck, screen.Cards[action.Index]] };
            case BundleScreen screen when action.Verb == MacroAction.Bundle:
                return run with { Deck = [.. run.Deck, .. screen.Bundles[action.Index]] };
            case EventScreen screen when action.Verb == MacroAction.Event:
                var option = screen.Options.First(o => o.Index == action.Index);
                if (option.Kills == true) return run with { Hp = 0 };
                return option.Relic != null ? WithRelic(run, option.Relic) : run with { Pending = option };
            case ShopScreen screen when action.Verb == MacroAction.Buy:
                var entry = screen.Entries.First(e => e.Slot == action.Index);
                var paid = run with { Gold = run.Gold - entry.Cost };
                return entry.Kind switch
                {
                    "card" => paid with { Deck = [.. run.Deck, entry.Card!] },
                    "relic" => WithRelic(paid, entry.Id!),
                    "potion" => WithPotion(paid, entry.Id!),
                    _ => paid,
                };
            case ShopScreen screen when action == MacroAction.Remove:
                // The removal prompt follows; the best removal is what this option is worth.
                var gold = run with { Gold = run.Gold - (screen.RemovalCost ?? 0) };
                return BestRemoval(gold) ?? gold;
            case RestScreen screen when action.Verb == MacroAction.Rest:
                var rest = screen.Options.First(o => o.Id == action.Arg);
                if (rest.Heal is { } heal) return run with { Hp = Math.Min(run.MaxHp, run.Hp + heal) };
                // Smithing opens the upgrade prompt; the best upgrade is what this option is worth.
                if (action.Arg == "SMITH") return BestUpgrade(run) ?? run;
                return run;
            case TreasureScreen screen when action.Verb == MacroAction.Relic:
                return WithRelic(run, screen.Relics[action.Index]);
            default:
                return run;
        }
    }

    /// <summary>The run after a map move to <paramref name="point"/>: the point added to the path and marked as being
    /// entered, not yet played (<see cref="MapView.Entering"/>). V and its training read a move's afterstate so.
    /// Entering a shop, the Foul Potions held are thrown at the merchant, as the first thing done there
    /// (<see cref="MacroBrain.Offered"/>), so the move's afterstate holds their gold and not them (<see cref="FoulThrown"/>).</summary>
    public static RunView Entered(RunView run, Coord point)
    {
        var entered = run with { Map = run.Map with { Visited = [.. run.Map.Visited, point], Entering = true } };
        return run.Map.Points.FirstOrDefault(p => p.Col == point.Col && p.Row == point.Row)?.Type == "Shop" ? FoulThrown(entered) : entered;
    }

    /// <summary>The gold a Foul Potion thrown at a merchant gives (FoulPotion's GoldVar).</summary>
    public const int FoulGold = 100;

    /// <summary>
    /// The run once its Foul Potions are thrown at a merchant: each gone, and its gold gained as the game's gold hooks
    /// change it (Ectoplasm: none; Bowler Hat: 1.25 times).
    /// </summary>
    public static RunView FoulThrown(RunView run)
    {
        int thrown = run.Potions.Count(p => p == Combat.PimcBrain.Foul);
        if (thrown == 0) return run;
        int each = run.Relics.Any(r => r.Id == "ECTOPLASM") ? 0 : run.Relics.Any(r => r.Id == "BOWLER_HAT") ? FoulGold * 5 / 4 : FoulGold;
        return run with { Gold = run.Gold + thrown * each, Potions = [.. run.Potions.Select(p => p == Combat.PimcBrain.Foul ? null : p)] };
    }

    /// <summary>
    /// Whether <paramref name="action"/> is an event's option whose result its button does not show: its afterstate is
    /// the run with it pending (<see cref="After"/>). Not one the button says kills (lost), nor one that gives a relic
    /// (an ancient's: the relic added).
    /// </summary>
    public static bool Pends(EventScreen screen, MacroAction action) =>
        action.Verb == MacroAction.Event && screen.Options.First(o => o.Index == action.Index) is { Kills: not true, Relic: null };

    /// <summary>An event option the screen says kills: the run ends there, lost.</summary>
    public static bool Kills(Observation obs, MacroAction action) =>
        obs.Screen is EventScreen screen && action.Verb == MacroAction.Event && screen.Options.First(o => o.Index == action.Index).Kills == true;

    /// <summary>A map move: the node's exit, which its clone cannot play past.</summary>
    public static bool Travels(Observation obs, MacroAction action) => obs.Screen is MapScreen && action.Verb == MacroAction.Map;

    /// <summary>Whether <see cref="After"/> gives <paramref name="action"/> as the best pick of the card prompt it opens.</summary>
    public static bool OpensPrompt(Observation obs, MacroAction action) => obs.Screen switch
    {
        RestScreen when action.Verb == MacroAction.Rest => action.Arg == "SMITH",
        ShopScreen when action == MacroAction.Remove => true,
        RewardsScreen screen when action.Verb == MacroAction.Reward => screen.Rewards.First(r => r.Index == action.Index).Kind == "remove_card",
        _ => false,
    };

    /// <summary>
    /// What picking <paramref name="card"/> at a card prompt outside combat does to the run, by the prompt's purpose. A
    /// transform is the removal it starts with: what the new card will be is not shown.
    /// </summary>
    public static RunView Picked(RunView run, CardView card, string purpose) => purpose switch
    {
        "deck_for_removal" or "deck_for_transformation" => run with { Deck = Without(run.Deck, card) },
        "deck_for_upgrade" => Upgrade(run, card),
        "choose_a_card_screen" or "simple_grid_for_rewards" => run with { Deck = [.. run.Deck, card] },
        _ => run,
    };

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

    private static RunView WithRelic(RunView run, string id) => run with { Relics = [.. run.Relics, new RelicView { Id = id }] };

    private static RunView WithPotion(RunView run, string id)
    {
        int slot = run.Potions.ToList().IndexOf(null);
        return slot < 0 ? run : run with { Potions = [.. run.Potions.Select((p, i) => i == slot ? id : p)] };
    }

    /// <summary>The cards of <paramref name="deck"/> left after taking out <paramref name="card"/> once.</summary>
    private static List<CardView> Without(IReadOnlyList<CardView> deck, CardView card)
    {
        var left = deck.ToList();
        string key = ProtocolJson.CardKey(card);
        int i = left.FindIndex(c => ProtocolJson.CardKey(c) == key);
        if (i >= 0) left.RemoveAt(i);
        return left;
    }
}
