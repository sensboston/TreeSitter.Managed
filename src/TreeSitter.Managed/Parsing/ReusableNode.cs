// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/reusable_node.h and lib/src/reduce_action.h.
// Same fields and same logic; the arrays are TsArray, as in the rest of the port.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TreeSitter.Subtrees;

namespace TreeSitter.Parsing;

/// <summary>StackEntry of reusable_node.h.</summary>
internal struct ReusableNodeEntry
{
    internal Subtree Tree;
    internal uint ChildIndex;
    internal uint ByteOffset;
}

/// <summary>ReusableNode: walks the old tree to find nodes the parser can reuse.</summary>
internal struct ReusableNode
{
    internal TsArray<ReusableNodeEntry> Stack;
    internal Subtree LastExternalToken;

    /// <summary>reusable_node_new</summary>
    internal static ReusableNode New() => new ReusableNode { Stack = new TsArray<ReusableNodeEntry>(), LastExternalToken = Subtree.Null };

    /// <summary>reusable_node_clear</summary>
    internal void Clear()
    {
        Stack.Clear();
        LastExternalToken = Subtree.Null;
    }

    /// <summary>reusable_node_tree</summary>
    internal readonly Subtree Tree => Stack.Size > 0
        ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Stack.Contents!), (nint)(Stack.Size - 1)).Tree
        : Subtree.Null;

    /// <summary>reusable_node_byte_offset</summary>
    internal readonly uint ByteOffset => Stack.Size > 0
        ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Stack.Contents!), (nint)(Stack.Size - 1)).ByteOffset
        : uint.MaxValue;

    /// <summary>reusable_node_delete</summary>
    internal void Delete() => Stack.Delete();

    /// <summary>reusable_node_advance</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Advance()
    {
        // The entries are read through the array of the stack: the node data of an entry is
        // looked up once per step (a Subtree accessor would look it up again), and the stack
        // size is written back once, at the exit point.
        uint size = Stack.Size;
        ref ReusableNodeEntry entriesBase = ref MemoryMarshal.GetArrayDataReference(Stack.Contents!);
        ref ReusableNodeEntry lastEntry = ref Unsafe.Add(ref entriesBase, (nint)(size - 1));
        Subtree lastTree = lastEntry.Tree;
        ulong lastRaw = lastTree.Value;
        uint byteOffset;
        if ((lastRaw & 1UL) != 0)
        {
            byteOffset = lastEntry.ByteOffset + ((uint)((lastRaw >> 48) & 0xFF)) + ((uint)((lastRaw >> 56) & 0xFF));
        }
        else
        {
            ref SubtreeHeapData heap = ref SubtreeArena.Get(lastTree);
            byteOffset = lastEntry.ByteOffset + heap.Padding.Bytes + heap.Size.Bytes;
            if ((heap.Flags & SubtreeFlags.HasExternalTokens) != 0)
            {
                LastExternalToken = FindLastExternalToken(lastTree, ref heap);
            }
        }

        uint nextIndex;
        int childrenHandle;
        uint childCount;
        do
        {
            nextIndex = Unsafe.Add(ref entriesBase, (nint)(size - 1)).ChildIndex + 1;
            size--;
            if (size == 0)
            {
                Stack.Size = 0;
                return;
            }

            Subtree tree = Unsafe.Add(ref entriesBase, (nint)(size - 1)).Tree;
            if ((tree.Value & 1UL) != 0)
            {
                childrenHandle = -1;
                childCount = 0;
            }
            else
            {
                ref SubtreeHeapData heap = ref SubtreeArena.Get(tree);
                childrenHandle = heap.ChildrenHandle;
                childCount = heap.ChildCount;
            }
        }
        while (childCount <= nextIndex);

        Subtree nextChild = Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(SubtreeArena.GetChildren(childrenHandle)),
            (nint)nextIndex);

        // We popped at least one entry above, so size < Stack.Capacity: write directly into slot [size].
        ref ReusableNodeEntry slot = ref Unsafe.Add(ref entriesBase, (nint)size);
        slot.Tree = nextChild;
        slot.ChildIndex = nextIndex;
        slot.ByteOffset = byteOffset;
        Stack.Size = size + 1u;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Subtree FindLastExternalToken(Subtree tree, ref SubtreeHeapData heap)
    {
        uint childCount = heap.ChildCount;
        while (childCount > 0)
        {
            ref Subtree childrenBase = ref MemoryMarshal.GetArrayDataReference(
                SubtreeArena.GetChildren(heap.ChildrenHandle));
            for (uint i = childCount; i > 0; i--)
            {
                Subtree child = Unsafe.Add(ref childrenBase, (nint)(i - 1));
                if ((child.Value & 1UL) == 0)
                {
                    ref SubtreeHeapData childHeap = ref SubtreeArena.Get(child);
                    if ((childHeap.Flags & SubtreeFlags.HasExternalTokens) != 0)
                    {
                        tree = child;
                        heap = ref childHeap;
                        childCount = childHeap.ChildCount;
                        break;
                    }
                }
            }
        }

        return tree;
    }

    /// <summary>reusable_node_descend</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Descend()
    {
        uint size = Stack.Size;
        ref ReusableNodeEntry lastEntry = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(Stack.Contents!),
            (nint)(size - 1));
        Subtree tree = lastEntry.Tree;
        if ((tree.Value & 1UL) != 0)
        {
            return false;
        }

        ref SubtreeHeapData heap = ref SubtreeArena.Get(tree);
        if (heap.ChildCount > 0)
        {
            uint byteOffset = lastEntry.ByteOffset;
            Subtree firstChild = MemoryMarshal.GetArrayDataReference(SubtreeArena.GetChildren(heap.ChildrenHandle));
            Stack.Push(new ReusableNodeEntry
            {
                Tree = firstChild,
                ChildIndex = 0,
                ByteOffset = byteOffset,
            });
            return true;
        }

        return false;
    }

    /// <summary>reusable_node_advance_past_leaf</summary>
    internal void AdvancePastLeaf()
    {
        uint size = Stack.Size;
        ref ReusableNodeEntry lastEntry = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(Stack.Contents!),
            (nint)(size - 1));
        Subtree tree = lastEntry.Tree;
        uint byteOffset = lastEntry.ByteOffset;

        while ((tree.Value & 1UL) == 0)
        {
            ref SubtreeHeapData heap = ref SubtreeArena.Get(tree);
            if (heap.ChildCount == 0)
            {
                break;
            }

            Subtree firstChild = MemoryMarshal.GetArrayDataReference(SubtreeArena.GetChildren(heap.ChildrenHandle));
            Stack.Push(new ReusableNodeEntry
            {
                Tree = firstChild,
                ChildIndex = 0,
                ByteOffset = byteOffset,
            });
            tree = firstChild;
        }

        Advance();
    }

    /// <summary>reusable_node_reset</summary>
    internal void Reset(Subtree tree)
    {
        Clear();
        Stack.Push(new ReusableNodeEntry
        {
            Tree = tree,
            ChildIndex = 0,
            ByteOffset = 0,
        });

        // Never reuse the root node, because it has a non-standard internal structure
        // due to transformations that are applied when it is accepted: adding the EOF
        // child and any extra children.
        if (!Descend())
        {
            Clear();
        }
    }
}

