using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// The SL world of a plan rooted at a card prompt. The engine takes a combat in only where the game stands between
/// actions, so the prompt, and any prompt its answers raise, is a state of the node's clone: each path of answers is
/// played there once (<see cref="Crossing"/>), and where the player can play again the combat is written out and is the
/// engine's from then on. To search it is one world: at a prompt it shows what the clone shows and takes an answer, then
/// it is an <see cref="EngineWorld"/>.
/// </summary>
public sealed class PromptRoot(PromptRoot.Crossing crossing) : ICombatWorld, IDisposable
{
    /// <summary>
    /// What stands after each path of answers from the plan's prompt, asked of the node's clone once a path and kept:
    /// the worlds of one plan share it, from any thread.
    /// </summary>
    /// <param name="cross">Plays each path of answers on the node's clone and says what stands there, in the paths'
    /// order; asked from any thread.</param>
    public sealed class Crossing(Func<IReadOnlyList<IReadOnlyList<CombatAction>>, IReadOnlyList<PromptStep>> cross)
    {
        /// <summary>The paths asked of the clone at a time (<see cref="Ahead"/>): enough to keep a pool's workers busy,
        /// few enough that their replies, hundreds of kilobytes each, do not pile up while a prompt of a thousand
        /// answers is crossed.</summary>
        public const int Batch = 32;

        private readonly ConcurrentDictionary<string, Lazy<Place>> _places = new();
        private int _crossed;

        /// <summary>The paths played on the clone so far.</summary>
        public int Crossed => _crossed;

        private static string Key(IEnumerable<CombatAction> path) => string.Join(' ', path);

        internal Place At(IReadOnlyList<CombatAction> path) => _places.GetOrAdd(Key(path), _ => new(() =>
        {
            Interlocked.Increment(ref _crossed);
            return new Place(cross([path])[0]);
        })).Value;

        /// <summary>Plays these paths, <see cref="Batch"/> at a time, so that the clone's workers serve them side by
        /// side: the answers a search is about to try one after another.</summary>
        public void Ahead(IReadOnlyList<IReadOnlyList<CombatAction>> paths)
        {
            foreach (var asked in paths.Where(p => !_places.ContainsKey(Key(p))).Chunk(Batch))
            {
                Interlocked.Add(ref _crossed, asked.Length);
                var steps = cross(asked);
                for (int i = 0; i < asked.Length; i++)
                {
                    var place = new Place(steps[i]);
                    _places.TryAdd(Key(asked[i]), new(() => place));
                }
            }
        }
    }

    /// <summary>What stands after one path of answers: a prompt or the combat's end as the clone shows it, or the
    /// combat as the worker wrote it out for the engine.</summary>
    internal sealed class Place(PromptStep step)
    {
        /// <summary>The combat written out, as bytes: a plan crosses up to a thousand paths, and a world taken in for
        /// each would hold the engine's memory for the whole plan.</summary>
        private readonly byte[]? _export = step.Export == null ? null : Convert.FromBase64String(step.Export);

        /// <summary>The step as the clone showed it, without the combat written out.</summary>
        public PromptStep Step { get; } = step with { Export = null };

        /// <summary>The place for the engine's crossing (<see cref="EngineCrossing"/>).</summary>
        public CrossedPlace Crossed => new(Step.Status, Step.Observation, Step.Run, Step.HiddenKey, _export);

        /// <summary>A world of the engine's standing here, taken in for the caller, whose own it is; null where the
        /// engine has none (a prompt waits, or the combat is over).</summary>
        public EngineWorld? World() => _export == null ? null : EngineWorld.Import(_export, Step.Run);
    }

    /// <summary>An engine world this one has stood in, and how many stand on it: this world while it is there, and
    /// every snapshot taken there that is not forgotten.</summary>
    private sealed class Held(EngineWorld world)
    {
        public readonly EngineWorld World = world;
        public int Count = 1;
    }

    /// <summary>A point of this world: the answers given, and in the engine's part the engine's own snapshot.</summary>
    private sealed record Saved(IReadOnlyList<CombatAction> Path, Place Place, Held? At, WorldSnapshot? Inner);

    private readonly ConditionalWeakTable<WorldSnapshot, Saved> _snapshots = [];
    /// <summary>The engine worlds something still stands on, freed at the end (a snapshot never forgotten).</summary>
    private readonly HashSet<Held> _held = [];
    private IReadOnlyList<CombatAction> _path = [];
    private Place _place = crossing.At([]);
    private Held? _at;

    private EngineWorld? World => _at?.World;

    public CombatObservation Observe() => World?.Observe() ?? _place.Step.Observation;

    public RunView Run() => World?.Run() ?? _place.Step.Run;

    public IReadOnlyList<CombatAction> Legal() => World?.Legal() ?? [];

    public CombatStatus Status => World?.Status ?? _place.Step.Status;

    public ulong HiddenKey() => World?.HiddenKey() ?? _place.Step.HiddenKey;

    public ulong Hash(CombatObservation observation) => World?.Hash(observation) ?? ProtocolJson.Hash(observation);

    public ulong StateKey(CombatObservation observation) => World?.StateKey(observation) ?? ProtocolJson.StateKey(observation);

    public void Execute(CombatAction action)
    {
        if (World is { } world)
        {
            world.Execute(action);
            return;
        }
        if (_place.Step is not { Status: CombatStatus.InProgress, Observation.Prompt: { } prompt } || action.Kind != CombatActionKind.Pick || !prompt.Accepts(action.Picks))
            throw new InvalidOperationException($"{action} is no answer to the card prompt the combat waits on");
        _path = [.. _path, action];
        _place = crossing.At(_path);
        if (_place.World() is { } taken) _held.Add(_at = new(taken));
    }

    public WorldSnapshot Snapshot()
    {
        var snapshot = new WorldSnapshot([]);
        if (_at != null) _at.Count++;
        _snapshots.Add(snapshot, new(_path, _place, _at, World?.Snapshot()));
        return snapshot;
    }

    public void Restore(WorldSnapshot snapshot)
    {
        var saved = Find(snapshot);
        if (saved.At != _at)
        {
            if (saved.At != null) saved.At.Count++;
            Leave(_at);
            _at = saved.At;
        }
        (_path, _place) = (saved.Path, saved.Place);
        if (saved.Inner != null) World!.Restore(saved.Inner);
    }

    public void Forget(WorldSnapshot snapshot)
    {
        var saved = Find(snapshot);
        _snapshots.Remove(snapshot);
        if (saved.Inner != null) saved.At!.World.Forget(saved.Inner);
        Leave(saved.At);
    }

    private Saved Find(WorldSnapshot snapshot) => _snapshots.TryGetValue(snapshot, out var saved) ? saved
        : throw new InvalidOperationException("the snapshot is not one this world took, or it was forgotten");

    /// <summary>One fewer stands on the engine world: with none left, it is freed, so that a search over a prompt's
    /// answers holds the worlds of the answers it is in, not of every answer it has tried.</summary>
    private void Leave(Held? held)
    {
        if (held == null || --held.Count > 0) return;
        held.World.Dispose();
        _held.Remove(held);
    }

    /// <summary>Frees the engine's worlds something still stands on.</summary>
    public void Dispose()
    {
        foreach (var held in _held) held.World.Dispose();
        _held.Clear();
        _at = null;
    }
}
