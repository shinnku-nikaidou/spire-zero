using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Ai.SimWorker.Runtime.Patches;

internal static partial class HeadlessPatches
{
    // ---- speed: same results, fewer allocations on the hottest paths ----

    /// <summary>
    /// Combat history, hook contexts and targeting read <c>CombatState.Players</c> many times per action, and the
    /// game builds it through three LINQ lists (Creatures, then PlayerCreatures, then Players). These loops return
    /// the same fresh snapshot list in the same order.
    /// </summary>
    [HarmonyPatch(typeof(CombatState), nameof(CombatState.Creatures), MethodType.Getter)]
    private static class FastCreatures
    {
        private static bool Prefix(CombatState __instance, ref IReadOnlyList<Creature> __result)
        {
            var allies = __instance.Allies;
            var enemies = __instance.Enemies;
            var list = new List<Creature>(allies.Count + enemies.Count);
            for (int i = 0; i < allies.Count; i++) list.Add(allies[i]);
            for (int i = 0; i < enemies.Count; i++) list.Add(enemies[i]);
            __result = list;
            return false;
        }
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.PlayerCreatures), MethodType.Getter)]
    private static class FastPlayerCreatures
    {
        private static bool Prefix(CombatState __instance, ref IReadOnlyList<Creature> __result)
        {
            var list = new List<Creature>(1);
            Collect(__instance.Allies, list);
            Collect(__instance.Enemies, list);
            __result = list;
            return false;
        }

        private static void Collect(IReadOnlyList<Creature> creatures, List<Creature> into)
        {
            for (int i = 0; i < creatures.Count; i++)
                if (creatures[i].IsPlayer) into.Add(creatures[i]);
        }
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.Players), MethodType.Getter)]
    private static class FastPlayers
    {
        private static bool Prefix(CombatState __instance, ref IReadOnlyList<Player> __result)
        {
            var list = new List<Player>(1);
            Collect(__instance.Allies, list);
            Collect(__instance.Enemies, list);
            __result = list;
            return false;
        }

        private static void Collect(IReadOnlyList<Creature> creatures, List<Player> into)
        {
            for (int i = 0; i < creatures.Count; i++)
                if (creatures[i].Player is { } player) into.Add(player);
        }
    }
}
