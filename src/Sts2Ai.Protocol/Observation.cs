namespace Sts2Ai.Protocol;

/// <summary>
/// What the player sees at a decision: the run around them and the screen that asks. Like
/// <see cref="CombatObservation"/>, every field is whitelisted from the UI; hidden information never appears (seeds,
/// RNG state, what an unvisited "?" holds, what a reroll or a transform will give).
/// </summary>
public sealed record Observation
{
    public required RunView Run { get; init; }
    public required ScreenView Screen { get; init; }
}

/// <summary>The run as the top bar, the deck view and the map show it.</summary>
public sealed record RunView
{
    public required string Character { get; init; }
    public required int Ascension { get; init; }
    /// <summary>Act index, from 0.</summary>
    public required int Act { get; init; }
    /// <summary>The act's id: which of the act's variants this run rolled.</summary>
    public required string ActId { get; init; }
    /// <summary>Floor counted over the whole run, as the top bar shows it.</summary>
    public required int Floor { get; init; }
    public required int Hp { get; init; }
    public required int MaxHp { get; init; }
    public required int Gold { get; init; }
    /// <summary>Sorted by card key, so that the same deck reached in a different order looks the same.</summary>
    public required IReadOnlyList<CardView> Deck { get; init; }
    public required IReadOnlyList<RelicView> Relics { get; init; }
    /// <summary>One entry per potion slot: the potion's id, or null for an empty slot.</summary>
    public required IReadOnlyList<string?> Potions { get; init; }
    public required MapView Map { get; init; }
    /// <summary>
    /// The game's rule, not the screen's: the acts (indexes from 0, as <see cref="Act"/>) whose plain monster fights, an
    /// event's included, are won at their start with their rewards, so that only elites and bosses are fought there
    /// (docs: simulator/runs). Null in the normal game: always in the real game, set only by the simulator.
    /// </summary>
    public IReadOnlyList<int>? AutoWin { get; init; }
}

/// <summary>The current act's map.</summary>
public sealed record MapView
{
    /// <summary>Every point, sorted by (row, col): the start (row 0), the rows, then the boss(es) above the last row.</summary>
    public required IReadOnlyList<MapPointView> Points { get; init; }
    /// <summary>The points visited in this act, in order; the last one is where the player is.</summary>
    public required IReadOnlyList<Coord> Visited { get; init; }
    /// <summary>True in a map move's afterstate only: the last visited point is being entered, not yet played.</summary>
    public bool? Entering { get; init; }
    /// <summary>The boss encounter, shown on the boss point.</summary>
    public required string Boss { get; init; }
    public string? SecondBoss { get; init; }
    /// <summary>The act's ancient, shown on the start point.</summary>
    public string? Ancient { get; init; }
}

public sealed record MapPointView
{
    public required int Col { get; init; }
    public required int Row { get; init; }
    /// <summary>The map icon: Monster, Elite, Unknown ("?", even once visited), Shop, Treasure, RestSite, Boss, Ancient.</summary>
    public required string Type { get; init; }
    /// <summary>The points a path leads to from here, sorted by (row, col).</summary>
    public required IReadOnlyList<Coord> Next { get; init; }
    /// <summary>Marked by a quest (a relic or card that rewards reaching it).</summary>
    public bool? Quest { get; init; }
}

public sealed record Coord(int Col, int Row);
