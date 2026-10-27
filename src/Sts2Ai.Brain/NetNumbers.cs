using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// The numbers of cards and relics as both nets read them (<c>learn/train_value.py</c>'s card_numbers and
/// relic_numbers): what a card or relic shows beyond its ids. A net file made before these inputs existed does not
/// declare them, and is fed without them (<see cref="Declared"/>).
/// </summary>
public static class NetNumbers
{
    /// <summary>Every scaled number the nets read is capped at ±this: a few states show huge ones (an erupting
    /// WATERFALL_GIANT's HP).</summary>
    public const double Cap = 10;

    public static float Capped(double x) => (float)Math.Clamp(x, -Cap, Cap);

    /// <summary>The inputs of <paramref name="all"/> that the graph declares.</summary>
    public static List<NamedOnnxValue> Declared(InferenceSession session, IEnumerable<NamedOnnxValue> all) =>
        all.Where(v => session.InputMetadata.ContainsKey(v.Name)).ToList();

    /// <summary>
    /// The cards' numbers, in the cards' order, padded to at least one: costs (cost / 3, 0 for X or none), mod_amounts
    /// (the enchantment's and affliction's amounts / 10, in the order the modifiers are listed), card_scalars (star cost
    /// / 3, and 1 for an X cost), and each number / 10 by name (vars, var_values, var_mask).
    /// </summary>
    public static NamedOnnxValue[] Cards(IReadOnlyList<CardView> cards, Func<string, string, long> index)
    {
        int n = Math.Max(1, cards.Count);
        int m = Math.Max(1, cards.Select(c => c.Vars?.Count ?? 0).DefaultIfEmpty(0).Max());
        var costs = new DenseTensor<float>([1, n]);
        var amounts = new DenseTensor<float>([1, n, 2]);
        var scalars = new DenseTensor<float>([1, n, 2]);
        var names = new DenseTensor<long>([1, n, m]);
        var values = new DenseTensor<float>([1, n, m]);
        var mask = new DenseTensor<float>([1, n, m]);
        for (int k = 0; k < cards.Count; k++)
        {
            var card = cards[k];
            costs[0, k] = card.Cost is { } cost && cost > 0 ? (float)(cost / 3.0) : 0;
            var modifiers = new[] { card.Enchant, card.Affliction }.OfType<ModifierView>().ToList();
            for (int j = 0; j < modifiers.Count; j++) amounts[0, k, j] = Capped(modifiers[j].Amount / 10.0);
            scalars[0, k, 0] = card.Stars is { } stars && stars > 0 ? (float)(stars / 3.0) : 0;
            scalars[0, k, 1] = card.XCost == true ? 1 : 0;
            int v = 0;
            foreach (var (name, value) in card.Vars ?? new Dictionary<string, decimal>())
            {
                names[0, k, v] = index("vars", name);
                values[0, k, v] = Capped((double)value / 10);
                mask[0, k, v++] = 1;
            }
        }
        return
        [
            NamedOnnxValue.CreateFromTensor("costs", costs), NamedOnnxValue.CreateFromTensor("mod_amounts", amounts),
            NamedOnnxValue.CreateFromTensor("card_scalars", scalars), NamedOnnxValue.CreateFromTensor("vars", names),
            NamedOnnxValue.CreateFromTensor("var_values", values), NamedOnnxValue.CreateFromTensor("var_mask", mask),
        ];
    }

    /// <summary>The relics' numbers, in the relics' order, padded to at least one: relic_values (counter / 10, or 0 when
    /// none shows; whether one shows; whether the relic is melted) and relic_status (its status, Normal when none).</summary>
    public static NamedOnnxValue[] Relics(IReadOnlyList<RelicView> relics, Func<string, string, long> index)
    {
        int n = Math.Max(1, relics.Count);
        var values = new DenseTensor<float>([1, n, 3]);
        var status = new DenseTensor<long>([1, n]);
        for (int k = 0; k < relics.Count; k++)
        {
            var relic = relics[k];
            values[0, k, 0] = relic.Counter is { } counter ? Capped(counter / 10.0) : 0;
            values[0, k, 1] = relic.Counter != null ? 1 : 0;
            values[0, k, 2] = relic.Melted == true ? 1 : 0;
            status[0, k] = index("relic_status", relic.Status ?? "Normal");
        }
        return [NamedOnnxValue.CreateFromTensor("relic_values", values), NamedOnnxValue.CreateFromTensor("relic_status", status)];
    }
}
