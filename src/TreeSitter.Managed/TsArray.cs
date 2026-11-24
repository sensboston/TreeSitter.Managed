// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/array.h.
//
// array.h is a set of macros over `Array(T) { contents, size, capacity }`. TsArray<T>
// keeps the same three fields and the same growth rules. Each macro is a method with the
// same behavior; the comment above each method names the macro it ports.
//
// Growth, as in the C code (keep it, allocation profiles must match C):
//   - push and grow_by go through _array__grow: the capacity doubles, with a minimum of 8
//     and a minimum of the new size.
//   - reserve, splice (and so insert, erase, extend, assign) go through _array__reserve:
//     the capacity becomes exactly the requested size. It never shrinks.
//
// TsArray is a struct meant to be used by reference: keep it in a field or a local and
// call the methods on that variable. A copy made by assignment shares the same Contents
// array but has its own Size and Capacity, so do not keep using both.
// default(TsArray<T>) is array_new() and array_init().
//
// Sorted search: the C macros compare with `compare(element, needle)` callbacks or with
// `_compare_int`. Here the caller passes a static lambda (no allocation per call).

using System.Diagnostics;

namespace TreeSitter;

/// <summary>
/// Compares an element with a needle. A negative result means the element sorts before the
/// needle, zero means equal, and a positive result means it sorts after.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <typeparam name="TNeedle">The type of the value searched for.</typeparam>
/// <param name="element">The element of the array.</param>
/// <param name="needle">The value searched for.</param>
internal delegate int ElementComparison<T, TNeedle>(T element, TNeedle needle);

/// <summary>
/// A growable array with the size and capacity rules of the tree-sitter C array.h.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
internal struct TsArray<T>
{
    /// <summary>Storage. Null while Capacity is 0.</summary>
    internal T[]? Contents;

    /// <summary>Number of elements in use (array.size).</summary>
    internal uint Size;

    /// <summary>Number of elements the storage can hold (array.capacity).</summary>
    internal uint Capacity;

    // array_init
    internal void Init()
    {
        Contents = null;
        Size = 0;
        Capacity = 0;
    }

    // array_delete. The garbage collector frees the storage.
    internal void Delete()
    {
        Contents = null;
        Size = 0;
        Capacity = 0;
    }

    // array_get. The index must be below Size.
    internal ref T Get(uint index)
    {
        Debug.Assert(index < Size, "array index out of range");
        return ref Contents![index];
    }

    // array_front
    internal ref T Front() => ref Get(0);

    // array_back
    internal ref T Back() => ref Get(Size - 1);

    // array_clear. The storage is kept.
    internal void Clear() => Size = 0;

    // Read-only view of the first Size elements. Not a C macro: array_push_all needs it.
    internal ReadOnlySpan<T> AsSpan() => Contents.AsSpan(0, (int)Size);

    // array_reserve / _array__reserve: grows the storage to exactly newCapacity.
    // Does nothing when newCapacity is not larger than the current capacity.
    internal void Reserve(uint newCapacity)
    {
        if (newCapacity > Capacity)
        {
            Array.Resize(ref Contents, (int)newCapacity);
            Capacity = newCapacity;
        }
    }

    // _array__grow: used by array_push and array_grow_by. The capacity doubles, but is at
    // least 8 and at least the new size.
    private void Grow(uint count)
    {
        uint newSize = Size + count;
        if (newSize > Capacity)
        {
            uint newCapacity = Capacity * 2u;
            if (newCapacity < 8u)
            {
                newCapacity = 8u;
            }
            if (newCapacity < newSize)
            {
                newCapacity = newSize;
            }
            Reserve(newCapacity);
        }
    }

    // array_push
    internal void Push(T element)
    {
        Grow(1);
        Contents![Size] = element;
        Size++;
    }

    // array_grow_by: appends count zero-initialized elements. Nothing happens for count 0.
    internal void GrowBy(uint count)
    {
        if (count == 0)
        {
            return;
        }
        Grow(count);
        Array.Clear(Contents!, (int)Size, (int)count);
        Size += count;
    }

    // array_push_all: appends all elements of other.
    internal void PushAll(TsArray<T> other) => Extend(other.AsSpan());

    // array_extend: appends the elements of items.
    internal void Extend(ReadOnlySpan<T> items) => Splice(Size, 0, (uint)items.Length, items);

