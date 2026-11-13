using Sts2Ai.Protocol;

namespace Sts2Ai.Engine;

/// <summary>
/// What a combat changes of the run around it: the members of <see cref="RunView"/> of these names. It is what
/// <c>sz_world_run</c> writes, in <see cref="ProtocolJson"/>'s form: the deck in the order of its cards' keys, each
/// card with no upgrade preview, as a combat's leaf reads a run.
/// </summary>
public sealed record RunChange(int Hp, int MaxHp, int Gold, IReadOnlyList<CardView> Deck, IReadOnlyList<RelicView> Relics, IReadOnlyList<string?> Potions)
{
    public static RunChange Of(RunView run) => new(run.Hp, run.MaxHp, run.Gold, run.Deck, run.Relics, run.Potions);

    /// <summary><paramref name="run"/> as the combat has left it.</summary>
    public RunView Over(RunView run) => run with { Hp = Hp, MaxHp = MaxHp, Gold = Gold, Deck = Deck, Relics = Relics, Potions = Potions };
}
