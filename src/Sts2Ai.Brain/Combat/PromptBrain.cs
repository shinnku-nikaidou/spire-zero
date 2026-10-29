using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The card prompts asked during a combat, version 0 (docs: combat brain §2, rule 7): a fixed rule per kind of prompt, from
/// what happens to the picked cards. Cards that leave the hand this turn (a discard, an exhaust, a card put on top of
/// the draw pile) are the least useful ones; a discard takes Sly cards first, since discarding plays them. A card made
/// free is the dearest one. Every other prompt adds to or improves the picked cards (a card into the hand, a copy, an
/// upgrade), so it takes the most useful ones, as many as it may. It reads the prompt alone, so search worlds can ask it
/// too, and it never draws at random: ties go to the first option in canonical order.
/// </summary>
public sealed class PromptBrain : IChoicePolicy
{
    public static PromptBrain Instance { get; } = new();

    /// <summary>Sources whose picks go on top of the draw pile: gone this turn, drawn the next.</summary>
    private static readonly HashSet<string> PutOnTop = ["PHOTON_CUT", "THINKING_AHEAD"];

    /// <summary>Sources that make the picked card free for the rest of the combat.</summary>
    private static readonly HashSet<string> MakeFree = ["TOUCH_OF_INSANITY"];

    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        var choice = decision.Choice;
        var context = choice.Context;
        var options = Enumerable.Range(0, choice.Options.Count);
        if (context.Purpose == "hand_for_discard" || context.PromptKey == "card_selection.TO_EXHAUST" || PutOnTop.Contains(context.Source ?? ""))
        {
            bool discard = context.Purpose == "hand_for_discard";
            // Worst first; beyond the minimum, only cards that are better gone.
            var order = options.OrderByDescending(i => discard && Has(choice.Options[i], "Sly"))
                .ThenBy(i => Worth(choice.Options[i])).ToList();
            var gladly = order.Where(i => Has(choice.Options[i], "Unplayable") || discard && Has(choice.Options[i], "Sly")).ToList();
            return order.Take(Math.Clamp(gladly.Count, choice.Min, choice.Max)).ToList();
        }
        if (MakeFree.Contains(context.Source ?? ""))
            return options.OrderByDescending(i => choice.Options[i].Cost ?? 0).ThenByDescending(i => Worth(choice.Options[i])).Take(choice.Max).ToList();
        return options.OrderByDescending(i => Worth(choice.Options[i])).Take(choice.Max).ToList();
    }

    /// <summary>
    /// How useful a card is to hold: its numbers, a draw worth a few points, and a flat part for what the numbers
    /// miss. An unplayable card (a status, a curse) is worth nothing.
    /// </summary>
    public static double Worth(CardView card)
    {
        if (Has(card, "Unplayable")) return -100;
        double Var(string name) => (double)(card.Vars?.GetValueOrDefault(name) ?? 0);
        return 5 + Var("Damage") + 1.5 * Var("PoisonPower") + Var("Block") + 4 * Var("Cards") + 4 * Var("Energy")
               + 2 * (Var("WeakPower") + Var("VulnerablePower")) + 2 * (card.Up ?? 0);
    }

    private static bool Has(CardView card, string keyword) => card.Keywords?.Contains(keyword) == true;
}
