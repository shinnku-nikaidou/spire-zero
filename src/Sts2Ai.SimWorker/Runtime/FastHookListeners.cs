using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Ai.SimWorker.Runtime;

/// <summary>
/// Hook dispatch is most of the simulator's time. For every dispatch (about 75 per action) the game builds a new
/// listener list plus two or three iterator objects, for the run and for the combat. These replacements fill a
/// pooled list instead, and keep the original semantics:
///  - the same models in the same order, copied from the decompiled iterators;
///  - the snapshot is taken at the first MoveNext, not when the method is called;
///  - each listener is re-checked with the game's own Contains just before it is yielded, so a model removed by
///    an earlier listener is skipped, as before.
/// SelfTest compares both with the original iterators (kept through reverse patches) at every cut point.
/// </summary>
internal static class FastHookListeners
{
    private static readonly Func<CombatState, AbstractModel, bool> CombatContains =
        AccessTools.MethodDelegate<Func<CombatState, AbstractModel, bool>>(AccessTools.Method(typeof(CombatState), "Contains"));
    private static readonly Func<AbstractModel, bool> RunContains =
        AccessTools.MethodDelegate<Func<AbstractModel, bool>>(AccessTools.Method(typeof(RunState), "Contains"));

    [ThreadStatic] private static Stack<List<AbstractModel>>? _pool;

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
    private static class CombatListeners
    {
        private static bool Prefix(CombatState __instance, ref IEnumerable<AbstractModel> __result)
        {
            __result = new Listeners(__instance, null, null);
            return false;
        }
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners))]
    private static class RunListeners
    {
        private static bool Prefix(RunState __instance, ICombatState? childCombatState, ref IEnumerable<AbstractModel> __result)
        {
            __result = new Listeners(null, __instance, childCombatState);
            return false;
        }
    }

    /// <summary>The unpatched iterators, for SelfTest.</summary>
    [HarmonyPatch]
    internal static class Original
    {
        [HarmonyReversePatch, HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
        public static IEnumerable<AbstractModel> Combat(CombatState instance) => throw new NotImplementedException("reverse patch");

        [HarmonyReversePatch, HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners))]
        public static IEnumerable<AbstractModel> Run(RunState instance, ICombatState? childCombatState) => throw new NotImplementedException("reverse patch");
    }

    /// <summary>Like a compiler-generated iterator: the first GetEnumerator returns the object itself.</summary>
    private sealed class Listeners(CombatState? combat, RunState? run, ICombatState? child) : IEnumerable<AbstractModel>, IEnumerator<AbstractModel>
    {
        private enum Phase { NotStarted, Snapshot, ModSubscribers, ChildCombat, Done }

        private Phase _phase;
        private bool _handedOut;
        private List<AbstractModel>? _list;
        private int _index;
        private IEnumerator<AbstractModel>? _inner;

        public AbstractModel Current { get; private set; } = null!;
        object System.Collections.IEnumerator.Current => Current;

        public IEnumerator<AbstractModel> GetEnumerator()
        {
            if (_handedOut) return new Listeners(combat, run, child);
            _handedOut = true;
            return this;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public bool MoveNext()
        {
            while (true)
            {
                switch (_phase)
                {
                    case Phase.NotStarted:
                        _list = _pool is { Count: > 0 } pool ? pool.Pop() : new List<AbstractModel>(64);
                        if (combat != null) FillCombat(combat, _list);
                        else FillRun(run!, child, _list);
                        _phase = Phase.Snapshot;
                        break;

                    case Phase.Snapshot:
                        while (_index < _list!.Count)
                        {
                            var model = _list[_index++];
                            if (combat != null ? CombatContains(combat, model) : RunContains(model))
                            {
                                Current = model;
                                return true;
                            }
                        }
                        ReturnList();
                        _inner = (combat != null ? ModHelper.IterateAllCombatStateSubscribers(combat) : ModHelper.IterateAllRunStateSubscribers(run!)).GetEnumerator();
                        _phase = Phase.ModSubscribers;
                        break;

                    case Phase.ModSubscribers:
                        if (_inner!.MoveNext())
                        {
                            Current = _inner.Current;
                            return true;
                        }
                        _inner.Dispose();
                        _inner = child?.IterateHookListeners().GetEnumerator();
                        _phase = _inner == null ? Phase.Done : Phase.ChildCombat;
                        break;

                    case Phase.ChildCombat:
                        if (_inner!.MoveNext())
                        {
                            Current = _inner.Current;
                            return true;
                        }
                        _inner.Dispose();
                        _inner = null;
                        _phase = Phase.Done;
                        break;

                    default:
                        return false;
                }
            }
        }

        public void Dispose()
        {
            ReturnList();
            _inner?.Dispose();
            _inner = null;
            _phase = Phase.Done;
        }

        public void Reset() => throw new NotSupportedException();

        private void ReturnList()
        {
            if (_list == null) return;
            _list.Clear();
            (_pool ??= new()).Push(_list);
            _list = null;
        }
    }

    /// <summary>The list CombatState.IterateHookListeners builds, in the same order.</summary>
    private static void FillCombat(CombatState state, List<AbstractModel> list)
    {
        var allies = state.Allies;
        var enemies = state.Enemies;
        for (int i = 0; i < allies.Count + enemies.Count; i++)
        {
            var creature = i < allies.Count ? allies[i] : enemies[i - allies.Count];
            list.AddRange(creature.Powers);
            var player = creature.Player;
            if (player == null)
            {
                list.Add(creature.Monster!);
                continue;
            }
            if (!player.IsActiveForHooks) continue;
            var relics = player.Relics;
            for (int j = 0; j < relics.Count; j++)
                if (!relics[j].IsMelted) list.Add(relics[j]);
            var potions = player.PotionSlots;
            for (int j = 0; j < potions.Count; j++)
                if (potions[j] != null) list.Add(potions[j]!);
            if (player.PlayerCombatState == null) continue;
            list.AddRange(player.PlayerCombatState.OrbQueue.Orbs);
            var piles = player.PlayerCombatState.AllPiles;
            for (int p = 0; p < piles.Count; p++)
            {
                var cards = piles[p].Cards;
                for (int c = 0; c < cards.Count; c++)
                {
                    var card = cards[c];
                    list.Add(card);
                    if (card.Affliction != null) list.Add(card.Affliction);
                    if (card.Enchantment != null) list.Add(card.Enchantment);
                }
            }
        }
        for (int i = 0; i < state.Modifiers.Count; i++) list.Add(state.Modifiers[i]);
        for (int i = 0; i < state.BadgeModels.Count; i++) list.Add(state.BadgeModels[i]);
        if (state.MultiplayerScalingModel != null) list.Add(state.MultiplayerScalingModel);
    }

    /// <summary>The list RunState.IterateHookListeners builds, in the same order. Combat listeners follow it lazily.</summary>
    private static void FillRun(RunState state, ICombatState? child, List<AbstractModel> list)
    {
        var players = state.Players;
        for (int i = 0; i < players.Count; i++)
        {
            if (!players[i].IsActiveForHooks) continue;
            var deck = players[i].Deck.Cards;
            for (int c = 0; c < deck.Count; c++)
            {
                list.Add(deck[c]);
                if (deck[c].Enchantment != null) list.Add(deck[c].Enchantment!);
            }
        }
        if (child != null) return;
        for (int i = 0; i < players.Count; i++)
        {
            if (!players[i].IsActiveForHooks) continue;
            var relics = players[i].Relics;
            for (int j = 0; j < relics.Count; j++)
                if (!relics[j].IsMelted) list.Add(relics[j]);
            var potions = players[i].PotionSlots;
            for (int j = 0; j < potions.Count; j++)
                if (potions[j] != null) list.Add(potions[j]!);
        }
        list.AddRange(state.Modifiers);
        list.AddRange(state.BadgeModels);
        list.Add(state.MultiplayerScalingModel);
    }
}
