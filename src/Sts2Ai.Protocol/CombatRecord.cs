namespace Sts2Ai.Protocol;

/// <summary>
/// What one enemy of the current combat showed at the start of each round so far, oldest first: the public record of
/// its moves (docs: harness §4). A player who watched the fight saw all of it; the learned combat parts read it.
/// </summary>
public sealed record EnemyTurns(uint Uid, string? Id, IReadOnlyList<EnemyTurn> Turns);

/// <summary>An enemy's intents as the first decision of a round showed them.</summary>
public sealed record EnemyTurn(int Round, IReadOnlyList<IntentView> Intents);
