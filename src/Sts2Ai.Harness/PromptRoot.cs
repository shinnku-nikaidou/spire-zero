using System.Collections.Concurrent;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// The root of a plan at a card prompt. The engine takes a combat in only where the game stands between actions, so the
/// prompt, and any prompt its answers raise, is a state of the node's clone: each path of answers is played there once
/// (<see cref="Crossing"/>), and where the player can play again the combat is written out and is the engine's from then
/// on (<see cref="EngineCrossing"/>).
/// </summary>
public static class PromptRoot
{
    /// <summary>
    /// What stands after each path of answers from the plan's prompt, asked of the node's clone once a path and kept:
    /// the engine's search asks it from any thread.
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
    }
}
