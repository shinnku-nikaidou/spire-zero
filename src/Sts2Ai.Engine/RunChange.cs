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

/// <summary>
/// The changes <c>sz_world_run</c> writes for the worlds of one import, read with the deck and the relics kept: a
/// search asks for the run at every end of a turn, and from end to end little but the HP differs. The text is
/// <see cref="RunChange"/>'s in <see cref="ProtocolJson"/>'s form, its members in their order, so the deck and the
/// relics are one stretch of it, and that stretch read once gives the same lists every time it comes again. Asked
/// from the threads of every world of the import.
/// </summary>
internal sealed class RunChanges
{
    private sealed record Lists(IReadOnlyList<CardView> Deck, IReadOnlyList<RelicView> Relics);

    private sealed record Rest(int Hp, int MaxHp, int Gold, IReadOnlyList<string?> Potions);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lists> _lists = new();

    /// <summary><paramref name="run"/> as the combat has left it, from the change's text.</summary>
    public RunView Over(RunView run, ReadOnlySpan<byte> change)
    {
        int deck = change.IndexOf(",\"deck\":"u8), potions = change.LastIndexOf(",\"potions\":"u8);
        if (deck < 0 || potions < deck) throw new InvalidDataException("sz_world_run: not a run change");
        // {"hp":..,"max_hp":..,"gold":.. | ,"deck":[..],"relics":[..] | ,"potions":[..]}
        var lists = _lists.GetOrAdd(System.Text.Encoding.UTF8.GetString(change[(deck + 1)..potions]),
            text => ProtocolJson.Deserialize<Lists>("{" + text + "}"));
        var rest = ProtocolJson.Deserialize<Rest>((byte[])[.. change[..deck], .. change[potions..]]);
        return run with { Hp = rest.Hp, MaxHp = rest.MaxHp, Gold = rest.Gold, Deck = lists.Deck, Relics = lists.Relics, Potions = rest.Potions };
    }
}
