using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

/// <summary>
/// A world the lockstep check holds against the real game: an <see cref="ICombatWorld"/>, with its observation as the
/// JSON it would send. A world of typed observations sends its own, serialized; a world that writes the JSON itself
/// (the engine) gives those bytes, so that nothing read back and written again hides a difference.
/// </summary>
internal abstract class SecondWorld : IDisposable
{
    public abstract ICombatWorld World { get; }

    public virtual string Observation() => ProtocolJson.Serialize(World.Observe());

    public abstract void Dispose();
}

/// <summary>Makes the second worlds of the lockstep check: the one place where one kind of world differs from another.</summary>
internal interface ISecondWorlds
{
    /// <summary>
    /// Called at a decision of the real combat, while it is live: takes from it what a world of this kind is made of,
    /// and returns what makes that world later, standing at this decision, once the real combat is gone. Null where
    /// this kind of world cannot be made from a combat: the engine's in the middle of an action, at a card prompt.
    /// </summary>
    Func<SecondWorld>? At(CombatSession real);
}

/// <summary>
/// The SL world that replays the combat (<see cref="SimWorlds.SL"/>): the simulator's own second world, which must agree
/// with the game everywhere. The lockstep check is tested against it.
///
/// With <paramref name="determinized"/>, what the observation hides is drawn again in each world as it is made
/// (<see cref="CombatSession.Determinize"/>): a world that shows the right observation and has the wrong hidden state.
/// The check must find no import difference in it and many after actions, which tests the check from the other side.
/// Like the engine's world, it cannot be made in the middle of an action.
/// </summary>
internal sealed class ReplayWorlds(Pump pump, bool determinized = false) : ISecondWorlds
{
    public Func<SecondWorld>? At(CombatSession real)
    {
        var snapshot = real.Snapshot();
        if (determinized)
        {
            if (real.Prompt != null) return null;
            snapshot = snapshot with { Trace = [.. snapshot.Trace, new TraceEvent(TraceKind.Determinize, Seed: (ulong)snapshot.Trace.Count)] };
        }
        var run = Alone(real);
        return () => new Replay(SimWorlds.SL(pump, snapshot, run));
    }

    /// <summary>A run for a combat started on its own, which has none: nothing in the check reads it.</summary>
    private static RunView Alone(CombatSession real) => new()
    {
        Character = real.Player.Character.Id.Entry,
        Ascension = real.Run.AscensionLevel,
        Act = real.Run.CurrentActIndex,
        ActId = "<none>",
        Floor = 1,
        Hp = real.Player.Creature.CurrentHp,
        MaxHp = real.Player.Creature.MaxHp,
        Gold = real.Player.Gold,
        Deck = [],
        Relics = [],
        Potions = [],
        Map = new MapView { Points = [], Visited = [], Boss = "<none>" },
    };

    private sealed class Replay(SimWorlds source) : SecondWorld
    {
        public override ICombatWorld World { get; } = source.Worlds(1, 0)[0];

        public override void Dispose() => source.Dispose();
    }
}
