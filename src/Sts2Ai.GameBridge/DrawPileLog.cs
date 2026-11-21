using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace Sts2Ai.GameBridge;

/// <summary>
/// The local player's draw pile as the player knows it: what anyone watching the screen saw happen to it since its
/// last shuffle, each card by its card key (<see cref="Observe.CardKey"/>), never its place unless the player saw the
/// place. The export carries it (<c>sim.draw_log</c>), and a NoSL world drawn from the real combat replays it to draw
/// the pile's order (engine/crates/sts2ai-engine/src/drawlog.rs; plans/2026-11-17-1120-nosl-dread-architecture.md 9.3).
///
/// The game changes the pile only through <see cref="CardPile"/>'s own members, and each change is put down here by
/// the rule that made it, which is what the player knows of it:
/// <list type="bullet">
/// <item>a shuffle (the opening one, <see cref="CardPileCmd.Shuffle"/>): the pile's cards as a multiset, then the cards
/// a rule moved to the top after it (Perfect Fit);</item>
/// <item>a card entering through <see cref="CardPileCmd.Add(IEnumerable{CardModel}, CardPile, CardPilePosition, AbstractModel?, bool, bool)"/>:
/// on top, at the bottom, or at a random place, as the call asked (a patch of its body hands the position on); a card
/// of the pile that goes through it again (Ki Chant puts the pile back on top after its shuffle) is a move;</item>
/// <item>the opening's moves to the bottom (an enchantment that starts there) and to the top (Innate);</item>
/// <item>a card leaving: from the top when it is drawn (<see cref="CombatHistory.CardDrawn"/>), from the top, the
/// bottom or anywhere when <see cref="CardPileCmd.AutoPlayFromDrawPile(PlayerChoiceContext, Player, int, CardPilePosition, bool, Action{CardModel})"/>
/// plays it, the top cards together when Ki Chant shows them, and otherwise from a place the player cannot see (one
/// picked from the pile, one taken at random among some);</item>
/// <item>a card replaced where it stands (a transform), and a card that shows another key than before, as a change
/// of a card the player cannot place.</item>
/// </list>
/// A host installs the patches with <see cref="Patch"/>; everything runs on the game's thread. A change no rule above
/// explains marks the record broken, which the engine refuses to draw from: the game itself never sees an error.
/// </summary>
public static class DrawPileLog
{
    /// <summary>One event. Keys are filled in when the record is next brought up to date, so that a shuffle's cards
    /// are read when the combat around them stands.</summary>
    private sealed class Entry(string op)
    {
        public string Op = op;
        public string? Card;
        public string? At;
        public List<string>? Cards;
        public string? To;
        /// <summary>A card whose key is read when the record is brought up to date (a move).</summary>
        public CardModel? KeyOf;
        /// <summary>A shuffle's cards, whose keys are read when the record is brought up to date.</summary>
        public List<CardModel>? KeysOf;
        /// <summary>A card taken whose rule is not known yet: a draw or a play from the pile says it later.</summary>
        public CardModel? Taken;
        /// <summary>The taken card went into another pile: it left this one for good, and coming back is an add.</summary>
        public bool Landed;
    }

    private static readonly List<Entry> Entries = [];
    /// <summary>Each card of the pile and the key it showed when last read; null while not read yet.</summary>
    private static readonly Dictionary<CardModel, string?> Keys = new(ReferenceEqualityComparer.Instance);
    /// <summary>The pile the record is of; none before the first shuffle.</summary>
    private static CardPile? _pile;
    /// <summary>While a shuffle puts the cards in their new order: how many it has still to put in. It takes out the
    /// cards the pile held and puts every card in once, at the bottom.</summary>
    private static int? _rebuilding;
    /// <summary>Ki Chant's cards, put down as taken together, whose leaving is not put down again.</summary>
    private static readonly HashSet<CardModel> TakenTogether = new(ReferenceEqualityComparer.Instance);
    /// <summary>The plays from the pile that <see cref="CardPileCmd.AutoPlayFromDrawPile(PlayerChoiceContext, Player, int, CardPilePosition, bool, Action{CardModel})"/>
    /// is making: from where, and how many are left.</summary>
    private static (CardPilePosition Position, int Left)? _autoPlay;
    /// <summary>The position <see cref="CardPileCmd.Add(IEnumerable{CardModel}, CardPile, CardPilePosition, AbstractModel?, bool, bool)"/>
    /// asked for, during its call of <see cref="CardPile.AddInternal"/>.</summary>
    [ThreadStatic] private static CardPilePosition? _adding;
    /// <summary>The order of the pile a shuffle made, before the rules moved cards (<see cref="ShuffleStarts"/>).</summary>
    private static List<CardModel>? _shuffled;
    private static string? _broken;

