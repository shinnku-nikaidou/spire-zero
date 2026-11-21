using MegaCrit.Sts2.Core.Combat;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

/// <summary>One combat of a whole run as the real game played it: its decisions and its end.</summary>
internal sealed record RunCombat(int Act, int Floor, string Encounter, IReadOnlyList<RealStep> Steps, CombatStatus End);

/// <summary>
/// The lockstep check's real side over a whole run: the game plays the run with a seeded random player (the sweep's
/// player in combat, uniform choices elsewhere), and every combat of it is kept as <see cref="Lockstep.Compare"/> takes
/// one: what the game showed at each decision, the run around the combat with it, the action it was given, and what
/// makes a second world there. A combat of a run meets what a combat started on its own cannot: the deck, relics and
/// potions a run really builds, what events put on cards, and the run the combat's end changes.
/// </summary>
internal static class RunLockstep
{
    /// <param name="seed">Seeds the player.</param>
    /// <param name="keep">Called at every decision of a combat and at its end, while it is live; null: nothing is dumped.</param>
    /// <returns>The run's combats in order, how the run ended, and the game's own Log.Error lines on the way.</returns>
    /// <param name="opening">Called as each combat begins (<see cref="CombatOpening"/>), after the run's own; null: nothing.</param>
    public static (List<RunCombat> Combats, string End, IReadOnlyList<string> GameErrors) Record(Pump pump, RunSetup setup, int seed, IRunWorlds worlds, Keep? keep,
        Action? opening = null)
    {
        HeadlessGame.TakeErrors();
        var rng = new Random(seed);
        var (policy, answers) = (new SweepPolicy(rng), new RandomChoices(rng));
        var combats = new List<RunCombat>();
        RunSession? live = null;
        (int Act, int Floor, string Encounter, List<RealStep> Steps)? open = null;

        // The combat as it stands, with what the game is about to be given there; no action: the combat's end.
        void Note(CombatAction? action)
        {
            var session = live!;
            var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state");
            var (player, prompt) = (session.Player, session.Prompt);
            var status = player.Creature.IsDead ? CombatStatus.Lost : CombatManager.Instance.IsInProgress ? CombatStatus.InProgress : CombatStatus.Won;
            bool plays = status == CombatStatus.InProgress && prompt == null;
            var observation = session.Combat();
            var shown = new Shown(ProtocolJson.Serialize(observation), plays ? [.. CombatActions.Legal(state, player).Select(a => a.ToString())] : [], status,
                ProtocolJson.Serialize(session.View(previews: false)));
            open ??= (session.Run.CurrentActIndex, session.Run.TotalFloor, state.Encounter?.Id.Entry ?? "<none>", []);
            open.Value.Steps.Add(new(shown, state.RoundNumber, action, action is { } taken ? Lockstep.Subject(player, prompt, taken) : null,
                action == null ? null : worlds.At(session), GameIds.Present(state, player, prompt), keep?.Invoke(player, status, prompt != null, observation)));
        }

        var combat = new Plays(decision =>
        {
            var action = policy.Choose(decision);
            Note(action);
            return action;
        });
        // A card prompt of a combat is a decision of it; one of another screen (an event's, a rest site's) is the run's own.
        // A combat's answer is given as its action says it, the options in their order: the order of the picks can
        // matter (the discards of Gambling Chip land in it), and a world is given the action, not the policy's answer.
        var choices = new Picks(decision =>
        {
            var answer = answers.Choose(decision);
            if (!CombatManager.Instance.IsInProgress) return answer;
            var action = CombatAction.Pick(answer.Picks);
            Note(action);
            return new([.. action.Picks]);
        });
        using var session = live = RunSession.Start(pump, setup, choices, combat, new RandomMacro(rng));
        var own = CombatOpening.Starting;
        CombatOpening.Starting = () =>
        {
            own?.Invoke();
            opening?.Invoke();
        };
        while (!session.IsOver)
        {
            session.Step();
            session.Settle();
            if (open is not { } played || (CombatManager.Instance.IsInProgress && !session.IsOver)) continue;
            Note(null);
            combats.Add(new(played.Act, played.Floor, played.Encounter, played.Steps, played.Steps[^1].Shown.Status));
            open = null;
        }
        string end = session.Won ? "won" : $"died on floor {session.Run.TotalFloor}";
        return (combats, end, HeadlessGame.TakeErrors());
    }

    private sealed class Plays(Func<CombatDecision, CombatAction> choose) : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => choose(decision);
    }

    private sealed class Picks(Func<ChoiceDecision, ChoiceAnswer> choose) : IChoicePolicy
    {
        public ChoiceAnswer Choose(ChoiceDecision decision) => choose(decision);
    }
}
