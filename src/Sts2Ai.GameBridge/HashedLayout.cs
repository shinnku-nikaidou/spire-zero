using System.Collections.Concurrent;
using System.Reflection;

namespace Sts2Ai.GameBridge;

/// <summary>
/// Where the items of a .NET <see cref="HashSet{T}"/> or <see cref="Dictionary{TKey,TValue}"/> sit. Both keep their
/// items in an array of slots and enumerate it from slot 0 up. A removed item's slot is freed, and the next item added
/// takes the slot freed last, so a collection that has had items removed no longer enumerates in the order of adding:
/// what it will do next depends on its freed slots, which this reads from the runtime's own fields.
/// </summary>
public static class HashedLayout
{
    private sealed record Access(FieldInfo Entries, FieldInfo Count, FieldInfo FreeList, FieldInfo FreeCount, FieldInfo Next);

    private static readonly ConcurrentDictionary<Type, Access> Accesses = new();

    /// <summary>Whether <paramref name="type"/> is a hash set or a dictionary of the runtime's.</summary>
    public static bool Covers(Type type) =>
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(HashSet<>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

    /// <summary>
    /// The slots of a hash set or a dictionary that has freed slots: the slot of each item, in the order of
    /// enumeration, and the freed slots in the order adds will take them. Null for one with no freed slot: its items
    /// sit in slots 0, 1, 2, ... and the next add takes the slot after the last.
    /// </summary>
    /// <exception cref="InvalidOperationException">The collection's fields are not as this reads them: every slot in
    /// use must be an item's or a freed one, and none both.</exception>
    public static (int[] At, int[] Free)? Of(object collection)
    {
        var access = Accesses.GetOrAdd(collection.GetType(), Bind);
        int freeCount = (int)access.FreeCount.GetValue(collection)!;
        if (freeCount == 0) return null;
        var entries = (Array)access.Entries.GetValue(collection)!;
        int count = (int)access.Count.GetValue(collection)!;
        // An entry's next is the next of its bucket's chain, -1 at its end; a freed entry's is -3 minus the next freed slot, -2 at the end.
        var next = new int[count];
        for (int i = 0; i < count; i++) next[i] = (int)access.Next.GetValue(entries.GetValue(i))!;
        int[] at = [.. Enumerable.Range(0, count).Where(i => next[i] >= -1)];
        var free = new List<int>();
        for (int i = (int)access.FreeList.GetValue(collection)!; i >= 0 && free.Count <= count; i = -3 - next[i])
        {
            if (i >= count || next[i] >= -1) throw new InvalidOperationException($"slot {i} of a {collection.GetType().Name} is on its free list and holds an item");
            free.Add(i);
        }
        if (free.Count != freeCount || free.Distinct().Count() != free.Count || at.Length + free.Count != count)
            throw new InvalidOperationException(
                $"a {collection.GetType().Name} with {count} slots: {at.Length} hold items and {free.Count} are on the free list, which the runtime counts as {freeCount}");
        return (at, [.. free]);
    }

    private static Access Bind(Type type)
    {
        if (!Covers(type)) throw new InvalidOperationException($"{type} is neither a HashSet<T> nor a Dictionary<K, V>");
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => type.GetField(name, Private) ?? throw new InvalidOperationException($"{type} has no {name}: another runtime than the one this was written for");
        var entries = Field("_entries");
        var next = entries.FieldType.GetElementType()!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(f => f.Name.Equals("next", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"an entry of {type} has no next: another runtime than the one this was written for");
        return new(entries, Field("_count"), Field("_freeList"), Field("_freeCount"), next);
    }
}