/// <summary>ReduceAction of reduce_action.h.</summary>
internal struct ReduceAction
{
    internal uint Count;
    internal ushort Symbol;
    internal int DynamicPrecedence;
    internal ushort ProductionId;
}

/// <summary>ReduceActionSet of reduce_action.h.</summary>
internal static class ReduceActionSet
{
    /// <summary>ts_reduce_action_set_add</summary>
    internal static void Add(ref TsArray<ReduceAction> self, ReduceAction newAction)
    {
        for (uint i = 0; i < self.Size; i++)
        {
            ReduceAction action = self.Contents![i];
            if (action.Symbol == newAction.Symbol && action.Count == newAction.Count)
            {
                return;
            }
        }

        self.Push(newAction);
    }
}

/// <summary>The TSRangeArray functions of get_changed_ranges.c that the parser uses.</summary>
internal static class RangeArray
{
    /// <summary>ts_range_array_add</summary>
    private static void Add(ref TsArray<Range> self, Length start, Length end)
    {
        if (self.Size > 0)
        {
            ref Range lastRange = ref self.Back();
            if (start.Bytes <= lastRange.EndByte)
            {
                lastRange = lastRange with { EndByte = end.Bytes, EndPoint = end.Extent };
                return;
            }
        }

        if (start.Bytes < end.Bytes)
        {
            self.Push(new Range(start.Extent, end.Extent, start.Bytes, end.Bytes));
        }
    }

