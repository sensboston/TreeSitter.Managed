using Xunit;

namespace TreeSitter.Tests.Runtime;

// Expected sizes and capacities follow lib/src/array.h of tree-sitter v0.27.2 (3fb7be4).
public class TsArrayTests
{
    private readonly record struct Entry(ushort Symbol, int Tag);

    private static readonly ElementComparison<int, int> CompareInts =
        static (element, needle) => element.CompareTo(needle);

    private static readonly Func<Entry, int> EntrySymbol = static entry => entry.Symbol;

    private static int[] Values(TsArray<int> array) => array.AsSpan().ToArray();

    private static void AssertSearch(TsArray<int> array, int needle, uint expectedIndex, bool expectedExists)
    {
        array.SearchSortedWith(CompareInts, needle, out uint index, out bool exists);
        Assert.Equal(expectedIndex, index);
        Assert.Equal(expectedExists, exists);
    }

    [Fact]
    public void DefaultArrayIsEmptyWithNoStorage()
    {
        TsArray<int> array = default;
        Assert.Equal(0u, array.Size);
        Assert.Equal(0u, array.Capacity);
        Assert.Null(array.Contents);
    }

    [Fact]
    public void InitAndDeleteDropTheStorage()
    {
        TsArray<int> array = default;
        array.Push(1);
        array.Init();
        Assert.Equal(0u, array.Size);
        Assert.Equal(0u, array.Capacity);
        Assert.Null(array.Contents);

        array.Push(2);
        array.Delete();
        Assert.Equal(0u, array.Size);
        Assert.Equal(0u, array.Capacity);
        Assert.Null(array.Contents);
    }

    [Fact]
    public void PushMatchesGrowRulesAtEveryStep()
    {
        // _array__grow (array.h, lines 249-259):
        //   new_size = size + count;
        //   if (new_size > capacity) {
        //     new_capacity = capacity * 2;
        //     if (new_capacity < 8) new_capacity = 8;
        //     if (new_capacity < new_size) new_capacity = new_size;
        //     reserve(new_capacity);
        //   }
        TsArray<uint> array = default;
        uint expectedCapacity = 0;
        for (uint value = 0; value <= 1000; value++)
        {
            array.Push(value);

            uint newSize = value + 1;
            if (newSize > expectedCapacity)
            {
                uint newCapacity = expectedCapacity * 2;
                if (newCapacity < 8)
                {
                    newCapacity = 8;
                }
                if (newCapacity < newSize)
                {
                    newCapacity = newSize;
                }
                expectedCapacity = newCapacity;
            }

            Assert.Equal(newSize, array.Size);
            Assert.Equal(expectedCapacity, array.Capacity);
            Assert.Equal(value, array.Get(value));
        }

        // Capacity steps 8, 16, 32, ..., 512, 1024. After 1001 pushes it is 1024.
        Assert.Equal(1024u, array.Capacity);
    }

    [Fact]
    public void ReserveSetsExactCapacityAndNeverShrinks()
    {
        // _array__reserve (array.h, lines 213-225): only grows, to exactly new_capacity.
        TsArray<int> array = default;
        array.Reserve(10);
        Assert.Equal(10u, array.Capacity);
        Assert.Equal(0u, array.Size);

        object? storage = array.Contents;
        array.Reserve(4);
        Assert.Equal(10u, array.Capacity);
        Assert.Same(storage, array.Contents);

        array.Reserve(11);
        Assert.Equal(11u, array.Capacity);
    }

    [Fact]
    public void GrowByZeroDoesNothing()
    {
        TsArray<int> array = default;
        array.GrowBy(0);
        Assert.Equal(0u, array.Size);
        Assert.Equal(0u, array.Capacity);
        Assert.Null(array.Contents);
    }

    [Fact]
    public void GrowByAppendsZeroedElementsAndUsesGrowRules()
    {
        TsArray<int> array = default;
        array.Push(1);
        array.Push(2);
        array.Push(3);
        array.Clear();

        // The old values are still in the storage: GrowBy must zero them.
        array.GrowBy(2);
        Assert.Equal(new[] { 0, 0 }, Values(array));
        Assert.Equal(8u, array.Capacity);

        // _array__grow: 2 + 10 > 8, so the capacity doubles to 16.
        array.GrowBy(10);
        Assert.Equal(12u, array.Size);
        Assert.Equal(16u, array.Capacity);
        Assert.Equal(new int[12], Values(array));
    }

