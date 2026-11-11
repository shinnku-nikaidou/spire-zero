using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

public enum CombatActionKind { PlayCard, UsePotion, EndTurn, Pick }

/// <summary>
/// One player action in a combat. It names what it acts on by uid (see <see cref="CombatObservation"/>), not by
/// position, so it stays valid while the hand changes and means the same thing in the sim and in the real game.
/// Text form, also used in JSON: "play:CARD[:TARGET]", "potion:SLOT[:TARGET]", "end_turn", and "pick:I,J" (or "pick:"
/// for none), the answer to the card prompt the combat waits on (<see cref="CombatObservation.Prompt"/>): the picked
/// options' places in the prompt's canonical order, as <see cref="MacroAction.Pick"/> writes one outside combat.
/// </summary>
[JsonConverter(typeof(CombatActionJsonConverter))]
public readonly record struct CombatAction(CombatActionKind Kind, uint? Card = null, int? PotionSlot = null, uint? Target = null, string? Answer = null)
{
    public static CombatAction Play(uint card, uint? target = null) => new(CombatActionKind.PlayCard, Card: card, Target: target);
    public static CombatAction Potion(int slot, uint? target = null) => new(CombatActionKind.UsePotion, PotionSlot: slot, Target: target);
    public static CombatAction EndTurn => new(CombatActionKind.EndTurn);

    /// <summary>The answer that picks <paramref name="picks"/>, kept in order: one answer has one form.</summary>
    public static CombatAction Pick(IEnumerable<int> picks) => new(CombatActionKind.Pick, Answer: string.Join(",", picks.Order()));

    /// <summary>The options a <see cref="CombatActionKind.Pick"/> picks.</summary>
    public IReadOnlyList<int> Picks => Answer is null or "" ? [] : [.. Answer.Split(',').Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture))];

    public override string ToString() => Kind switch
    {
        CombatActionKind.PlayCard => $"play:{Card}{TargetSuffix}",
        CombatActionKind.UsePotion => $"potion:{PotionSlot}{TargetSuffix}",
        CombatActionKind.Pick => $"pick:{Answer}",
        _ => "end_turn",
    };

    private string TargetSuffix => Target is uint t ? $":{t}" : "";

    public static CombatAction Parse(string text) =>
        TryParse(text, out var action) ? action : throw new FormatException($"not a combat action: {text}");

    public static bool TryParse(string text, out CombatAction action)
    {
        action = default;
        var parts = text.Split(':');
        if (parts is ["end_turn"])
        {
            action = EndTurn;
            return true;
        }
        if (parts is ["pick", var answer])
        {
            var picks = new List<int>();
            foreach (var p in answer == "" ? [] : answer.Split(','))
            {
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return false;
                picks.Add(i);
            }
            action = Pick(picks);
            return true;
        }
        if (parts.Length is < 2 or > 3) return false;
        uint? target = null;
        if (parts.Length == 3)
        {
            if (!uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) return false;
            target = t;
        }
        switch (parts[0])
        {
            case "play" when uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var card):
                action = Play(card, target);
                return true;
            case "potion" when int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var slot):
                action = Potion(slot, target);
                return true;
            default:
                return false;
        }
    }
}

public sealed class CombatActionJsonConverter : JsonConverter<CombatAction>
{
    public override CombatAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CombatAction.TryParse(reader.GetString() ?? "", out var action) ? action : throw new JsonException($"not a combat action: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, CombatAction value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
