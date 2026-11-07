using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

public enum CombatActionKind { PlayCard, UsePotion, EndTurn }

/// <summary>
/// One player action in a combat. It names what it acts on by uid (see <see cref="CombatObservation"/>), not by
/// position, so it stays valid while the hand changes and means the same thing in the sim and in the real game.
/// Text form, also used in JSON: "play:CARD[:TARGET][@PICK]", "potion:SLOT[:TARGET][@PICK]", "end_turn[@PICK]".
/// <see cref="Pick"/> answers the first card prompt the action raises (a card's, a potion's, or one the turn's end or the
/// next turn's start brings), as <see cref="Answer"/> writes it; without it <see cref="ICombatWorld"/> and the run ask
/// their prompt policy. Only search sets it, and a host never sees it: the game is told <see cref="Plain"/>, and Harness
/// answers the prompt when it comes (docs: combat-brain/shared §1).
/// </summary>
[JsonConverter(typeof(CombatActionJsonConverter))]
public readonly record struct CombatAction(CombatActionKind Kind, uint? Card = null, int? PotionSlot = null, uint? Target = null, string? Pick = null)
{
    /// <summary>A prompt's answer as <see cref="Pick"/> holds it: the picked options' places, in order, comma-separated; empty for none.</summary>
    public static string Answer(IEnumerable<int> picks) => string.Join(",", picks.Order());

    /// <summary>The options <see cref="Pick"/> picks, if it is set.</summary>
    public IReadOnlyList<int>? Picks => Pick switch
    {
        null => null,
        "" => [],
        var pick => [.. pick.Split(',').Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture))],
    };

    public static CombatAction Play(uint card, uint? target = null) => new(CombatActionKind.PlayCard, Card: card, Target: target);
    public static CombatAction Potion(int slot, uint? target = null) => new(CombatActionKind.UsePotion, PotionSlot: slot, Target: target);
    public static CombatAction EndTurn => new(CombatActionKind.EndTurn);

    /// <summary>The action as the game knows it: legal or not, without what search chose for its prompt.</summary>
    public CombatAction Plain => this with { Pick = null };

    public override string ToString() => Kind switch
    {
        CombatActionKind.PlayCard => $"play:{Card}{TargetSuffix}{PickSuffix}",
        CombatActionKind.UsePotion => $"potion:{PotionSlot}{TargetSuffix}{PickSuffix}",
        _ => $"end_turn{PickSuffix}",
    };

    private string TargetSuffix => Target is uint t ? $":{t}" : "";

    private string PickSuffix => Pick is { } p ? $"@{p}" : "";

    public static CombatAction Parse(string text) =>
        TryParse(text, out var action) ? action : throw new FormatException($"not a combat action: {text}");

    public static bool TryParse(string text, out CombatAction action)
    {
        action = default;
        string? pick = null;
        int at = text.IndexOf('@');
        if (at >= 0)
        {
            pick = text[(at + 1)..];
            if (pick != "" && !pick.Split(',').All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
            text = text[..at];
        }
        var parts = text.Split(':');
        if (parts is ["end_turn"])
        {
            action = EndTurn with { Pick = pick };
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
                action = Play(card, target) with { Pick = pick };
                return true;
            case "potion" when int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var slot):
                action = Potion(slot, target) with { Pick = pick };
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
