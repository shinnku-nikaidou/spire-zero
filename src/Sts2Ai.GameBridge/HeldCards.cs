using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Ai.GameBridge;

/// <summary>
/// A card that a power keeps in its own data, such as the card Nightmare adds copies of next turn; the power's tooltip
/// names it. The game gives no access to it, so it is found as the card field of the power's internal data.
/// </summary>
public static class HeldCards
{
    private static readonly FieldInfo InternalData = typeof(PowerModel).GetField("_internalData", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static CardModel? Get(PowerModel power) => Slot(power) is var (data, field) ? (CardModel?)field.GetValue(data) : null;

    /// <exception cref="InvalidOperationException">The power keeps no card.</exception>
    public static void Set(PowerModel power, CardModel card)
    {
        var (data, field) = Slot(power) ?? throw new InvalidOperationException($"{power.Id.Entry} keeps no card");
        field.SetValue(data, card);
    }

    private static (object Data, FieldInfo Field)? Slot(PowerModel power) =>
        InternalData.GetValue(power) is { } data && data.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType == typeof(CardModel)) is { } field
            ? (data, field)
            : null;
}
