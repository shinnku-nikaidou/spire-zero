using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// Keeps playing random cards, uses a potion now and then, and ends the turn only when nothing else is left.
/// A placeholder that wins most easy combats, for benchmarks and tests until search exists.
/// </summary>
public sealed class PlayPolicy(Random rng) : ICombatPolicy
{
    public CombatAction Choose(CombatDecision decision)
    {
        var plays = decision.Legal.Where(a => a.Kind == CombatActionKind.PlayCard).ToList();
        if (plays.Count > 0) return plays[rng.Next(plays.Count)];
        var potions = decision.Legal.Where(a => a.Kind == CombatActionKind.UsePotion).ToList();
        if (potions.Count > 0 && rng.Next(4) == 0) return potions[rng.Next(potions.Count)];
        return CombatAction.EndTurn;
    }
}

/// <summary>Uniformly random legal selection.</summary>
public sealed class RandomChoices(Random rng) : IChoicePolicy
{
    public IReadOnlyList<int> Choose(ChoiceDecision decision)
    {
        var choice = decision.Choice;
        int count = choice.Min == choice.Max ? choice.Min : rng.Next(choice.Min, choice.Max + 1);
        return Enumerable.Range(0, choice.Options.Count).OrderBy(_ => rng.Next()).Take(count).ToList();
    }
}

/// <summary>Uniform over the legal actions: a placeholder that reaches every surface, not a strategy.</summary>
public sealed class RandomMacro(Random rng) : IMacroPolicy
{
    public string Choose(MacroDecision decision) => decision.Legal[rng.Next(decision.Legal.Count)];
}

public static class Policies
{
    public static readonly string[] CombatNames = ["play"];

    public static ICombatPolicy Combat(string name, Random rng) => name switch
    {
        "play" => new PlayPolicy(rng),
        _ => throw new ArgumentException($"unknown policy {name} (one of {string.Join(", ", CombatNames)})"),
    };
}
