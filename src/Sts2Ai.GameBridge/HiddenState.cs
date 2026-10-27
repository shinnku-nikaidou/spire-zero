using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// What a combat's observation hides that changes how the same plays turn out (<see cref="ICombatWorld.HiddenKey"/>):
/// the draw pile's order, the position of every random stream, and each monster's queued move. The order of this
/// turn's plays is left out on purpose, so the same plays in another order still meet as one state in search.
/// </summary>
public static class HiddenState
{
    public static ulong Key(CombatState st, Player player)
    {
        var key = new StringBuilder();
        foreach (var card in player.PlayerCombatState!.DrawPile.Cards) key.Append(Observe.CardKey(card)).Append('|');
        foreach (var type in Enum.GetValues<RunRngType>()) key.Append(st.RunState.Rng.GetRng(type).ToSerializable().counter).Append(',');
        foreach (var type in Enum.GetValues<PlayerRngType>()) key.Append(player.PlayerRng.GetRng(type).ToSerializable().counter).Append(',');
        foreach (var creature in st.Enemies.Concat(st.Allies))
            if (creature.Monster is { IsMutable: true } monster)
                key.Append(monster.Rng.ToSerializable().counter).Append(':').Append(monster.NextMove?.Id).Append(',');
        return ProtocolJson.Hash(key.ToString());
    }
}
