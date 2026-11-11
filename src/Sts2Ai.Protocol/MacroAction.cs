using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>
/// An action outside combat (docs: protocol/observation §5): a verb and, for most, an argument. Its text form, "verb" or
/// "verb:argument" ("map:3,5", "reward:0", "leave"), is what the wire, the node's trace and the decision log hold; in
/// code it is this type, read once by <see cref="Parse"/>. A card prompt's answer is one too, "pick:i,j"
/// (<see cref="Pick"/>).
/// </summary>
[JsonConverter(typeof(MacroActionJson))]
public readonly record struct MacroAction(string Verb, string Arg = "")
{
    // The verbs, by the screen that offers them.
    public const string Map = "map";
    public const string Potion = "potion";
    public const string DiscardPotion = "discard_potion";
    public const string Reward = "reward";
    public const string Card = "card";
    public const string CardAlt = "card_alt";
    public const string Bundle = "bundle";
    public const string Event = "event";
    public const string Buy = "buy";
    public const string Rest = "rest";
    public const string Relic = "relic";
    public const string Big = "big";
    public const string Small = "small";
    public const string PickVerb = "pick";

    /// <summary>Closes the rewards screen, taking nothing more.</summary>
    public static readonly MacroAction RewardsDone = new("rewards_done");

    /// <summary>Leaves an event for the map.</summary>
    public static readonly MacroAction Proceed = new("proceed");

    /// <summary>Leaves a shop or a rest site.</summary>
    public static readonly MacroAction Leave = new("leave");

    /// <summary>The shop's card removal service.</summary>
    public static readonly MacroAction Remove = new("remove");

    /// <summary>Leaves an opened chest's relics.</summary>
    public static readonly MacroAction RelicSkip = new("relic_skip");

    /// <summary>The action whose argument is an index: "reward:2", "buy:0", "potion:1".</summary>
    public static MacroAction Of(string verb, int index) => new(verb, index.ToString(CultureInfo.InvariantCulture));

    /// <summary>The action whose argument is a column and a row: "map:3,5", "big:1,2".</summary>
    public static MacroAction Of(string verb, int x, int y) => new(verb, string.Create(CultureInfo.InvariantCulture, $"{x},{y}"));

    /// <summary>A card prompt's answer, its picks in the order given, which is the order they are taken in: "pick:4,1";
    /// no pick is "pick:".</summary>
    public static MacroAction Pick(IEnumerable<int> picks) => new(PickVerb, string.Join(",", picks));

    /// <summary>The text form read back; text without a colon is a verb alone.</summary>
    public static MacroAction Parse(string text)
    {
        int colon = text.IndexOf(':');
        return colon < 0 ? new(text) : new(text[..colon], text[(colon + 1)..]);
    }

    /// <summary>The argument as an index.</summary>
    /// <exception cref="FormatException">The argument is not one.</exception>
    public int Index => int.TryParse(Arg, NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : throw new FormatException($"{this}: not an index");

    /// <summary>The argument as a column and a row ("map:3,5": column 3, row 5).</summary>
    /// <exception cref="FormatException">The argument is not two numbers.</exception>
    public Coord Coord => Arg.Split(',') is [var x, var y] && int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out int col)
        && int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out int row)
        ? new(col, row)
        : throw new FormatException($"{this}: not a column and a row");

    /// <summary>The argument as a card prompt's picks; none for an empty one.</summary>
    public IReadOnlyList<int> Picks => [.. Arg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture))];

    /// <summary>The text form. A card prompt's answer keeps its colon with no pick ("pick:"), as it always has.</summary>
    public override string ToString() => Arg.Length == 0 && Verb != PickVerb ? Verb : $"{Verb}:{Arg}";
}

/// <summary>A macro action in JSON is its text form.</summary>
public sealed class MacroActionJson : JsonConverter<MacroAction>
{
    public override MacroAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MacroAction.Parse(reader.GetString() ?? throw new JsonException("a macro action is a string"));

    public override void Write(Utf8JsonWriter writer, MacroAction value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
