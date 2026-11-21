using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// A combat answer's line played out turn by turn in the engine, for the mod's details panel (<see cref="FightTable"/>):
/// on a copy of the world the line was planned in, each action in turn; a turn's row is made where the enemies' turn
/// after it has run (the step that ends the turn runs it), or where the combat ends.
/// </summary>
public static class FightTables
{
    /// <summary>The table of <paramref name="line"/> from where <paramref name="world"/> stands, which stays as it is.
    /// The line stops at an action the engine refuses.</summary>
    /// <param name="outlook">Where the line stops with the combat in progress: the combat net's chance to win and HP at a
    /// win there; null for none.</param>
    public static FightTable Of(EngineWorld world, IReadOnlyList<CombatAction> line, Func<EngineWorld, (double Win, double Hp)>? outlook = null)
    {
        using var at = world.Clone();
        var start = at.Observe();
        var enemies = start.Enemies;
        var rows = new List<TurnRow>();
        var (turn, shown, cards) = (start, start, new List<string>());
        int block = 0;
        void Row()
        {
            var left = Left(enemies, shown);
            rows.Add(new(turn.Round, cards, Left(enemies, turn).Sum() - left.Sum(), block, turn.Player.Hp - shown.Player.Hp, shown.Player.Hp, left));
            (turn, cards) = (shown, []);
        }
        foreach (var action in line)
        {
            if (at.Status != CombatStatus.InProgress) break;
            if (action.Kind == CombatActionKind.PlayCard) cards.Add(shown.Hand.FirstOrDefault(c => c.Uid == action.Card) is { } card ? card.Id + (card.Up is > 0 ? "+" : "") : "?");
            block = shown.Player.Block;
            try
            {
                at.Execute(action);
                shown = at.Observe();
            }
            catch (EngineException)
            {
                break;
            }
            if (action.Kind == CombatActionKind.EndTurn || at.Status != CombatStatus.InProgress) Row();
        }
        // A line that stops inside a turn (an action refused, or a plan cut short): the turn so far.
        if (cards.Count > 0)
        {
            block = shown.Player.Block;
            Row();
        }
        var (win, hp) = at.Status == CombatStatus.InProgress && outlook != null ? outlook(at) : (double.NaN, double.NaN);
        return new(start.Player.Hp, [.. enemies.Select(e => new TableEnemy(e.Id, e.Hp, e.MaxHp))], rows, at.Status,
            double.IsNaN(win) ? null : Math.Round(win, 4), double.IsNaN(hp) ? null : Math.Round(hp, 1));
    }

    /// <summary>The HP each of <paramref name="enemies"/> has in <paramref name="obs"/>, 0 once gone.</summary>
    private static int[] Left(IReadOnlyList<CreatureView> enemies, CombatObservation obs) =>
        [.. enemies.Select(e => obs.Enemies.FirstOrDefault(o => o.Uid == e.Uid)?.Hp ?? 0)];

    /// <summary>The seed PIMC draws its k-th world from (search/pimc.rs, world_seed): FNV-1a of "SEED:K".</summary>
    public static ulong WorldSeed(ulong seed, int k)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in $"{seed}:{k}") hash = (hash ^ c) * 1099511628211;
        return hash;
    }
}
