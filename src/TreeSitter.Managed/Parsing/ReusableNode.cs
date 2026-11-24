// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/reusable_node.h and lib/src/reduce_action.h.
// Same fields and same logic; the arrays are TsArray, as in the rest of the port.

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
        ? Stack.Contents![Stack.Size - 1].Tree
        : Subtree.Null;

    /// <summary>reusable_node_byte_offset</summary>
    internal readonly uint ByteOffset => Stack.Size > 0
        ? Stack.Contents![Stack.Size - 1].ByteOffset
        : uint.MaxValue;

    /// <summary>reusable_node_delete</summary>
    internal void Delete() => Stack.Delete();

    /// <summary>reusable_node_advance</summary>
    internal void Advance()
    {
        ReusableNodeEntry lastEntry = Stack.Back();
        uint byteOffset = lastEntry.ByteOffset + lastEntry.Tree.TotalBytes;
        if (lastEntry.Tree.HasExternalTokens)
        {
            LastExternalToken = SubtreeOps.LastExternalToken(lastEntry.Tree);
        }

        Subtree tree;
        uint nextIndex;
        do
        {
            ReusableNodeEntry poppedEntry = Stack.Pop();
            nextIndex = poppedEntry.ChildIndex + 1;
            if (Stack.Size == 0)
            {
                return;
            }

            tree = Stack.Back().Tree;
        }
        while (tree.ChildCount <= nextIndex);

        Stack.Push(new ReusableNodeEntry
        {
            Tree = tree.Children[(int)nextIndex],
            ChildIndex = nextIndex,
            ByteOffset = byteOffset,
        });
    }

    /// <summary>reusable_node_descend</summary>
    internal bool Descend()
    {
        ReusableNodeEntry lastEntry = Stack.Back();
        if (lastEntry.Tree.ChildCount > 0)
        {
            Stack.Push(new ReusableNodeEntry
            {
                Tree = lastEntry.Tree.Children[0],
                ChildIndex = 0,
                ByteOffset = lastEntry.ByteOffset,
            });
            return true;
        }

        return false;
    }

    /// <summary>reusable_node_advance_past_leaf</summary>
    internal void AdvancePastLeaf()
    {
        while (Descend())
        {
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
        for (uint i = startIndex; i < self.Size; i++)
        {
            Range range = self.Contents![i];
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