    [Fact]
    public void GrowByLargerThanDoubleTheCapacityUsesTheNewSize()
    {
        // _array__grow (array.h, lines 249-259): the doubled capacity (0 -> 8 minimum) is
        // smaller than the new size (20), so the capacity becomes exactly 20.
        TsArray<int> array = default;
        array.GrowBy(20);
        Assert.Equal(20u, array.Size);
        Assert.Equal(20u, array.Capacity);
        Assert.Equal(new int[20], Values(array));
    }

    [Fact]
    public void FrontBackGetAndPopWorkOnTheElements()
    {
        TsArray<int> array = default;
        array.Push(10);
        array.Push(20);
        array.Push(30);
        Assert.Equal(10, array.Front());
        Assert.Equal(30, array.Back());
        Assert.Equal(20, array.Get(1));

        array.Front() = 11;
        array.Back() = 31;
        Assert.Equal(new[] { 11, 20, 31 }, Values(array));

        Assert.Equal(31, array.Pop());
        Assert.Equal(2u, array.Size);
        Assert.Equal(new[] { 11, 20 }, Values(array));
    }

    [Fact]
    public void ClearKeepsTheStorage()
    {
        TsArray<int> array = default;
        array.Extend(new[] { 1, 2 });
        object? storage = array.Contents;
        array.Clear();
        Assert.Equal(0u, array.Size);
        Assert.Equal(2u, array.Capacity);
        Assert.Same(storage, array.Contents);
    }

