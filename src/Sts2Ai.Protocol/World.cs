namespace Sts2Ai.Protocol;

/// <summary>What the AI may know of outcomes the game has not shown yet (docs: environments §2).</summary>
public enum PlayEnvironment { NoSL, SL }

public enum CombatStatus { InProgress, Won, Lost }

/// <summary>
/// A simulated combat that search can act in (docs: protocol/worlds §1). Search never touches a game directly: it gets
/// worlds from an <see cref="IWorldSource"/>. A NoSL world is one sample of the hidden state that agrees with the
/// observation; an SL world is an exact copy of the real combat.
/// </summary>
public interface ICombatWorld
{
    CombatObservation Observe();
    /// <summary>
    /// The run around the combat as it stands in this world, as V reads it (docs: combat-brain/leaf §2): the deck with what
    /// the combat has changed for good (a card grown, one stolen), the relics, the potions, HP, max HP and gold.
    /// </summary>
    RunView Run();
    /// <summary>Empty once the combat is over.</summary>
    IReadOnlyList<CombatAction> Legal();
    CombatStatus Status { get; }
    /// <summary>
    /// What <see cref="Observe"/> hides that changes how the same plays turn out from here (the draw pile's order, the
    /// position of each random stream, a monster's queued move), as a hash. Together with the observation it tells
    /// states apart for search, within this world.
    /// </summary>
    ulong HiddenKey();
    /// <summary>Card prompts raised by the action are answered inside the world (docs: combat-brain/shared §1), the first
    /// by <see cref="CombatAction.Pick"/> if it has one.</summary>
    void Execute(CombatAction action);
    /// <summary>The first card prompt the last <see cref="Execute"/> raised, if any: search tries its other answers.</summary>
    PromptSeen? LastPrompt => null;
    WorldSnapshot Snapshot();
    /// <summary>Goes back to a snapshot taken from this same world.</summary>
    void Restore(WorldSnapshot snapshot);
}

/// <summary>A card prompt as a world met it: the first one an action raised.</summary>
/// <param name="Groups">Its options grouped by what the player can tell apart (<see cref="CardChoice.Groups"/>).</param>
/// <param name="Min">The fewest options it takes.</param>
/// <param name="Max">The most options it takes.</param>
/// <param name="Answer">The answer it was given (<see cref="CombatAction.Answer"/>).</param>
public sealed record PromptSeen(IReadOnlyList<IReadOnlyList<int>> Groups, int Min, int Max, string Answer)
{
    public PromptSeen(CardChoice choice, IReadOnlyList<int> answer) : this(choice.Groups(), choice.Min, choice.Max, CombatAction.Answer(answer)) { }

    /// <summary>
    /// Every other answer the prompt takes, once per choice of cards the player can tell apart (k cards of a group are its
    /// first k), the fewest picks first, then in the options' order (the cards of a group are next to each other).
    /// </summary>
    public IEnumerable<string> Others()
    {
        int options = Groups.Sum(g => g.Count);
        for (int size = Min; size <= Math.Min(Max, options); size++)
            foreach (var picks in Choose(Groups, 0, size))
                if (CombatAction.Answer(picks) is var answer && answer != Answer) yield return answer;
    }

    /// <summary>Every choice of <paramref name="size"/> cards from the groups at <paramref name="from"/> and after.</summary>
    private static IEnumerable<List<int>> Choose(IReadOnlyList<IReadOnlyList<int>> groups, int from, int size)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }
        for (int g = from; g < groups.Count; g++)
            for (int k = Math.Min(size, groups[g].Count); k >= 1; k--)
                foreach (var rest in Choose(groups, g + 1, size - k))
                    yield return [.. groups[g].Take(k), .. rest];
    }
}

/// <summary>Hands out the worlds for one decision.</summary>
public interface IWorldSource
{
    PlayEnvironment Environment { get; }
    /// <summary>NoSL: <paramref name="k"/> samples drawn from <paramref name="searchSeed"/>. SL: the one exact copy.</summary>
    IReadOnlyList<ICombatWorld> Worlds(int k, ulong searchSeed);
}

/// <summary>
/// A point in a world, as the steps taken since the world began, in their text form. Opaque to search: only the world
/// that made it reads it, and restores it by replay.
/// </summary>
public sealed record WorldSnapshot(IReadOnlyList<string> Steps);

/// <summary>
/// An SL clone of the current node (docs: environments/information §7): the node's checkpoint at its entry (the game's save and the
/// relic fields it drops), and every decision since, as "surface action" (picks of a card prompt as the game's own
/// indexes). Opaque outside the simulator, which loads the save as the game's Continue does, puts the relic fields back
/// and replays the decisions. <see cref="AutoWin"/> is the run's rule (<see cref="RunView.AutoWin"/>), which the clone
/// keeps: null in the normal game.
/// </summary>
public sealed record NodeClone(string Save, IReadOnlyList<string> Trace, IReadOnlyList<int>? AutoWin = null);

/// <summary>An action that search scored: its mean value over the worlds, the 95% confidence half-width, and how many worlds.
/// On a macro decision or a card prompt outside combat the value is V's, the chance to win the run.</summary>
/// <param name="Values">Its value in each world, in world order, to 4 decimals: the Q that ExIt's targets are made of
/// (docs: learning/loop §5).</param>
/// <param name="Win">The SL planner's: the chance to win the combat on the action's best line, 1 or 0 where the line
/// reaches the combat's end, else the combat net's.</param>
/// <param name="Hp">With <paramref name="Win"/>: the HP held at the end of a win on that line, expected where it is the net's.</param>
/// <param name="Plan">On the map, the route to the boss after the action, as the macro brain would take it.</param>
public sealed record Candidate(string Action, double Value, double Ci, int Samples, IReadOnlyList<double>? Values = null,
    double? Win = null, double? Hp = null, IReadOnlyList<string>? Plan = null);
