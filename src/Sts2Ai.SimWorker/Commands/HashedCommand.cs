using System.CommandLine;
using System.Runtime.InteropServices;
using Sts2Ai.GameBridge;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// hashed: what .NET's own <see cref="HashSet{T}"/> and <see cref="Dictionary{TKey,TValue}"/> do under seeded sequences
/// of adds, removes, clears and the bulk operations, written as text: after every operation the items in the order of
/// enumeration, the slot of each, and the freed slots in the order adds will take them (<see cref="HashedLayout"/>).
/// The engine's own set and dictionary must enumerate as the game's do, and are held against this file: against the
/// runtime itself, not against a reading of it. It needs no game.
/// </summary>
internal static class HashedCommand
{
    private static readonly Option<string> Out = Opt.Required<string>("--out", "the file to write");
    private static readonly Option<int> Sequences = Opt.Of("--sequences", 1500, "sequences of each kind, a set's and a dictionary's");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base");

    public static Command Command()
    {
        var command = Opt.Command("hashed", "what .NET's HashSet and Dictionary do under seeded operations, as text: for the engine's own collections to be held against",
            [Out, Sequences, Seed]);
        command.SetAction(Run);
        return command;
    }

    private static int Run(ParseResult r)
    {
        int sequences = r.GetValue(Sequences);
        using var file = new StreamWriter(r.GetValue(Out)!);
        file.WriteLine($"""
            # {RuntimeInformation.FrameworkDescription}: HashSet<int> and Dictionary<int, int> under seeded operations.
            # Made by: sim hashed --sequences {sequences} --seed {r.GetValue(Seed)}
            #
            # A sequence starts with "set N KIND" or "dict N KIND" and an empty collection. Then two lines a step: the
            # operation, with "-> RESULT" where it returns one, and the collection after it:
            #   = ITEMS | AT | FREE
            # ITEMS in the order of enumeration (a dictionary's as KEY:VALUE), AT the slot of each item, FREE the freed
            # slots in the order adds will take them, the next one first. The rule the file shows: an add takes FREE's
            # first slot, or with none freed the slot after the highest ever used; a remove puts its slot first in
            # FREE; enumeration goes by slot; clear forgets every slot.
            #
            # A set's operations: add K -> BOOL; remove K -> BOOL; clear; and in a "bulk" sequence
            # remove_where M R -> COUNT (every K with K % M == R), union K.., except K.., intersect K.. (UnionWith,
            # ExceptWith and IntersectWith of that list; they add and remove one item at a time, a remove_where and
            # an intersect by rising slot, the others in the list's order; an intersect with no K is a clear, as
            # .NET makes it). A dictionary's: add K V -> BOOL (TryAdd), remove K -> BOOL,
            # clear; and in a "bulk" sequence put K V (the indexer: a key it holds keeps its slot). A "basic" sequence
            # has adds, removes and clears only. Not here: TrimExcess and EnsureCapacity.
            """);
        for (int n = 0; n < sequences; n++) Set(file, n, new Random(unchecked(r.GetValue(Seed) * 1_000_003 + n)), bulk: n >= sequences / 2);
        for (int n = 0; n < sequences; n++) Dict(file, n, new Random(unchecked(r.GetValue(Seed) * 2_000_003 + n)), bulk: n >= sequences / 2);
        return 0;
    }

    /// <summary>How long a sequence is and how many keys it draws from: mostly few keys, so that adds and removes meet;
    /// one in twenty long and wide, so that the collection grows several times with freed slots in it.</summary>
    private static (int Steps, int Keys) Size(int n, Random rng) => n % 20 == 19 ? (100, 16 + rng.Next(48)) : (20 + rng.Next(50), 3 + rng.Next(20));

    private static void Set(TextWriter file, int n, Random rng, bool bulk)
    {
        var (steps, keys) = Size(n, rng);
        var set = new HashSet<int>();
        file.WriteLine($"set {n} {(bulk ? "bulk" : "basic")}");
        for (int step = 0; step < steps; step++)
        {
            int roll = rng.Next(100);
            int[] Some() => [.. Enumerable.Range(0, rng.Next(1, 6)).Select(_ => rng.Next(keys))];
            if (roll < 3)
            {
                set.Clear();
                file.WriteLine("clear");
            }
            else if (bulk && roll < 7)
            {
                var (m, rest) = (2 + rng.Next(3), 0);
                rest = rng.Next(m);
                file.WriteLine($"remove_where {m} {rest} -> {set.RemoveWhere(k => k % m == rest)}");
            }
            else if (bulk && roll < 11)
            {
                var other = Some();
                set.UnionWith(other);
                file.WriteLine($"union {string.Join(' ', other)}");
            }
            else if (bulk && roll < 15)
            {
                var other = Some();
                set.ExceptWith(other);
                file.WriteLine($"except {string.Join(' ', other)}");
            }
            else if (bulk && roll < 18)
            {
                int[] other = [.. Enumerable.Range(0, keys).Where(_ => rng.Next(4) > 0)];
                set.IntersectWith(other);
                file.WriteLine($"intersect {string.Join(' ', other)}");
            }
            else if (roll < 60)
            {
                int key = rng.Next(keys);
                file.WriteLine($"add {key} -> {Bool(set.Add(key))}");
            }
            else
            {
                int key = Held(set, set.Count, rng) ?? rng.Next(keys);
                file.WriteLine($"remove {key} -> {Bool(set.Remove(key))}");
            }
            file.WriteLine(State(set, set.Select(k => k.ToString()), set.Count));
        }
    }

    private static void Dict(TextWriter file, int n, Random rng, bool bulk)
    {
        var (steps, keys) = Size(n, rng);
        var dict = new Dictionary<int, int>();
        file.WriteLine($"dict {n} {(bulk ? "bulk" : "basic")}");
        for (int step = 0; step < steps; step++)
        {
            int roll = rng.Next(100);
            if (roll < 3)
            {
                dict.Clear();
                file.WriteLine("clear");
            }
            else if (bulk && roll < 18)
            {
                int key = rng.Next(keys);
                dict[key] = step;
                file.WriteLine($"put {key} {step}");
            }
            else if (roll < 60)
            {
                int key = rng.Next(keys);
                file.WriteLine($"add {key} {step} -> {Bool(dict.TryAdd(key, step))}");
            }
            else
            {
                int key = Held(dict.Keys, dict.Count, rng) ?? rng.Next(keys);
                file.WriteLine($"remove {key} -> {Bool(dict.Remove(key))}");
            }
            file.WriteLine(State(dict, dict.Select(kv => $"{kv.Key}:{kv.Value}"), dict.Count));
        }
    }

    /// <summary>Four times in five a key the collection holds, so that most removes remove something.</summary>
    private static int? Held(IEnumerable<int> keys, int count, Random rng) => count > 0 && rng.Next(5) > 0 ? keys.ElementAt(rng.Next(count)) : null;

    private static string State(object collection, IEnumerable<string> items, int count)
    {
        var (at, free) = HashedLayout.Of(collection) ?? ([.. Enumerable.Range(0, count)], []);
        return $"= {string.Join(' ', items)} | {string.Join(' ', at)} | {string.Join(' ', free)}";
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
