using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// An event: "event:i" picks option i of the current page (locked options are shown but not legal); "proceed" leaves
/// for the map, once the event is finished or when the page offers a proceed button.
/// </summary>
public static class EventActions
{
    public const string Proceed = "proceed";

    /// <summary>
    /// Options flagged <see cref="EventOption.IsProceed"/> are UI buttons, never run headless: an ancient's "PROCEED"
    /// only opens the map (it is "proceed"), Trial's "DOUBLE_DOWN" opens the abandon-run popup (abandoning is never an
    /// action).
    /// </summary>
    public static List<string> Legal(EventModel ev)
    {
        if (ev.IsFinished) return [Proceed];
        var actions = new List<string>();
        var options = ev.CurrentOptions;
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            if (option.IsLocked) continue;
            if (!option.IsProceed) actions.Add($"event:{i}");
            else if (option.TextKey == "PROCEED" && !actions.Contains(Proceed)) actions.Add(Proceed);
        }
        return actions;
    }

    /// <summary>
    /// Picks an option as its button does: through the event synchronizer, which runs it in the background (the
    /// caller pumps until the game settles). "proceed" changes nothing in the game: the caller opens the map.
    /// </summary>
    public static void Start(EventModel ev, string action)
    {
        if (!Legal(ev).Contains(action)) throw ActionText.Illegal(action, Surfaces.Event);
        if (action == Proceed) return;
        RunManager.Instance.EventSynchronizer.ChooseLocalOption(ActionText.Index(action["event:".Length..]));
    }
}
