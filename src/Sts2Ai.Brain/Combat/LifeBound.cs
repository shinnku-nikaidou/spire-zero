using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Combat;

/// <summary>
/// The SL planner's one rule written by hand, for speed (the user, 2026-11-18: 「max下引入手寫剪枝規則，如果一個枝葉算下去
/// hp+max hp已經低於目前最好的hp+max hp，就剪掉…這是必要的為了性能上的犧牲」; then 「適用於全部網絡，然後注意藥水，藥水作為
/// hash的key的一部分，得相同的藥水討論才有意義」): a line stops at a state that cannot end the combat with as much HP plus
/// max HP (<see cref="Reach"/>) as a win found with the same potions at its end does (<see cref="Note"/>). Every effort
/// prunes so, the beam (<see cref="SlPlanner"/>) and the solver (<see cref="SlSolver"/>). A line the pick would take for a
/// relic's counter, say, can go so; a proof is a proof under this rule.
/// </summary>
public static class LifeBound
{
    /// <summary>The potions a state holds, as the rule tells states apart: their ids, sorted, comma-separated.</summary>
    public static string Potions(IReadOnlyList<string?> potions) => string.Join(",", potions.OfType<string>().Order(StringComparer.Ordinal));

    /// <summary>A win found raises the bound of the potions it ends with to its HP plus max HP; true if it did.</summary>
    public static bool Note(Dictionary<string, int> bounds, TurnEnd line)
    {
        if (line.Status != CombatStatus.Won || bounds.TryGetValue(line.Potions, out int bound) && bound >= line.Life) return false;
        bounds[line.Potions] = line.Life;
        return true;
    }

    /// <summary>Whether a state holding <paramref name="potions"/> that can end the combat with at most
    /// <paramref name="reach"/> is below the bound of its potions.</summary>
    public static bool Below(IReadOnlyDictionary<string, int>? bounds, string potions, int reach) =>
        bounds != null && bounds.TryGetValue(potions, out int bound) && reach < bound;

    /// <summary>
    /// The most HP plus max HP the player can end the combat with from this state: the HP now, raised by every heal
    /// the state still holds but never past the max HP, plus the max HP, raised by every gain it holds;
    /// <see cref="int.MaxValue"/> where a gain has no amount to count. These are every heal and gain the game has on the
    /// player in a fight or at its end (v0.113.1, each <c>CreatureCmd.Heal</c> and <c>GainMaxHp</c>; the user,
    /// 2026-11-18: 「都記上」, 「蜥蜴尾巴就加50%」, 「再生確實需要加上（雖然也是藥），但藥用了之後還是會加血，其他的也一樣」):
    /// <list type="bullet">
    /// <item>after a win: Burning Blood heals 6, Black Blood 12, Meat on the Bone 12 (at half HP or less); Chosen Cheese
    /// gives 1 max HP;</item>
    /// <item>Lizard Tail, unused, heals 50 % of max HP on dying;</item>
    /// <item>potions held: Blood Potion heals 20 % of max HP, Ambergris 50 %, Fairy in a Bottle 30 % (on dying), Regen
    /// Potion 15 (Regen 5, one less each turn); Fruit Juice gives 5 max HP; Entropic Brew fills the belt with random
    /// potions, each of which can be a Blood Potion;</item>
    /// <item>Regen on the player heals its amount, one less each turn;</item>
    /// <item>cards in any pile, each as often as it plays (once, and its own Replay): Not Yet heals 10 (13 upgraded),
    /// Feed gives 3 max HP (4 upgraded) on a kill, Alchemize makes a random potion. One that does not exhaust plays
    /// again and again (the user: 「如果这张卡没有消耗…这条规则直接不适用」; Souls Power, from the Grave of the
    /// Forgotten, takes Exhaust off a card): no bound;</item>
    /// <item>no amount to count, so no bound: Book Repair Knife (3 a kill by Doom), Book of Five Rings (20 each fifth
    /// card added to the deck), Dragon Fruit (1 max HP a gain of gold), Darkstone Periapt (6 max HP a curse added to
    /// the deck).</item>
    /// </list>
    /// Before combat (Blood Vial, Bath Towel, Pantograph and the like) it has healed already. Demon Tongue heals back
    /// what the player's own turn took, never past the HP before. Other effects that play a card again (Burst, Echo Form,
    /// Duplication and the like) are not counted.
    /// </summary>
    public static int Reach(CombatObservation obs)
    {
        var relics = obs.Relics.Where(r => r.Status != "Disabled").Select(r => r.Id).ToList();
        if (relics.Any(r => r is "BOOK_REPAIR_KNIFE" or "BOOK_OF_FIVE_RINGS" or "DRAGON_FRUIT" or "DARKSTONE_PERIAPT")) return int.MaxValue;
        var potions = obs.Potions.OfType<string>().ToList();
        long maxHp = obs.Player.MaxHp + relics.Count(r => r == "CHOSEN_CHEESE") + 5L * potions.Count(p => p == "FRUIT_JUICE");
        // Heals by a share of the max HP, counted at the end's, the most it can be.
        var shares = new List<int>();
        long heal = 0;
        foreach (var card in obs.Hand.Concat(obs.Draw).Concat(obs.Discard).Concat(obs.Exhaust))
        {
            if (card.Id is not ("NOT_YET" or "FEED" or "ALCHEMIZE")) continue;
            if (card.Keywords?.Contains("Exhaust") != true) return int.MaxValue;
            int plays = 1 + (card.Replay ?? 0);
            bool up = card.Up > 0;
            switch (card.Id)
            {
                case "NOT_YET":
                    heal += plays * (long)(card.Vars?.GetValueOrDefault("Heal") is > 0 and var h ? h : up ? 13 : 10);
                    break;
                case "FEED":
                    maxHp += plays * (long)(card.Vars?.GetValueOrDefault("MaxHp") is > 0 and var m ? m : up ? 4 : 3);
                    break;
                default:
                    shares.AddRange(Enumerable.Repeat(20, plays));
                    break;
            }
        }
        foreach (var relic in relics)
            switch (relic)
            {
                case "BURNING_BLOOD":
                    heal += 6;
                    break;
                case "BLACK_BLOOD" or "MEAT_ON_THE_BONE":
                    heal += 12;
                    break;
                case "LIZARD_TAIL":
                    shares.Add(50);
                    break;
            }
        foreach (var potion in potions)
            switch (potion)
            {
                case "BLOOD_POTION":
                    shares.Add(20);
                    break;
                case "AMBERGRIS":
                    shares.Add(50);
                    break;
                case "FAIRY_IN_A_BOTTLE":
                    shares.Add(30);
                    break;
                case "REGEN_POTION":
                    heal += 15;
                    break;
                case "ENTROPIC_BREW":
                    shares.AddRange(Enumerable.Repeat(20, obs.Potions.Count));
                    break;
            }
        foreach (var power in obs.Player.Powers)
            if (power.Id == "REGEN_POWER" && power.Amount > 0) heal += (long)power.Amount * (power.Amount + 1) / 2;
        heal += shares.Sum(share => (maxHp * share + 99) / 100);
        return (int)Math.Min(int.MaxValue - 1, Math.Min(maxHp, obs.Player.Hp + heal) + maxHp);
    }
}
