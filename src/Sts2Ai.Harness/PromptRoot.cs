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
    public sealed class Crossing(Func<IReadOnlyList<IReadOnlyList<CombatAction>>, IReadOnlyList<PromptStep>> cross) : IDisposable
    {
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

        /// <summary>Plays these paths at once, so that the clone's workers serve them side by side: the answers a
        /// search is about to try one after another.</summary>
        public void Ahead(IReadOnlyList<IReadOnlyList<CombatAction>> paths)
        {
            var asked = paths.Where(p => !_places.ContainsKey(Key(p))).ToList();
            if (asked.Count == 0) return;
            Interlocked.Add(ref _crossed, asked.Count);
            var steps = cross(asked);
            for (int i = 0; i < asked.Count; i++)
            {
                var place = new Place(steps[i]);
                _places.TryAdd(Key(asked[i]), new(() => place));
            }
        }

        public void Dispose()
        {
            foreach (var place in _places.Values)
                if (place.IsValueCreated) place.Value.Dispose();
        }
    }

    /// <summary>What stands after one path of answers: a prompt or the combat's end as the clone shows it, or the
    /// combat as the engine took it in.</summary>
    internal sealed class Place(PromptStep step) : IDisposable
    {
        private EngineWorld? _taken;

        public PromptStep Step => step;

        /// <summary>A world of the engine's standing here, the caller's own; null where the engine has none (a prompt
        /// waits, or the combat is over).</summary>
        public EngineWorld? World()
        {
            if (step.Export == null) return null;
            // One world from one thread at a time: the world taken in is copied under the lock.
            lock (this) return (_taken ??= EngineWorld.Import(Convert.FromBase64String(step.Export), step.Run)).Clone();
        }

        public void Dispose() => _taken?.Dispose();
    }

    /// <summary>A point of this world: the answers given, and in the engine's part the engine's own snapshot.</summary>
    private sealed record Saved(IReadOnlyList<CombatAction> Path, Place Place, EngineWorld? World, WorldSnapshot? Inner);

    private readonly ConditionalWeakTable<WorldSnapshot, Saved> _snapshots = [];
    /// <summary>Every engine world this one has stood in: a snapshot may bring any of them back.</summary>
    private readonly List<EngineWorld> _made = [];
    private IReadOnlyList<CombatAction> _path = [];
    private Place _place = crossing.At([]);
    private EngineWorld? _world;

    public CombatObservation Observe() => _world?.Observe() ?? _place.Step.Observation;

    public RunView Run() => _world?.Run() ?? _place.Step.Run;

    public IReadOnlyList<CombatAction> Legal() => _world?.Legal() ?? [];

    public CombatStatus Status => _world?.Status ?? _place.Step.Status;

    public ulong HiddenKey() => _world?.HiddenKey() ?? _place.Step.HiddenKey;

    public void Execute(CombatAction action)
    {
        if (_world != null)
        {
            _world.Execute(action);
            return;
        }
        if (_place.Step is not { Status: CombatStatus.InProgress, Observation.Prompt: { } prompt } || action.Kind != CombatActionKind.Pick || !prompt.Accepts(action.Picks))
            throw new InvalidOperationException($"{action} is no answer to the card prompt the combat waits on");
        _path = [.. _path, action];
        _place = crossing.At(_path);
        if ((_world = _place.World()) != null) _made.Add(_world);
    }

    public WorldSnapshot Snapshot()
    {
        var snapshot = new WorldSnapshot([]);
        _snapshots.Add(snapshot, new(_path, _place, _world, _world?.Snapshot()));
        return snapshot;
    }

    public void Restore(WorldSnapshot snapshot)
    {
        if (!_snapshots.TryGetValue(snapshot, out var saved)) throw new InvalidOperationException("the snapshot is not one this world took");
        (_path, _place, _world) = (saved.Path, saved.Place, saved.World);
        if (saved.Inner != null) _world!.Restore(saved.Inner);
    }

    /// <summary>Frees the engine's worlds this one stood in.</summary>
    public void Dispose()
    {
        foreach (var world in _made) world.Dispose();
        _made.Clear();
        _world = null;
    }
}
