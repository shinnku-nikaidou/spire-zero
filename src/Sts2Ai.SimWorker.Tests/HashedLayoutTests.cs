using Sts2Ai.GameBridge;

namespace Sts2Ai.SimWorker.Tests;

/// <summary>Where .NET's own hash set and dictionary keep their items, as the export writes it once slots are freed.</summary>
public class HashedLayoutTests
{
    [Fact]
    public void ACollectionWithNoFreedSlotHasNoLayoutToSay()
    {
        Assert.Null(HashedLayout.Of(new HashSet<int> { 5, 3, 9 }));
        var cleared = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        cleared.Remove("a");
        cleared.Clear();
        Assert.Null(HashedLayout.Of(cleared));
    }

    [Fact]
    public void FreedSlotsAreTakenLastFreedFirstAndItemsEnumerateBySlot()
    {
        var set = new HashSet<int> { 10, 11, 12, 13 };
        set.Remove(11);
        set.Remove(12);
        var (at, free) = HashedLayout.Of(set)!.Value;
        Assert.Equal([0, 3], at);
        Assert.Equal([2, 1], free);
        // The layout says what the next adds do: 20 takes slot 2 and 21 slot 1, so 21 comes before 20.
        set.Add(20);
        set.Add(21);
        Assert.Equal([10, 21, 20, 13], set);
        Assert.Null(HashedLayout.Of(set));
    }

    [Fact]
    public void ADictionaryIsReadTheSameWay()
    {
        var dict = new Dictionary<int, string> { [1] = "a", [2] = "b", [3] = "c" };
        dict.Remove(1);
        var (at, free) = HashedLayout.Of(dict)!.Value;
        Assert.Equal([1, 2], at);
        Assert.Equal([0], free);
        dict.Remove(3);
        dict.Remove(2);
        (at, free) = HashedLayout.Of(dict)!.Value;
        Assert.Empty(at);
        Assert.Equal([1, 2, 0], free);
    }

    [Fact]
    public void AnotherCollectionIsRefused() => Assert.Throws<InvalidOperationException>(() => HashedLayout.Of(new SortedSet<int> { 1 }));
}
