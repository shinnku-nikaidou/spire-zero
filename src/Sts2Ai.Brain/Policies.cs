using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// What a combat policy gets at each decision: the legal actions, and the observation on request. In a run, the run
/// observation around the combat too (<paramref name="inRun"/> makes it from the combat's); a combat started on its
/// own has none.
/// </summary>
public sealed class CombatDecision(IReadOnlyList<CombatAction> legal, Func<CombatObservation> observe, Func<CombatObservation, Observation>? inRun = null)
{
    private CombatObservation? _observation;
    private Observation? _runObservation;

    public IReadOnlyList<CombatAction> Legal { get; } = legal;

    /// <summary>Built on first use: a policy that ignores it shouldn't pay for it.</summary>
    public CombatObservation Observation => _observation ??= observe();

    /// <summary>Built on first use, like <see cref="Observation"/>.</summary>
    public Observation? RunObservation => _runObservation ??= inRun?.Invoke(Observation);
}

/// <summary>
/// What a macro policy gets at each out-of-combat decision: the surface, its legal actions in text form ("map:3,5",
/// "reward:0", "leave", ...; grammar in docs: protocol §3), and the observation on request.
/// </summary>
public sealed class MacroDecision(string surface, IReadOnlyList<string> legal, Func<Observation> observe)
{
    private Observation? _observation;

    public string Surface { get; } = surface;
    public IReadOnlyList<string> Legal { get; } = legal;

    /// <summary>Built on first use, like <see cref="CombatDecision.Observation"/>.</summary>
    public Observation Observation => _observation ??= observe();
}

/// <summary>
/// A card prompt for a choice policy, and the observation it is asked in (docs: protocol §3, surface card_choice) on
/// request. The observation is null where there is no run around the prompt (a combat started on its own).
/// </summary>
public sealed class ChoiceDecision(CardChoice choice, Func<Observation>? observe = null)
{
    private Observation? _observation;

    public CardChoice Choice { get; } = choice;

    /// <summary>Built on first use, like <see cref="CombatDecision.Observation"/>.</summary>
    public Observation? Observation => _observation ??= observe?.Invoke();
}

/// <summary>Picks the next action of a combat.</summary>
public interface ICombatPolicy
{
    CombatAction Choose(CombatDecision decision);
}

/// <summary>Answers the card-choice prompts that come up while a card or power resolves.</summary>
public interface IChoicePolicy
{
    /// <summary>Indexes into <see cref="CardChoice.Options"/>; must satisfy <see cref="CardChoice.Accepts"/>.</summary>
    IReadOnlyList<int> Choose(ChoiceDecision decision);
}

/// <summary>Picks an out-of-combat action (map, rewards, events, shop, rest, treasure, card rewards, bundles).</summary>
public interface IMacroPolicy
{
    /// <summary>One of <see cref="MacroDecision.Legal"/>.</summary>
    string Choose(MacroDecision decision);
}

/// <summary>Uniform over the legal actions. Ends turns early a lot.</summary>
public sealed class RandomPolicy(Random rng) : ICombatPolicy
{
    public CombatAction Choose(CombatDecision decision) => decision.Legal[rng.Next(decision.Legal.Count)];
}

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
    public static readonly string[] CombatNames = ["play", "random"];

    public static ICombatPolicy Combat(string name, Random rng) => name switch
    {
        "play" => new PlayPolicy(rng),
        "random" => new RandomPolicy(rng),
        _ => throw new ArgumentException($"unknown policy {name} (one of {string.Join(", ", CombatNames)})"),
    };
}
