using System.Diagnostics;
using Sts2Ai.Engine;
using Sts2Ai.GameBridge;
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

    /// <summary>A run for a combat started on its own, which has none: nothing in the check reads it.</summary>
    public static RunView Alone(CombatSession real) => new()
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
        var run = SecondWorld.Alone(real);
        return () => new Replay(SimWorlds.SL(pump, snapshot, run));
    }

    private sealed class Replay(SimWorlds source) : SecondWorld
    {
        public override ICombatWorld World { get; } = source.Worlds(1, 0)[0];

        public override void Dispose() => source.Dispose();
    }
}

/// <summary>No second world: the real combats are only played, for what a dump keeps of them (<see cref="CombatDump"/>).</summary>
internal sealed class NoWorlds : ISecondWorlds
{
    public Func<SecondWorld>? At(CombatSession real) => null;
}

/// <summary>
/// The engine's world (engine/DESIGN.md): at a decision the live combat is written out along the engine's schema
/// (<see cref="EngineExport"/>), and the world is the engine's import of those bytes. The engine takes a combat in
/// between actions only, so none is made at a card prompt. One of these serves one combat: it counts what it wrote.
/// </summary>
internal sealed class EngineWorlds(EngineExport export) : ISecondWorlds
{
    /// <summary>The exports written, their bytes in all, and the time they took.</summary>
    public (int Count, long Bytes, double Ms) Written { get; private set; }

    /// <summary>The classes outside the engine's schema that the combat's objects pointed to, with the references to
    /// each over all the exports: state the engine was not given.</summary>
    public Dictionary<string, int> Outside { get; } = [];

    public Func<SecondWorld>? At(CombatSession real)
    {
        if (real.Prompt != null) return null;
        long started = Stopwatch.GetTimestamp();
        var written = export.Write(HeadlessGame.GameVersion);
        byte[] bytes = written.Bytes;
        Written = (Written.Count + 1, Written.Bytes + bytes.Length, Written.Ms + Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        foreach (var (type, references) in written.Outside) Outside[type] = Outside.GetValueOrDefault(type) + references;
        var run = SecondWorld.Alone(real);
        return () => new Imported(EngineWorld.Import(bytes, run));
    }

    private sealed class Imported(EngineWorld world) : SecondWorld
    {
        public override ICombatWorld World => world;

        public override string Observation() => world.ObservationJson();

        public override void Dispose() => world.Dispose();
    }
}
