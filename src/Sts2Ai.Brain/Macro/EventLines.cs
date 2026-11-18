using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>
/// NoSL self-play, for V's training (plans/2026-11-17-0306-nosl-shared-nets.md, 3.2; plans/2026-11-18-1340-nosl-v0.md,
/// 3.3): where an event's option ends when NoSL takes it, every option the run could have taken. The option is played
/// on the node's clone, which the simulator has and NoSL's own answers never read, and every decision after it inside
/// the room (an event's next page, a card prompt, a reward) is answered as NoSL answers it: from what the screen
/// shows, with <paramref name="noSl"/>, V in NoSL, and no exploring. The line ends where the room is left for the map,
/// or where the run ends; V's label for the option is V in NoSL there. A line that starts a combat, comes back to a
/// state already met or decided, or runs past <see cref="MaxSteps"/> answers gives no end: V learns no label from it.
///
/// The line is NoSL's, not SL's: SL's search takes the best of every answer after an option, each played for real, and
/// a later step whose result is hidden (a card to transform) would then be the best of several true results, which
/// NoSL cannot have.
/// </summary>
/// <param name="decided">The keys of the states decided before the event's decision (<see cref="CloneSearch.Key"/>).</param>
public sealed class EventLines(IRunValue noSl, IMacroClone clone, IReadOnlySet<ulong> decided)
{
    /// <summary>The answers after the option a line may take.</summary>
    public const int MaxSteps = 32;

    /// <summary>The options of an event decision whose lines are logged: those its buttons do not show the result of
    /// (<see cref="ScreenAfterstates.Pends"/>).</summary>
    public IReadOnlyList<EventLine> All(Afterstate root, IReadOnlyList<MacroAction> legal) =>
        root.Screen is not EventScreen screen ? [] :
        [.. legal.Where(a => ScreenAfterstates.Pends(screen, a)).Select(a => Walk(root, screen.Options.First(o => o.Index == a.Index), a))];

    /// <summary>The line of <paramref name="option"/>, picked by <paramref name="action"/> at <paramref name="root"/>.</summary>
    public EventLine Walk(Afterstate root, EventOptionView option, MacroAction action)
    {
        // A fresh brain a line: it keeps what its last answer opened (MacroBrain's _opened).
        var brain = new MacroBrain(noSl);
        var path = new List<MacroAction> { action };
        var seen = new HashSet<ulong>(decided) { CloneSearch.Key(root) };
        while (true)
        {
            var after = clone.After([path])[0];
            switch (after.Surface)
            {
                case null:
                    return new(option, after.Run, after.Won == true ? "won" : "died");
                case Surfaces.Map:
                    return new(option, after.Run, "left");
                case Surfaces.Combat:
                    return new(option, null, "fight");
            }
            if (!seen.Add(CloneSearch.Key(after))) return new(option, null, "again");
            if (path.Count > MaxSteps) return new(option, null, "long");
            var obs = new Observation { Run = after.Run, Screen = after.Screen! };
            path.Add(after.Screen is CardChoiceScreen { Choice: var choice }
                ? MacroAction.Pick(brain.Choose(new ChoiceDecision(choice, () => obs)).Picks)
                : brain.Choose(new MacroDecision(after.Surface, after.Legal, () => obs)).Action);
        }
    }
}
