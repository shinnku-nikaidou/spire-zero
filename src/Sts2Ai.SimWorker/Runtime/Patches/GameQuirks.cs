using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Ai.SimWorker.Runtime.Patches;

internal static partial class HeadlessPatches
{
    // ---- game quirks: errors the shipped game logs too, fixed so that every game error stays a real one ----

    /// <summary>
    /// A card play pushes the card on its choice context and pops it at the end, but returns early, without the pop,
    /// when the player has died during the play (or the card left combat). The model stack only feeds multiplayer
    /// display, so nothing else changes; but the hook that started the play then pops its own model with the card
    /// on top and logs an error. Seen as Tools of the Trade discarding Ricochet at turn start, Sly playing it, and a
    /// Spiny Toad's Thorns killing the player (checked by selftest). This pops the card when the play left it on top.
    /// </summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    internal static class PopCardAfterEarlyReturn
    {
        /// <summary>How many plays returned early with the card left on top (checked by selftest).</summary>
        public static int Pops;

        private static void Postfix(CardModel __instance, PlayerChoiceContext choiceContext, ref Task __result)
        {
            if (__result.IsCompletedSuccessfully) PopIfLeft(__instance, choiceContext);
            else __result = PopAfter(__instance, choiceContext, __result);
        }

        private static async Task PopAfter(CardModel card, PlayerChoiceContext context, Task play)
        {
            await play;
            PopIfLeft(card, context);
        }

        private static void PopIfLeft(CardModel card, PlayerChoiceContext context)
        {
            if (context.LastInvolvedModel != card || !(card.Owner.Creature.IsDead || card.CombatState == null)) return;
            Pops++;
            context.PopModel(card);
        }
    }

    /// <summary>
    /// Transforming a card into a random one takes the first result of the transform, and the transform gives none
    /// once the combat is ending: the command then throws, out of the hook that asked. From a hook of the turn's
    /// start that ends the combat's turns, so the won combat never ends. Seen as Entropy 2 with Smokestack against the
    /// last enemy at 2 HP: the first card transformed was a Dazed, the Burn it became had Smokestack kill the enemy,
    /// and the second transform threw (checked by selftest). A transform asked in an ending combat does nothing, as
    /// the game's other commands do there.
    /// </summary>
    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.TransformToRandom))]
    internal static class NoRandomTransformInAnEndingCombat
    {
        /// <summary>How many transforms were left out (checked by selftest).</summary>
        public static int Skipped;

        private static bool Prefix(ref Task<CardPileAddResult> __result)
        {
            if (!CombatManager.Instance.IsEnding) return true;
            Skipped++;
            __result = Task.FromResult(default(CardPileAddResult));
            return false;
        }
    }

    /// <summary>
    /// A hook that asks the player in combat (Tools of the Trade's discard at a turn's start) becomes an action of its
    /// own, which the game lists until it starts: <c>GetHookActionForId</c> takes it off that list in a continuation of
    /// its start, attached with no scheduler, so that it runs on a thread-pool thread beside the game. There it races the
    /// game's own use of the list (an ArgumentOutOfRangeException, then a turn loop that dies and a combat that never
    /// settles) and an export of the combat walking it ("Collection was modified"): the two errors of the 33 failed runs
    /// of 3,300 in test-v11-0-td4-c11-0, on every host, since the simulator asks in combat as the game asks a player
    /// (da26190b). The continuation runs on the thread that attached it, the game's, as the game's awaits do (checked by
    /// selftest).
    /// </summary>
    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.GetHookActionForId))]
    internal static class HookActionStartedOnTheGamesThread
    {
        /// <summary>How many of these continuations were attached to the game's thread (checked by selftest).</summary>
        public static int Attached;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var continueWith = AccessTools.Method(typeof(Task), nameof(Task.ContinueWith), [typeof(Action<Task>)]);
            int replaced = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(continueWith))
                {
                    // Keep the instruction object so labels and exception blocks stay attached.
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(HookActionStartedOnTheGamesThread), nameof(OnTheGamesThread));
                    replaced++;
                }
                yield return ins;
            }
            if (replaced != 1)
                throw new InvalidOperationException($"headless: {replaced} Task.ContinueWith calls in ActionQueueSynchronizer.GetHookActionForId, not 1; re-audit after the game update");
        }

        private static Task OnTheGamesThread(Task started, Action<Task> continuation)
        {
            Attached++;
            return started.ContinueWith(continuation, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
