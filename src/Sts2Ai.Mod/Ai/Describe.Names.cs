using System.Text.RegularExpressions;

namespace Sts2Ai.Mod;

internal static partial class Describe
{
    /// <summary>A line of the simulator's (the scout's) with each game id in it named in the game's language: an event
    /// option by its title (an ancient's by the relic it gives), any other id by the first table that has it.</summary>
    public static string Names(string line)
    {
        line = OptionKey().Replace(line, m => Text("events", m.Value + ".title") ?? Named(m.Value[(m.Value.LastIndexOf('.') + 1)..]) ?? m.Value);
        return GameId().Replace(line, m =>
        {
            bool plus = m.Value.EndsWith('+');
            string id = plus ? m.Value[..^1] : m.Value;
            return (Named(id) ?? id) + (plus ? "+" : "");
        });
    }

    private static string? Named(string id) =>
        Text("cards", id + ".title") ?? Text("relics", id + ".title") ?? Text("potions", id + ".title") ?? Text("events", id + ".title")
        ?? Text("encounters", id + ".title") ?? Text("monsters", id + ".name") ?? Text("acts", id + ".title");

    [GeneratedRegex(@"\b[A-Z_]+\.pages\.[A-Z_0-9]+\.options\.[A-Z_0-9]+")]
    private static partial Regex OptionKey();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\+?|\b[A-Z]{3,}\+?(?![a-z])")]
    private static partial Regex GameId();
}