    [Fact]
    public void ExtendAppendsItemsAndPushAllAppendsAnotherArray()
    {
        TsArray<int> array = default;
        array.Extend(new[] { 1, 2 });
        Assert.Equal(new[] { 1, 2 }, Values(array));
        Assert.Equal(2u, array.Capacity);

        // _array__splice reserves the exact new size.
        TsArray<int> other = default;
        other.Extend(new[] { 3, 4, 5 });
        array.PushAll(other);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Values(array));
        Assert.Equal(5u, array.Capacity);
    }

    [Fact]
    public void ExtendWithNothingLeavesTheArrayEmpty()
    {
        TsArray<int> array = default;
        array.Extend(ReadOnlySpan<int>.Empty);
        Assert.Equal(0u, array.Size);
        Assert.Null(array.Contents);
    }

    [Fact]
    public void SpliceReplacesMiddleRangeAndShiftsTheTail()
    {
        TsArray<int> array = default;
        array.Extend(new[] { 1, 2, 3, 4, 5 });
        array.Splice(1, 2, 3, new[] { 10, 11, 12 });
        Assert.Equal(new[] { 1, 10, 11, 12, 4, 5 }, Values(array));
        Assert.Equal(6u, array.Size);
        Assert.Equal(6u, array.Capacity);
    }

    [Fact]
    public void SpliceWorksAtTheBeginningAndAtTheEnd()
    {
        TsArray<int> array = default;
        array.Extend(new[] { 1, 2, 3 });

        array.Splice(0, 0, 1, new[] { 0 });
        Assert.Equal(new[] { 0, 1, 2, 3 }, Values(array));

        array.Splice(4, 0, 2, new[] { 8, 9 });
        Assert.Equal(new[] { 0, 1, 2, 3, 8, 9 }, Values(array));

        // Remove the first three elements, insert nothing.
        array.Splice(0, 3, 0, ReadOnlySpan<int>.Empty);
        Assert.Equal(new[] { 3, 8, 9 }, Values(array));
    }

    [Fact]
    public void SpliceWithoutItemsInsertsZeros()
    {
        TsArray<int> array = default;
        array.Extend(new[] { 1, 2, 3 });
        array.Splice(1, 0, 2, ReadOnlySpan<int>.Empty);
        Assert.Equal(new[] { 1, 0, 0, 2, 3 }, Values(array));
    }

    [Fact]
    public void InsertPlacesElementAndGrowsCapacityByExactlyOne()
    {
        // _array__splice reserves size + 1 each time, so the capacity equals the size here.
        TsArray<int> array = default;
        array.Insert(0, 7);
        array.Insert(1, 9);
        array.Insert(1, 8);
        Assert.Equal(new[] { 7, 8, 9 }, Values(array));
        Assert.Equal(3u, array.Size);
        Assert.Equal(3u, array.Capacity);
    }

    [Fact]
    public void EraseRemovesElementAndKeepsTheOrder()
    {
        TsArray<int> array = default;
        array.Extend(new[] { 1, 2, 3, 4 });

        array.Erase(0);
        Assert.Equal(new[] { 2, 3, 4 }, Values(array));

        array.Erase(1);
        Assert.Equal(new[] { 2, 4 }, Values(array));

        array.Erase(1);
        Assert.Equal(new[] { 2 }, Values(array));
        Assert.Equal(1u, array.Size);
    }

    [Fact]
    public void SwapExchangesContentsSizesAndCapacities()
    {
        TsArray<int> a = default;
        a.Extend(new[] { 1, 2 });
        TsArray<int> b = default;
        b.Extend(new[] { 3, 4, 5 });

        a.Swap(ref b);

        Assert.Equal(new[] { 3, 4, 5 }, Values(a));
        Assert.Equal(3u, a.Capacity);
        Assert.Equal(new[] { 1, 2 }, Values(b));
        Assert.Equal(2u, b.Capacity);
    }

    [Fact]
    public void AssignCopiesItemsAndOnlyGrowsTheStorage()
    {
        TsArray<int> array = default;
        array.Assign(new[] { 1, 2, 3 });
        Assert.Equal(new[] { 1, 2, 3 }, Values(array));
        Assert.Equal(3u, array.Capacity);

        object? storage = array.Contents;
        array.Assign(new[] { 4 });
        Assert.Equal(new[] { 4 }, Values(array));
        Assert.Equal(3u, array.Capacity);
        Assert.Same(storage, array.Contents);
    }

    [Fact]
    public void AssignEmptyOnDefaultArrayLeavesNoStorage()
    {
        TsArray<int> array = default;
        array.Assign(ReadOnlySpan<int>.Empty);
        Assert.Equal(0u, array.Size);
        Assert.Null(array.Contents);
    }

    [Fact]
    public void SearchSortedWithFindsTheLastEqualElementOrTheInsertionPoint()
    {
        // Duplicates: 2 is at indices 1 to 3. The C loop stops at the last one (index 3).
        TsArray<int> array = default;
        array.Extend(new[] { 1, 2, 2, 2, 3 });
        AssertSearch(array, 0, 0, false);
        AssertSearch(array, 1, 0, true);
        AssertSearch(array, 2, 3, true);
        AssertSearch(array, 3, 4, true);
        AssertSearch(array, 4, 5, false);
    }

    [Fact]
    public void SearchSortedWithOnEmptyArrayReturnsZeroAndNotFound()
    {
        TsArray<int> array = default;
        AssertSearch(array, 5, 0, false);
    }

    [Fact]
    public void SearchSortedByUsesTheKeyAndTheSameRules()
    {
        TsArray<Entry> array = default;
        array.Extend(new[] { new Entry(3, 0), new Entry(7, 1), new Entry(7, 2), new Entry(9, 3) });

        array.SearchSortedBy(EntrySymbol, 7, out uint index, out bool exists);
        Assert.Equal(2u, index);
        Assert.True(exists);

        array.SearchSortedBy(EntrySymbol, 8, out index, out exists);
        Assert.Equal(3u, index);
        Assert.False(exists);

        array.SearchSortedBy(EntrySymbol, 2, out index, out exists);
        Assert.Equal(0u, index);
        Assert.False(exists);

        array.SearchSortedBy(EntrySymbol, 10, out index, out exists);
        Assert.Equal(4u, index);
        Assert.False(exists);
    }

    [Fact]
    public void SearchSortedByOnEmptyArrayReturnsZeroAndNotFound()
    {
        TsArray<Entry> array = default;
        array.SearchSortedBy(EntrySymbol, 5, out uint index, out bool exists);
        Assert.Equal(0u, index);
        Assert.False(exists);
    }

    [Fact]
    public void InsertSortedWithKeepsOrderAndSkipsEqualElements()
    {
        TsArray<int> array = default;
        array.Extend(new[] { 1, 3, 5 });
        array.InsertSortedWith(CompareInts, 4);
        array.InsertSortedWith(CompareInts, 0);
        array.InsertSortedWith(CompareInts, 9);
        array.InsertSortedWith(CompareInts, 3);
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 9 }, Values(array));
    }

    [Fact]
    public void InsertSortedByKeepsOrderAndSkipsEqualKeys()
    {
        TsArray<Entry> array = default;
        array.Extend(new[] { new Entry(3, 0), new Entry(9, 1) });
        array.InsertSortedBy(EntrySymbol, new Entry(7, 2));
        array.InsertSortedBy(EntrySymbol, new Entry(7, 3));
        array.InsertSortedBy(EntrySymbol, new Entry(1, 4));

        Assert.Equal(4u, array.Size);
        Assert.Equal((ushort)1, array.Get(0).Symbol);
        Assert.Equal((ushort)3, array.Get(1).Symbol);
        Assert.Equal((ushort)7, array.Get(2).Symbol);
        Assert.Equal(2, array.Get(2).Tag);
        Assert.Equal((ushort)9, array.Get(3).Symbol);
    }
}
