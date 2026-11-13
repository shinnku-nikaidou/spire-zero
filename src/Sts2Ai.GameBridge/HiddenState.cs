using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// What a combat's observation hides that changes how the same plays turn out (<see cref="ICombatWorld.HiddenKey"/>):
/// the draw pile's order, the position of every random stream, and each monster's queued move. The order of this
/// turn's plays is left out on purpose, so the same plays in another order still meet as one state in search.
/// <see cref="Full"/> adds what else the game keeps, for the SL world. Each key is the hash of a text, which another
/// world that must tell the same states apart (the engine's) can be held against.
/// </summary>
public static class HiddenState
{
    public static ulong Key(CombatState st, Player player) => ProtocolJson.Hash(KeyText(st, player));

    public static string KeyText(CombatState st, Player player)
    {
        var key = new StringBuilder();
        foreach (var card in player.PlayerCombatState!.DrawPile.Cards) key.Append(Observe.CardKey(card)).Append('|');
        foreach (var type in Enum.GetValues<RunRngType>()) key.Append(st.RunState.Rng.GetRng(type).ToSerializable().counter).Append(',');
        foreach (var type in Enum.GetValues<PlayerRngType>()) key.Append(player.PlayerRng.GetRng(type).ToSerializable().counter).Append(',');
        foreach (var creature in st.Enemies.Concat(st.Allies))
            if (creature.Monster is { IsMutable: true } monster)
                key.Append(monster.Rng.ToSerializable().counter).Append(':').Append(monster.NextMove?.Id).Append(',');
        return key.ToString();
    }

    /// <summary>
    /// The SL world's key: the transpositions of its one exact combat must be the same state, so
    /// besides <see cref="Key"/> it holds each monster's moves so far and what the combat's history holds of this turn.
    /// The turn's entries go in as a multiset, so the same plays in another order still meet.
    /// </summary>
    public static ulong Full(CombatState st, Player player) => ProtocolJson.Hash(FullText(st, player));

    public static string FullText(CombatState st, Player player)
    {
        var key = new StringBuilder().Append(Key(st, player)).Append('#');
        foreach (var creature in st.Enemies.Concat(st.Allies))
            if (creature.Monster is { IsMutable: true, MoveStateMachine: { } machine })
                key.AppendJoin(',', machine.StateLog.Select(m => m.Id)).Append('|');
        key.AppendJoin(';', CombatManager.Instance.History.Entries.Where(e => e.HappenedThisTurn(st)).Select(Entry).Order(StringComparer.Ordinal));
        return key.ToString();
    }

    /// <summary>A history entry as its kind, who acted, and what it acted with. Not the game's own description, which
    /// some entries cannot give.</summary>
    private static string Entry(CombatHistoryEntry e) => $"{e.GetType().Name}:{e.Actor?.CombatId}:" + e switch
    {
        CardPlayStartedEntry x => $"{x.CardPlay.Card.Id.Entry}>{x.CardPlay.Target?.CombatId}",
        CardPlayFinishedEntry x => $"{x.CardPlay.Card.Id.Entry}>{x.CardPlay.Target?.CombatId}",
        CardDrawnEntry x => x.Card.Id.Entry,
        CardDiscardedEntry x => x.Card.Id.Entry,
        CardExhaustedEntry x => x.Card.Id.Entry,
        CardGeneratedEntry x => x.Card.Id.Entry,
        CardAfflictedEntry x => x.Card.Id.Entry,
        PotionUsedEntry x => x.Potion.Id.Entry,
        PowerReceivedEntry x => $"{x.Power.Id.Entry}{x.Amount}",
        BlockGainedEntry x => $"{x.Amount}",
        EnergySpentEntry x => $"{x.Amount}",
        _ => "",
    };
}
