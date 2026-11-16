using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Players;
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

/// <summary>Makes the second worlds of the lockstep check over a whole run, as <see cref="ISecondWorlds"/> of a combat.</summary>
internal interface IRunWorlds
{
    /// <summary>As <see cref="ISecondWorlds.At"/>, at a decision of a combat of the real run.</summary>
    Func<SecondWorld>? At(RunSession real);
}

/// <summary>
/// The SL world of a combat decision in a run (<see cref="NodeWorld"/>): the node's clone, loaded and replayed. It is
/// what search plays on today, and must agree with the run everywhere, at a card prompt too.
/// </summary>
internal sealed class NodeWorlds(Pump pump) : IRunWorlds
{
    public Func<SecondWorld>? At(RunSession real)
    {
        var clone = real.Clone();
        return () => new Node(new NodeWorld(pump, clone));
    }

    private sealed class Node(NodeWorld world) : SecondWorld
    {
        public override ICombatWorld World => world;

        public override void Dispose() => world.Dispose();
    }
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

/// <summary>What a combat's exports say of themselves, added up (<see cref="EngineExport.Written"/>).</summary>
internal sealed class ExportTally
{
    /// <summary>The references to objects of classes outside the engine's schema, by the class and the field that holds
    /// the reference: state the engine was not given.</summary>
    public Dictionary<string, int> Outside { get; } = [];

    /// <summary>The hash sets and dictionaries with freed slots, by the field that holds them.</summary>
    public Dictionary<string, int> Freed { get; } = [];

    /// <summary>The exports that hold at least one such collection.</summary>
    public int FreedExports { get; private set; }

    public void Add(EngineExport.Written written)
    {
        foreach (var (type, references) in written.Outside) Outside[type] = Outside.GetValueOrDefault(type) + references;
        foreach (var (field, collections) in written.Freed) Freed[field] = Freed.GetValueOrDefault(field) + collections;
        if (written.Freed.Count > 0) FreedExports++;
    }
}

/// <summary>No second world: the real combats are only played, for what a dump keeps of them (<see cref="CombatDump"/>).</summary>
internal sealed class NoWorlds : ISecondWorlds, IRunWorlds
{
    public Func<SecondWorld>? At(CombatSession real) => null;

    public Func<SecondWorld>? At(RunSession real) => null;
}

/// <summary>
/// The engine's world (engine/DESIGN.md): at a decision the live combat is written out along the engine's schema
/// (<see cref="EngineExport"/>), and the world is the engine's import of those bytes. The engine takes a combat in
/// between actions only, so none is made at a card prompt. One of these serves one combat: it counts what it wrote.
/// </summary>
internal sealed class EngineWorlds(EngineExport export) : ISecondWorlds, IRunWorlds
{
    /// <summary>The exports written, their bytes in all, and the time they took.</summary>
    public (int Count, long Bytes, double Ms) Written { get; private set; }

    /// <summary>What the exports say of themselves, over all of them.</summary>
    public ExportTally Tally { get; } = new();

    public Func<SecondWorld>? At(CombatSession real) => real.Prompt != null ? null : Make(SecondWorld.Alone(real), real.Player);

    /// <summary>In a run the world is given the run as it stands, which its own <see cref="ICombatWorld.Run"/> then
    /// carries the combat's changes over.</summary>
    public Func<SecondWorld>? At(RunSession real) => real.Prompt != null ? null : Make(real.View(previews: false), real.Player);

    private Func<SecondWorld> Make(RunView run, Player player)
    {
        long started = Stopwatch.GetTimestamp();
        var written = export.Write(HeadlessGame.GameVersion, PromptSelector.Around());
        byte[] bytes = written.Bytes;
        Written = (Written.Count + 1, Written.Bytes + bytes.Length, Written.Ms + Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Tally.Add(written);
        return () => new Imported(EngineWorld.Import(bytes, run));
    }

    private sealed class Imported(EngineWorld world) : SecondWorld
    {
        public override ICombatWorld World => world;

        public override string Observation() => world.ObservationJson();

        public override void Dispose() => world.Dispose();
    }
}