    /// <summary>ts_range_array_intersects</summary>
    internal static bool Intersects(in TsArray<Range> self, uint startIndex, uint startByte, uint endByte)
    {
        uint size = self.Size;
        if (startIndex >= size)
        {
            return false;
        }

        ref Range baseRef = ref MemoryMarshal.GetArrayDataReference(self.Contents!);
        for (uint i = startIndex; i < size; i++)
        {
            ref Range range = ref Unsafe.Add(ref baseRef, (nint)i);
            if (range.EndByte > startByte)
            {
                if (range.StartByte >= endByte)
                {
                    break;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>ts_range_array_get_changed_ranges</summary>
    internal static void GetChangedRanges(
        ReadOnlySpan<Range> oldRanges,
        ReadOnlySpan<Range> newRanges,
        ref TsArray<Range> differences)
    {
        int newIndex = 0;
        int oldIndex = 0;
        Length currentPosition = Length.Zero;
        bool inOldRange = false;
        bool inNewRange = false;

        while (oldIndex < oldRanges.Length || newIndex < newRanges.Length)
        {
            // C takes the address of old_ranges[old_index] even past the end; it reads it
            // only while the index is inside the array.
            Length nextOldPosition;
            if (inOldRange)
            {
                Range oldRange = oldRanges[oldIndex];
                nextOldPosition = new Length(oldRange.EndByte, oldRange.EndPoint);
            }
            else if (oldIndex < oldRanges.Length)
            {
                Range oldRange = oldRanges[oldIndex];
                nextOldPosition = new Length(oldRange.StartByte, oldRange.StartPoint);
            }
            else
            {
                nextOldPosition = Length.Max;
            }

            Length nextNewPosition;
            if (inNewRange)
            {
                Range newRange = newRanges[newIndex];
                nextNewPosition = new Length(newRange.EndByte, newRange.EndPoint);
            }
            else if (newIndex < newRanges.Length)
            {
                Range newRange = newRanges[newIndex];
                nextNewPosition = new Length(newRange.StartByte, newRange.StartPoint);
            }
            else
            {
                nextNewPosition = Length.Max;
            }

            if (nextOldPosition.Bytes < nextNewPosition.Bytes)
            {
                if (inOldRange != inNewRange)
                {
                    Add(ref differences, currentPosition, nextOldPosition);
                }

                if (inOldRange)
                {
                    oldIndex++;
                }

                currentPosition = nextOldPosition;
                inOldRange = !inOldRange;
            }
            else if (nextNewPosition.Bytes < nextOldPosition.Bytes)
            {
                if (inOldRange != inNewRange)
                {
                    Add(ref differences, currentPosition, nextNewPosition);
                }

                if (inNewRange)
                {
                    newIndex++;
                }

                currentPosition = nextNewPosition;
                inNewRange = !inNewRange;
            }
            else
            {
                if (inOldRange != inNewRange)
                {
                    Add(ref differences, currentPosition, nextNewPosition);
                }

                if (inOldRange)
                {
                    oldIndex++;
                }

                if (inNewRange)
                {
                    newIndex++;
                }

                inOldRange = !inOldRange;
                inNewRange = !inNewRange;
                currentPosition = nextNewPosition;
            }
        }
    }
}
