using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace Sts2Ai.SimWorker.Runtime.Patches;

internal static partial class HeadlessPatches
{
    // ---- rule fidelity: undo TestMode branches that change game rules (tmp/notes/testmode-audit.md) ----

    /// <summary>
    /// Shipped game: the boulder rolls across the screen and damages each enemy separately as it reaches it, left
    /// to right. TestMode instead deals one multi-target hit. Enemies are laid out in list order, so hitting them
    /// one by one in that order reproduces the shipped sequence (encounters with authored slots are an
    /// approximation: their screen order comes from scene files we do not read).
    /// </summary>
    [HarmonyPatch(typeof(RollingBoulderPower), "DoDamage")]
    private static class RollingBoulderOneByOne
    {
        private static readonly Func<RollingBoulderPower, PlayerChoiceContext, IEnumerable<Creature>, Task<IEnumerable<DamageResult>>> DoDamage =
            AccessTools.MethodDelegate<Func<RollingBoulderPower, PlayerChoiceContext, IEnumerable<Creature>, Task<IEnumerable<DamageResult>>>>(
                AccessTools.Method(typeof(RollingBoulderPower), "DoDamage"));

        private static bool Prefix(RollingBoulderPower __instance, PlayerChoiceContext choiceContext, IEnumerable<Creature> targets,
            ref Task<IEnumerable<DamageResult>> __result)
        {
            var list = targets.ToList();
            if (list.Count <= 1) return true;
            RollingBoulderSplits++;
            __result = HitEach(__instance, choiceContext, list);
            return false;
        }

        private static async Task<IEnumerable<DamageResult>> HitEach(RollingBoulderPower power, PlayerChoiceContext ctx, List<Creature> targets)
        {
            var results = new List<DamageResult>();
            foreach (var target in targets) results.AddRange(await DoDamage(power, ctx, [target]));
            return results;
        }
    }

    /// <summary>
    /// Methods whose TestMode branch skips an RNG draw (or a grab-bag pull) that the shipped game makes, which would
    /// shift every later draw from that stream, or skips putting the run's state back: a shop left without the game's
    /// leaving it keeps its goods as the run's shop until the next one. Their shipped branch is plain logic, so it runs
    /// as-is with the TestMode check replaced by a constant.
    /// </summary>
    [HarmonyPatch]
    private static class ShippedBranch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
        [
            AccessTools.Method(typeof(MerchantPotionEntry), nameof(MerchantPotionEntry.CalcCost)),
            AccessTools.Method(typeof(Cauldron), "GenerateRewards"),
            AccessTools.Method(typeof(CallingBell), "GenerateRewards"),
            AccessTools.Method(typeof(MerchantRoom), nameof(MerchantRoom.Exit)),
        ];

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var isOn = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOn));
            var isOff = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
            int replaced = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(isOn) || ins.Calls(isOff))
                {
                    // Keep the instruction object so labels and exception blocks stay attached.
                    ins.opcode = ins.Calls(isOn) ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1;
                    ins.operand = null;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
                throw new InvalidOperationException($"headless: no TestMode check left in {original.DeclaringType?.Name}.{original.Name}; re-audit after the game update");
        }
    }

    // ---- rule fidelity: rules the shipped game checks through its screens, which do not exist headless ----

    /// <summary>
    /// Out of combat the Foul Potion can be thrown at a merchant: the shop's (100 gold) or the Fake Merchant's (a fight
    /// for the rug and the relics on its shelf). The game allows it only while that merchant's screen node exists with
    /// its inventory closed (<c>FoulPotion.GetFoulPotionMerchantTarget</c>), so headless it never could. Here it can in
    /// a shop and in the Fake Merchant's event before its fight, the screens the player can always throw it from.
    /// </summary>
    [HarmonyPatch(typeof(FoulPotion), nameof(FoulPotion.PassesCustomUsabilityCheck), MethodType.Getter)]
    private static class FoulPotionAtMerchants
    {
        private static void Postfix(FoulPotion __instance, ref bool __result)
        {
            if (__result || CombatManager.Instance.IsInProgress) return;
            __result = __instance.Owner.RunState.CurrentRoom switch
            {
                MerchantRoom => true,
                EventRoom { CanonicalEvent: FakeMerchant } room => room.LocalMutableEvent is FakeMerchant { StartedFight: false },
                _ => false,
            };
        }
    }

    /// <summary>
    /// The Foul Potion thrown at the Fake Merchant starts its fight from each player's event, as the game does, without
    /// the event's screen node, which the game's code needs first and returns without.
    /// </summary>
    [HarmonyPatch(typeof(FoulPotion), "OnUse")]
    private static class FoulPotionThrownAtTheFakeMerchant
    {
        private static bool Prefix(FoulPotion __instance, ref Task __result)
        {
            var run = __instance.Owner.RunState;
            if (CombatManager.Instance.IsInProgress || run.CurrentRoom is not EventRoom { CanonicalEvent: FakeMerchant }) return true;
            __result = Task.WhenAll(run.Players.Select(p => ((FakeMerchant)RunManager.Instance.EventSynchronizer.GetEventForPlayer(p)).FoulPotionThrown(__instance)));
            return false;
        }
    }
}
