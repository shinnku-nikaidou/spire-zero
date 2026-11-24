using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>
/// An event: "event:i" picks option i of the current page (locked options are shown but not legal); "proceed" leaves
/// for the map, once the event is finished or when the page offers a proceed button.
/// </summary>
public static class EventActions
{
    /// <summary>The page and its buttons; a finished event shows only the proceed button.</summary>
    public static EventScreen Screen(EventModel ev, Player player)
    {
        return new()
        {
            Id = ev.Id.Entry,
            Page = ev.Description is { } shown ? $"{shown.LocTable}.{shown.LocEntryKey}" : null,
            Options = ev.IsFinished ? [] : ev.CurrentOptions.Select((o, i) =>
            {
                var tips = o.HoverTips.ToList();
                return new EventOptionView
                {
                    Index = i,
                    Key = o.TextKey,
                    Locked = o.IsLocked ? true : null,
                    Relic = o.Relic?.Id.Entry,
                    Tips = Tips(tips) is { Count: > 0 } ids ? ids : null,
                    Kills = o.WillKillPlayer?.Invoke(player) == true ? true : null,
                    Cards = Cards(tips, player) is { Count: > 0 } cards ? cards : null,
                    Relics = Relics(tips, player) is { Count: > 0 } relics ? relics : null,
                    Potions = Potions(tips, player) is { Count: > 0 } potions ? potions : null,
                    Numbers = Numbers(ev, o) is { Count: > 0 } numbers ? numbers : null,
                };
            }).ToList(),
            Finished = ev.IsFinished,
        };
    }

    /// <summary>The models the option's hover tips show (keyword and text tips are left out).</summary>
    private static List<string> Tips(List<IHoverTip> tips) => [.. tips
        .Select(t => t is CardHoverTip { Card: var card } ? card.Id.Entry + (card.IsUpgraded ? "+" : "") : t.CanonicalModel?.Id.Entry)
        .OfType<string>()];

    /// <summary>The cards the hover tips show, as the deck view shows a card; one of the deck's own is the deck's card itself.</summary>
    private static List<NamedCard> Cards(List<IHoverTip> tips, Player player) => [.. tips.OfType<CardHoverTip>()
        .Select(t => new NamedCard(Observe.Card(t.Card), player.Deck.Cards.Contains(t.Card) ? true : null))];

    /// <summary>The relics the hover tips show: one the player holds as the top bar shows it, another by its id.</summary>
    private static List<NamedRelic> Relics(List<IHoverTip> tips, Player player) => [.. tips
        .Select(t => t is CardHoverTip ? null : t.CanonicalModel as RelicModel).OfType<RelicModel>()
        .Select(r => player.Relics.FirstOrDefault(held => held.Id == r.Id) is { } held
            ? new NamedRelic(Observe.Relic(held), true) : new NamedRelic(new RelicView { Id = r.Id.Entry }))];

    /// <summary>The potions the hover tips show, by id, and whether the belt holds one.</summary>
    private static List<NamedPotion> Potions(List<IHoverTip> tips, Player player) => [.. tips
        .Select(t => t is CardHoverTip ? null : t.CanonicalModel as PotionModel).OfType<PotionModel>()
        .Select(p => new NamedPotion(p.Id.Entry, player.PotionSlots.Any(held => held?.Id == p.Id) ? true : null))];

    /// <summary>
    /// The event's numbers that the option's title and text and the page's text show, as shown, by "EVENT.Name". The
    /// event's other variables are left out: an event may keep there what it does not show. Its names (a card's, a
    /// relic's) are strings, not numbers: the hover tips show those models. Which variables a text shows is read from
    /// <c>EventTexts.json</c>, not from the text: the headless game loads none. A number the game draws from the unseeded
    /// <c>Rng.Chaotic</c> is left out too (<see cref="Chaotic"/>): no save or clone gives it back, so a clone of the run
    /// would show another page than the run did.
    /// </summary>
    private static SortedDictionary<string, decimal> Numbers(EventModel ev, EventOption option)
    {
        var numbers = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var text in new[] { ev.Description, option.Title, option.Description })
            if (text is { LocTable: "events" } && Shown.TryGetValue(text.LocEntryKey, out var names))
                foreach (string name in names)
                    if (ev.DynamicVars.TryGetValue(name, out var v) && v is not StringVar && !Chaotic.Contains($"{ev.Id.Entry}.{v.Name}"))
                        // as a text shows it: the whole part (DynamicVar.ToString), 3 for an HP loss of 3.35
                        numbers[$"{ev.Id.Entry}.{v.Name}"] = (int)v.PreviewValue;
        return numbers;
    }

    /// <summary>
    /// The event numbers the game draws from <c>Rng.Chaotic</c>, flavor its texts show and nothing reads (v0.113.1):
    /// Trial's entrant number (<c>Trial.CalculateVars</c>, 101 to 998), drawn again in every copy of the event.
    /// </summary>
    private static readonly HashSet<string> Chaotic = ["TRIAL.EntrantNumber"];

    /// <summary>The game's events text table as the nets need it (<c>EventTexts.json</c>, which <c>event_texts.py</c> writes
    /// from the game's pack): every option's key, and for each text that fills in variables, their names.</summary>
    private sealed record EventTexts(IReadOnlyList<string> Options, Dictionary<string, string[]> Shown);

    private static readonly EventTexts Texts = ReadTexts();

    /// <summary>Every event option's text key, as the game's events text table lists them (<see cref="EventOptionView.Key"/>).</summary>
    public static IReadOnlyList<string> OptionKeys => Texts.Options;

    /// <summary>For each text of the game's events table that fills in variables, their names.</summary>
    private static Dictionary<string, string[]> Shown => Texts.Shown;

    private static EventTexts ReadTexts()
    {
        using var stream = typeof(EventActions).Assembly.GetManifestResourceStream("EventTexts.json")
            ?? throw new InvalidOperationException("GameBridge holds no EventTexts.json");
        return JsonSerializer.Deserialize<EventTexts>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("EventTexts.json is null");
    }

    /// <summary>
    /// Options flagged <see cref="EventOption.IsProceed"/> are UI buttons, never run headless: an ancient's "PROCEED"
    /// only opens the map (it is "proceed"), Trial's "DOUBLE_DOWN" opens the abandon-run popup (abandoning is never an
    /// action).
    /// </summary>
    public static List<MacroAction> Legal(EventModel ev)
    {
        if (ev.IsFinished) return [MacroAction.Proceed];
        var actions = new List<MacroAction>();
        var options = ev.CurrentOptions;
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            if (option.IsLocked) continue;
            if (!option.IsProceed) actions.Add(MacroAction.Of(MacroAction.Event, i));
            else if (option.TextKey == "PROCEED" && !actions.Contains(MacroAction.Proceed)) actions.Add(MacroAction.Proceed);
        }
        return actions;
    }

    /// <summary>
    /// Picks an option as its button does: through the event synchronizer, which runs it in the background (the
    /// caller pumps until the game settles). "proceed" changes nothing in the game: the caller opens the map.
    /// </summary>
    public static void Start(EventModel ev, MacroAction action)
    {
        if (!Legal(ev).Contains(action)) throw Illegal.Action(action, Surfaces.Event);
        if (action == MacroAction.Proceed) return;
        RunManager.Instance.EventSynchronizer.ChooseLocalOption(action.Index);
    }
}