    // array_splice / _array__splice: removes oldCount elements at index and puts newCount
    // elements in their place. The new elements are copied from newItems. When newItems is
    // empty and newCount is above 0, the new elements are zero (C passes NULL for that).
    // The removed range must lie inside the array.
    internal void Splice(uint index, uint oldCount, uint newCount, ReadOnlySpan<T> newItems)
    {
        uint newSize = Size + newCount - oldCount;
        uint oldEnd = index + oldCount;
        uint newEnd = index + newCount;
        Debug.Assert(oldEnd <= Size, "splice range out of bounds");

        Reserve(newSize);
        T[] contents = Contents!;
        if (Size > oldEnd)
        {
            Array.Copy(contents, (int)oldEnd, contents, (int)newEnd, (int)(Size - oldEnd));
        }
        if (newCount > 0)
        {
            if (newItems.IsEmpty)
            {
                Array.Clear(contents, (int)index, (int)newCount);
            }
            else
            {
                Debug.Assert((uint)newItems.Length == newCount, "newItems must hold newCount elements");
                newItems.CopyTo(contents.AsSpan((int)index, (int)newCount));
            }
        }
        Size = newSize;
    }

    // array_insert: inserts one element at index. The new slot is zeroed by the splice,
    // then the element is written.
    internal void Insert(uint index, T element)
    {
        Splice(index, 0, 1, ReadOnlySpan<T>.Empty);
        Contents![index] = element;
    }

    // array_erase: removes the element at index and keeps the order of the others.
    internal void Erase(uint index)
    {
        Debug.Assert(index < Size, "erase index out of range");
        T[] contents = Contents!;
        Array.Copy(contents, (int)index + 1, contents, (int)index, (int)(Size - index - 1));
        Size--;
    }

    // array_pop: removes the last element and returns it. The array must not be empty.
    internal T Pop() => Contents![--Size];

    // array_assign: makes this array hold a copy of source. Reserve only grows the storage.
    internal void Assign(ReadOnlySpan<T> source)
    {
        Reserve((uint)source.Length);
        Size = (uint)source.Length;
        source.CopyTo(Contents.AsSpan(0, source.Length));
    }

    // array_swap: exchanges the contents, sizes and capacities of two arrays.
    internal void Swap(ref TsArray<T> other)
    {
        (Contents, other.Contents) = (other.Contents, Contents);
        (Size, other.Size) = (other.Size, Size);
        (Capacity, other.Capacity) = (other.Capacity, Capacity);
    }

    // _array__search_sorted with start 0, used by array_search_sorted_with. Returns the
    // index found and whether an equal element exists. When nothing is equal, index is the
    // position where the needle belongs. Among equal elements the last one is found,
    // because the loop moves to the right on comparison 0.
    internal void SearchSortedWith<TNeedle>(
        ElementComparison<T, TNeedle> compare, TNeedle needle, out uint index, out bool exists)
    {
        index = 0;
        exists = false;
        uint size = Size;
        if (size == 0)
        {
            return;
        }
        int comparison;
        while (size > 1)
        {
            uint halfSize = size / 2;
            uint midIndex = index + halfSize;
            comparison = compare(Contents![midIndex], needle);
            if (comparison <= 0)
            {
                index = midIndex;
            }
            size -= halfSize;
        }
        comparison = compare(Contents![index], needle);
        if (comparison == 0)
        {
            exists = true;
        }
        else if (comparison < 0)
        {
            index += 1;
        }
    }

    // _array__search_sorted with the _compare_int comparison of array_search_sorted_by.
    // The key selector returns the field already cast to int, and the comparison is
    // _compare_int: (int)*(a) - (int)(b), with the same wrap-around as in C. Search rules
    // are the same as in SearchSortedWith.
    internal void SearchSortedBy(Func<T, int> key, int needle, out uint index, out bool exists)
    {
        index = 0;
        exists = false;
        uint size = Size;
        if (size == 0)
        {
            return;
        }
        int comparison;
        while (size > 1)
        {
            uint halfSize = size / 2;
            uint midIndex = index + halfSize;
            comparison = unchecked(key(Contents![midIndex]) - needle);
            if (comparison <= 0)
            {
                index = midIndex;
            }
            size -= halfSize;
        }
        comparison = unchecked(key(Contents![index]) - needle);
        if (comparison == 0)
        {
            exists = true;
        }
        else if (comparison < 0)
        {
            index += 1;
        }
    }

    // array_insert_sorted_with: inserts value unless an equal element exists.
    internal void InsertSortedWith(ElementComparison<T, T> compare, T value)
    {
        SearchSortedWith(compare, value, out uint index, out bool exists);
        if (!exists)
        {
            Insert(index, value);
        }
    }

    // array_insert_sorted_by: inserts value unless an element with an equal key exists.
    internal void InsertSortedBy(Func<T, int> key, T value)
    {
        SearchSortedBy(key, key(value), out uint index, out bool exists);
        if (!exists)
        {
            Insert(index, value);
        }
    }
}