    public static void Patch(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(AddPosition)).Patch();
        harmony.CreateClassProcessor(typeof(Primitives)).Patch();
        harmony.CreateClassProcessor(typeof(Rules)).Patch();
    }

    /// <summary>The record as <see cref="Write"/> writes it, as text: what a host gives at a card prompt, where no export
    /// can be taken (plans/2026-11-21-1213-nosl-prompt-worlds.md).</summary>
    public static string Json()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) Write(json);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The record as the export's <c>sim.draw_log</c>: a list of events, the first a shuffle; empty before the
    /// combat's first shuffle.</summary>
    public static void Write(Utf8JsonWriter json)
    {
        Guard(BringUpToDate);
        json.WriteStartArray();
        if (_broken != null)
        {
            json.WriteStartObject();
            json.WriteString("op", "broken");
            json.WriteString("why", _broken);
            json.WriteEndObject();
        }
        else
        {
            foreach (var e in Entries)
            {
                json.WriteStartObject();
                json.WriteString("op", e.Op);
                if (e.Card != null) json.WriteString("card", e.Card);
                if (e.Taken != null || e.At != null) json.WriteString("at", e.At ?? "any");
                if (e.To != null) json.WriteString("to", e.To);
                if (e.Cards != null)
                {
                    json.WriteStartArray("cards");
                    foreach (var key in e.Cards) json.WriteStringValue(key);
                    json.WriteEndArray();
                }
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();
    }

    /// <summary>
    /// The program around the game put the local player's draw pile in an order of its own, drawn where nobody saw
    /// (a combat built to show an observation, the simulator's own determinize): the record starts again from a
    /// shuffle of the pile as it stands.
    /// </summary>
    public static void Shuffled(CardPile pile) => Reset(pile, [.. pile.Cards]);

    /// <summary>Runs a step of the record: an error marks the record broken and never reaches the game.</summary>
    private static void Guard(Action step)
    {
        if (_broken != null) return;
        try
        {
            step();
        }
        catch (Exception e)
        {
            _broken = $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static void Break(string why) => _broken ??= why;

    /// <summary>A new record, from a shuffle of <paramref name="pile"/> into <paramref name="order"/>.</summary>
    private static void Reset(CardPile pile, List<CardModel> order)
    {
        Entries.Clear();
        Keys.Clear();
        TakenTogether.Clear();
        _autoPlay = null;
        _broken = null;
        _pile = pile;
        foreach (var card in order) Keys[card] = null;
        Entries.Add(new Entry("reset") { KeysOf = [.. order] });
    }

    /// <summary>
    /// Reads every card's key: a card read before that shows another key now changed where the player cannot place it
    /// (put down before what comes next, in an order of keys, not of places); the shuffle and the moves not read yet
    /// take their keys. <paramref name="entering"/> is a card just put in the pile, not part of the record yet.
    /// </summary>
    private static void BringUpToDate() => BringUpToDate(null);

    private static void BringUpToDate(CardModel? entering)
    {
        if (_pile == null || _rebuilding != null) return;
        var changed = new List<(string From, string To)>();
        foreach (var card in _pile.Cards)
        {
            if (ReferenceEquals(card, entering)) continue;
            if (!Keys.TryGetValue(card, out var known))
            {
                Break($"{card.Id.Entry} is in the draw pile, but the record never saw it come in");
                return;
            }
            var key = Observe.CardKey(card);
            if (known != null && known != key) changed.Add((known, key));
            Keys[card] = key;
        }
        foreach (var e in Entries)
        {
            if (e.KeysOf != null)
            {
                e.Cards = [.. e.KeysOf.Select(c => Keys.GetValueOrDefault(c) ?? Observe.CardKey(c)).Order(StringComparer.Ordinal)];
                e.KeysOf = null;
            }
            if (e.KeyOf != null)
            {
                e.Card = Keys.GetValueOrDefault(e.KeyOf) ?? Observe.CardKey(e.KeyOf);
                e.KeyOf = null;
            }
        }
        foreach (var (from, to) in changed.OrderBy(c => c.From, StringComparer.Ordinal).ThenBy(c => c.To, StringComparer.Ordinal))
            Entries.Add(new Entry("relabel") { Card = from, To = to });
    }

    /// <summary><paramref name="card"/>'s leaving the pile, while its rule is not known yet.</summary>
    private static Entry? Pending(CardModel card) => Entries.LastOrDefault(e => ReferenceEquals(e.Taken, card));

    /// <summary><paramref name="card"/>'s leaving the pile, while it has gone nowhere else yet.</summary>
    private static Entry? InHand(CardModel card) => Pending(card) is { Landed: false } taken ? taken : null;

    private static string Place(CardPilePosition position) => position switch
    {
        CardPilePosition.Top => "top",
        CardPilePosition.Bottom => "bottom",
        CardPilePosition.Random => "random",
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
    };

    /// <summary>
    /// The body of <see cref="CardPileCmd.Add(IEnumerable{CardModel}, CardPile, CardPilePosition, AbstractModel?, bool, bool)"/>
    /// puts each card in with <see cref="CardPile.AddInternal"/> at an index made from the position asked for: a random
    /// one for <see cref="CardPilePosition.Random"/>, which the index alone does not tell from the top or the bottom.
    /// The patch hands the position to that call (<see cref="AddAt"/>).
    /// </summary>
    [HarmonyPatch]
    private static class AddPosition
    {
        private static MethodBase TargetMethod() => AccessTools.AsyncMoveNext(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
            [typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool), typeof(bool)]));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            var position = AccessTools.Field(original.DeclaringType, "position")
                ?? throw new MissingFieldException("CardPileCmd.Add's state machine has no field position: the game changed");
            var addInternal = AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal));
            var at = AccessTools.Method(typeof(DrawPileLog), nameof(AddAt));
            int found = 0;
            foreach (var instruction in code)
            {
                if (instruction.Calls(addInternal))
                {
                    found++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Ldfld, position);
                    yield return new CodeInstruction(OpCodes.Call, at);
                    continue;
                }
                yield return instruction;
            }
            if (found != 1) throw new InvalidOperationException($"CardPileCmd.Add calls AddInternal {found} times, not once: the game changed");
        }
    }

    /// <summary><see cref="CardPile.AddInternal"/> as <see cref="CardPileCmd.Add(IEnumerable{CardModel}, CardPile, CardPilePosition, AbstractModel?, bool, bool)"/>
    /// calls it, with the position it was asked for.</summary>
    public static void AddAt(CardPile pile, CardModel card, int index, bool silent, CardPilePosition position)
    {
        _adding = position;
        try
        {
            pile.AddInternal(card, index, silent);
        }
        finally
        {
            _adding = null;
        }
    }

    [HarmonyPatch]
    private static class Primitives
    {
        [HarmonyPatch(typeof(CardPile), nameof(CardPile.AddInternal)), HarmonyPostfix]
        private static void Added(CardPile __instance, CardModel card, int index) => Guard(() =>
        {
            var position = _adding;
            if (_autoPlay is { Left: > 0 } play && __instance.Type == PileType.Play && Pending(card) is { } played)
            {
                // A play from the draw pile: from where the call said.
                played.At = play.Position == CardPilePosition.Random ? "any" : Place(play.Position);
                played.Taken = null;
                _autoPlay = play.Left > 1 ? play with { Left = play.Left - 1 } : null;
            }
            if (!ReferenceEquals(__instance, _pile))
            {
                // A card taken from the pile is somewhere else now: a draw still says it came from the top.
                if (Pending(card) is { } landed) landed.Landed = true;
                return;
            }
            if (_rebuilding is { } left)
            {
                // A shuffle putting its order in: the record has it already.
                Keys[card] = Keys.GetValueOrDefault(card);
                _rebuilding = left > 1 ? left - 1 : null;
                return;
            }
            // The record as it stood before this card came: bringing it up to date may put down changes after it.
            int before = Entries.Count;
            BringUpToDate(card);
            if (_broken != null) return;
            var key = Observe.CardKey(card);
            if (position is { } at)
            {
                if (InHand(card) is { } moved)
                {
                    // A card of the pile put back in it: where it was is still not known, where it goes is.
                    moved.Op = "move";
                    moved.At = Place(at);
                    moved.Taken = null;
                }
                else Entries.Add(new Entry("add") { Card = key, At = Place(at) });
            }
            else if (index >= 0 && before > 0 && Entries[before - 1] is { Taken: { } replaced, Landed: false } transformed)
            {
                // The game's transform: the new card where the old one stood. Taking the old one out can change what
                // other cards of the pile show (Crescent Spear counts the cards that cost stars), put down just now after it.
                transformed.Op = "relabel";
                transformed.To = key;
                transformed.Taken = null;
                transformed.At = null;
                Keys.Remove(replaced);
            }
            else
            {
                Break($"{card.Id.Entry} entered the draw pile at {index} by no rule the record knows");
                return;
            }
            Keys[card] = key;
        });

        [HarmonyPatch(typeof(CardPile), nameof(CardPile.RemoveInternal)), HarmonyPrefix]
        private static void Removing(CardPile __instance, CardModel card) => Guard(() =>
        {
            if (!ReferenceEquals(__instance, _pile) || _rebuilding != null) return;
            if (TakenTogether.Remove(card))
            {
                Keys.Remove(card);
                return;
            }
            BringUpToDate();
            if (_broken != null) return;
            var key = Keys.GetValueOrDefault(card) ?? Observe.CardKey(card);
            Keys.Remove(card);
            Entries.Add(new Entry("take") { Card = key, Taken = card });
        });

        [HarmonyPatch(typeof(CardPile), nameof(CardPile.MoveToTopInternal)), HarmonyPostfix]
        private static void MovedToTop(CardPile __instance, CardModel card) => Moved(__instance, card, "top");

        [HarmonyPatch(typeof(CardPile), nameof(CardPile.MoveToBottomInternal)), HarmonyPostfix]
        private static void MovedToBottom(CardPile __instance, CardModel card) => Moved(__instance, card, "bottom");

        private static void Moved(CardPile pile, CardModel card, string at) => Guard(() =>
        {
            if (!ReferenceEquals(pile, _pile) || _rebuilding != null) return;
            BringUpToDate();
            if (_broken != null) return;
            Entries.Add(new Entry("move") { Card = Keys.GetValueOrDefault(card), KeyOf = Keys.GetValueOrDefault(card) == null ? card : null, At = at });
        });
    }

    [HarmonyPatch]
    private static class Rules
    {
        /// <summary>Every shuffle of a draw pile, the opening's and the game's own, runs the rules that move cards
        /// after it: the order before them is the shuffle's, the order after them the pile's.</summary>
        [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyShuffleOrder)), HarmonyPrefix]
        private static void ShuffleStarts(Player player, List<CardModel> cards)
        {
            if (LocalContext.IsMe(player)) _shuffled = [.. cards];
        }

        [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyShuffleOrder)), HarmonyPostfix]
        private static void Shuffled(Player player, List<CardModel> cards, bool isInitialShuffle) => Guard(() =>
        {
            if (!LocalContext.IsMe(player) || _shuffled is not { } before) return;
            _shuffled = null;
            var pile = player.PlayerCombatState?.DrawPile ?? throw new InvalidOperationException("a shuffle with no draw pile");
            Reset(pile, cards);
            // The cards the rules moved: the first ones, the rest in the shuffle's order. Each went to the top in turn.
            int moved = 0;
            while (moved < cards.Count && !cards.Skip(moved).SequenceEqual(before.Where(c => !cards.Take(moved).Contains(c)))) moved++;
            for (int i = moved - 1; i >= 0; i--) Entries.Add(new Entry("move") { KeyOf = cards[i], At = "top" });
            // The opening shuffle orders the pile in place; the game's shuffle puts the cards in after this.
            if (!isInitialShuffle && cards.Count > 0) _rebuilding = cards.Count;
        });

        [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardDrawn)), HarmonyPostfix]
        private static void Drawn(CardModel card) => Guard(() =>
        {
            if (Pending(card) is not { } drawn) return;
            drawn.At = "top";
            drawn.Taken = null;
        });

        [HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.AutoPlayFromDrawPile),
            [typeof(PlayerChoiceContext), typeof(Player), typeof(int), typeof(CardPilePosition), typeof(bool), typeof(Action<CardModel>)]), HarmonyPrefix]
        private static void PlaysFromPile(Player player, int count, CardPilePosition position)
        {
            if (LocalContext.IsMe(player)) _autoPlay = count > 0 ? (position, count) : null;
        }

        /// <summary>Ki Chant shows the pile's top cards, and every one of them leaves the pile: taken together.</summary>
        [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile),
            [typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>)]), HarmonyPrefix]
        private static void Shows(PlayerChoiceContext context, CardPile pile, Func<CardModel, bool>? filter) => Guard(() =>
        {
            if (!ReferenceEquals(pile, _pile) || context.LastInvolvedModel is not KiChant || filter == null) return;
            BringUpToDate();
            if (_broken != null) return;
            var top = pile.Cards.Where(filter).ToList();
            Entries.Add(new Entry("take_top") { Cards = [.. top.Select(c => Keys.GetValueOrDefault(c) ?? Observe.CardKey(c)).Order(StringComparer.Ordinal)] });
            foreach (var card in top) TakenTogether.Add(card);
        });
    }
}
