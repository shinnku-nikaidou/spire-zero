using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>
/// L0's probes in SL (docs: macro-brain/search §6): a node's clone at a decision outside combat, loaded once per path, plays
/// the path's answers from the decision and stops at the next decision the game asks, which it reports
/// (<see cref="Afterstate"/>). Nothing else is played: a combat, the map or the run's end stops a probe like any decision.
/// </summary>
internal static class NodeProbe
{
    public static IReadOnlyList<Afterstate> After(Pump pump, NodeClone node, IReadOnlyList<IReadOnlyList<MacroAction>> paths)
    {
        var opened = Checkpoint.Open(node.Save);
        return [.. paths.Select(path => After(pump, node, opened, path))];
    }

    private static Afterstate After(Pump pump, NodeClone node, Checkpoint.Opened opened, IReadOnlyList<MacroAction> path)
    {
        var answers = new Answers(path);
        RunSession? session = null;
        try
        {
            session = RunSession.Load(pump, node, answers, answers, answers, opened: opened);
            while (answers.Next == null && !session.IsOver)
            {
                if (session.AtHorizon) throw new InvalidOperationException("a probe travelled: the map's moves leave the node");
                session.Step();
            }
            if (answers.Pending > 0) throw new InvalidOperationException($"a probe stopped with {answers.Pending} answers left: {string.Join(" ", path)}");
            return answers.Next ?? new Afterstate(session.View(), null, null, [], session.Won);
        }
        catch (Asked) when (answers.Next != null)
        {
            return answers.Next;
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>The next decision was asked and kept: the probe ends here.</summary>
    private sealed class Asked : Exception;

    /// <summary>
    /// Answers from the path, then keeps the first decision past it. The driver asks combat decisions, card prompts and
    /// most macro decisions with the game waiting, so there the probe stops by throwing <see cref="Asked"/>; a card reward
    /// and a bundle are asked from inside a game task, so they get an answer that is never played on (the first action).
    /// </summary>
    private sealed class Answers(IReadOnlyList<MacroAction> path) : ICombatPolicy, IChoicePolicy, IMacroPolicy
    {
        private readonly Queue<MacroAction> _left = new(path);

        public Afterstate? Next { get; private set; }

        public int Pending => _left.Count;

        public CombatAction Choose(CombatDecision decision)
        {
            if (_left.Count > 0) throw new InvalidOperationException($"a probe met a combat with {_left.Count} answers left");
            var obs = decision.RunObservation ?? throw new InvalidOperationException("a combat decision outside the run");
            Next ??= new(obs.Run, Surfaces.Combat, obs.Screen, []);
            throw new Asked();
        }

        public ChoiceAnswer Choose(ChoiceDecision decision)
        {
            if (Next == null && _left.Count > 0)
            {
                var step = _left.Dequeue();
                if (step.Verb != MacroAction.PickVerb) throw new InvalidOperationException($"a probe's {step} met a card prompt");
                return new(step.Picks);
            }
            var obs = decision.Observation ?? throw new InvalidOperationException("a card prompt outside the run");
            Next ??= new(obs.Run, Surfaces.CardChoice, obs.Screen, []);
            throw new Asked();
        }

        public MacroAnswer Choose(MacroDecision decision)
        {
            if (Next == null && _left.Count > 0)
            {
                var step = _left.Peek();
                if (step.Verb == MacroAction.PickVerb) throw new InvalidOperationException($"a probe's {step} met {decision.Surface}");
                return new(_left.Dequeue());
            }
            var obs = decision.Observation;
            Next ??= new(obs.Run, decision.Surface, obs.Screen, decision.Legal);
            if (decision.Surface is Surfaces.CardReward or Surfaces.Bundle) return new(decision.Legal[0]);
            throw new Asked();
        }
    }
}
