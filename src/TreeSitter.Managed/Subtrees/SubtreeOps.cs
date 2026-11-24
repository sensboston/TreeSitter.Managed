// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/subtree.c (without the dot graph printer).
// Rules: docs/ARCHITECTURE.md D1. Each method has the C name above it and the same logic.
// - ref counts: Interlocked on SubtreeHeap.RefCount, at exactly the places C changes them.
// - ts_subtree_pool_allocate always creates a new SubtreeHeap; ts_subtree_pool_free does not
//   keep freed objects (recycling is off by decision D1), but the free_trees size rule is kept
//   in a comment so the place is visible.
// - ts_subtree_new_node takes the children array (SubtreeArray contents) as its own, like C;
//   the caller must not use that array afterwards.
// - ts_subtree_string returns the same text as C, byte for byte (see SubtreeScriptOracleTests).

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TreeSitter.Abi;

namespace TreeSitter.Subtrees;

/// <summary>ExternalScannerState: short data inline (24 bytes), longer data in an array.</summary>
internal struct ExternalScannerState
{
    // Fields are private to the implementation: an [InlineArray(24)] buffer, a byte[] for
    // long data, and the length (decision D8).

    /// <summary>The size of the inline buffer: sizeof(short_data) of the C code.</summary>
    private const int ShortCapacity = 24;

    /// <summary>The inline buffer of the C code (char short_data[24]).</summary>
    [InlineArray(ShortCapacity)]
    private struct ShortBuffer
    {
        private byte _element0;
    }

    private ShortBuffer _short;
    private byte[]? _long;
    private uint _length;

    /// <summary>ts_external_scanner_state_init</summary>
    internal static ExternalScannerState Create(ReadOnlySpan<byte> data)
    {
        ExternalScannerState state = default;
        if (data.Length > ShortCapacity)
        {
            state._long = data.ToArray();
        }
        else
        {
            Span<byte> buffer = MemoryMarshal.CreateSpan(ref state._short[0], ShortCapacity);
            data.CopyTo(buffer);
        }

        state._length = (uint)data.Length;
        return state;
    }

    /// <summary>
    /// The repeat_depth of a leaf from an external scanner. In SubtreeHeapData of the C code
    /// external_scanner_state and the non-terminal fields (repeat_depth among them) share one
    /// union, and ts_subtree_repeat_depth reads the field without checking child_count. So
    /// for such a leaf it returns bytes 16 and 17 of short_data (little endian). The leaf is
    /// zero-initialized by ts_subtree_new_leaf, so the bytes past the length are zero, and a
    /// long state (only the long_data pointer is written) gives zero.
    /// </summary>
    internal readonly ushort UnionRepeatDepth
    {
        get
        {
            if (_length > ShortCapacity)
            {
                return 0;
            }

            return (ushort)(_short[16] | (_short[17] << 8));
        }
    }

    /// <summary>ts_external_scanner_state_copy</summary>
    internal readonly ExternalScannerState Copy()
    {
        ExternalScannerState result = this;
        if (_length > ShortCapacity)
        {
            result._long = _long!.AsSpan().ToArray();
        }

        return result;
    }

    /// <summary>ts_external_scanner_state_delete</summary>
    internal void Delete()
    {
        // C frees the long buffer and leaves the state unusable. The garbage collector
        // frees the buffer here, so the state becomes empty.
        _long = null;
        _length = 0;
    }

    /// <summary>ts_external_scanner_state_data (length bytes)</summary>
    internal readonly ReadOnlySpan<byte> Data
    {
        get
        {
            if (_length > ShortCapacity)
            {
                return _long!.AsSpan(0, (int)_length);
            }

            // 'this' is readonly in a readonly member: the buffer is reached through a
            // writable reference, otherwise the compiler would copy the struct and the
            // span would point to the copy.
            ref ExternalScannerState self = ref Unsafe.AsRef(in this);
            return MemoryMarshal.CreateReadOnlySpan(ref self._short[0], (int)_length);
        }
    }

    internal readonly uint Length => _length;

    /// <summary>ts_external_scanner_state_eq</summary>
    internal readonly bool Eq(ReadOnlySpan<byte> buffer) =>
        _length == (uint)buffer.Length && Data.SequenceEqual(buffer);
}

/// <summary>SubtreePool</summary>
internal sealed class SubtreePool
{
    /// <summary>ts_subtree_pool_new</summary>
    internal SubtreePool(uint capacity)
    {
        FreeTrees = new TsArray<SubtreeHeap>();
        TreeStack = new TsArray<Subtree>();
        FreeTrees.Reserve(capacity);
    }

    internal TsArray<SubtreeHeap> FreeTrees;

    internal TsArray<Subtree> TreeStack;

    /// <summary>ts_subtree_pool_delete</summary>
    internal void Delete()
    {
        FreeTrees.Delete();
        TreeStack.Delete();
    }
}

internal static class SubtreeOps
{
    /// <summary>TS_TREE_STATE_NONE</summary>
    private const ushort TreeStateNone = ushort.MaxValue;

    /// <summary>ROOT_FIELD of subtree.c: the field name of the root frame.</summary>
    private const string RootField = "__ROOT__";

    /// <summary>TS_MAX_INLINE_TREE_LENGTH (UINT8_MAX)</summary>
    private const uint MaxInlineTreeLength = 255;

    // ------------------------------------------------------------ SubtreeArray

    /// <summary>ts_subtree_array_copy (retains every element)</summary>
    internal static void ArrayCopy(TsArray<Subtree> self, ref TsArray<Subtree> dest)
    {
        dest.Size = self.Size;
        dest.Capacity = self.Capacity;
        if (self.Capacity > 0)
        {
            dest.Contents = new Subtree[(int)self.Capacity];
            self.AsSpan().CopyTo(dest.Contents);
            for (uint i = 0; i < self.Size; i++)
            {
                Retain(dest.Get(i));
            }
        }
        else
        {
            dest.Contents = null;
        }
    }

    /// <summary>ts_subtree_array_clear</summary>
    internal static void ArrayClear(SubtreePool pool, ref TsArray<Subtree> self)
    {
        for (uint i = 0; i < self.Size; i++)
        {
            Release(pool, self.Get(i));
        }

        self.Clear();
    }

    /// <summary>ts_subtree_array_delete</summary>
    internal static void ArrayDelete(SubtreePool pool, ref TsArray<Subtree> self)
    {
        ArrayClear(pool, ref self);
        self.Delete();
    }

    /// <summary>ts_subtree_array_remove_trailing_extras</summary>
    internal static void ArrayRemoveTrailingExtras(ref TsArray<Subtree> self, ref TsArray<Subtree> destination)
    {
        destination.Clear();
        while (self.Size > 0)
        {
            Subtree last = self.Get(self.Size - 1);
            if (last.Extra)
            {
                self.Size--;
                destination.Push(last);
            }
            else
            {
                break;
            }
        }

        ArrayReverse(ref destination);
    }

    /// <summary>ts_subtree_array_reverse</summary>
    internal static void ArrayReverse(ref TsArray<Subtree> self)
    {
        uint limit = self.Size / 2;
        for (uint i = 0; i < limit; i++)
        {
            uint reverseIndex = self.Size - 1 - i;
            Subtree swap = self.Get(i);
            self.Get(i) = self.Get(reverseIndex);
            self.Get(reverseIndex) = swap;
        }
    }

    // ------------------------------------------------------------ construction

    /// <summary>ts_subtree_can_inline</summary>
    private static bool CanInline(Length padding, Length size, uint lookaheadBytes) =>
        padding.Bytes < MaxInlineTreeLength &&
        padding.Extent.Row < 16 &&
        padding.Extent.Column < MaxInlineTreeLength &&
        size.Bytes < MaxInlineTreeLength &&
        size.Extent.Row == 0 &&
        size.Extent.Column < MaxInlineTreeLength &&
        lookaheadBytes < 16;

    /// <summary>ts_subtree_new_leaf</summary>
    internal static Subtree NewLeaf(
        SubtreePool pool, ushort symbol, Length padding, Length size, uint lookaheadBytes, ushort parseState,
        bool hasExternalTokens, bool dependsOnColumn, bool isKeyword, Language language)
    {
        SymbolMetadata metadata = language.GetSymbolMetadata(symbol);
        bool extra = symbol == BuiltinSymbols.End;

        bool isInline =
            symbol <= byte.MaxValue &&
            !hasExternalTokens &&
            CanInline(padding, size, lookaheadBytes);

        if (isInline)
        {
            return Subtree.NewInline(
                symbol,
                parseState,
                metadata.Visible,
                metadata.Named,
                extra,
                false,
                false,
                isKeyword,
                dependsOnColumn,
                (byte)padding.Extent.Column,
                (byte)padding.Extent.Row,
                (byte)lookaheadBytes,
                (byte)padding.Bytes,
                (byte)size.Bytes);
        }

        SubtreeHeap heap = new SubtreeHeap
        {
            RefCount = 1,
            Padding = padding,
            Size = size,
            LookaheadBytes = lookaheadBytes,
            ErrorCost = 0,
            ChildCount = 0,
            Symbol = symbol,
            ParseState = parseState,
            Flags = MakeFlags(
                metadata.Visible,
                metadata.Named,
                extra,
                false,
                false,
                false,
                hasExternalTokens,
                false,
                dependsOnColumn,
                false,
                isKeyword),
        };
        return new Subtree(0, heap);
    }

    /// <summary>ts_subtree_set_symbol</summary>
    internal static void SetSymbol(ref Subtree self, ushort symbol, Language language)
    {
        SymbolMetadata metadata = language.GetSymbolMetadata(symbol);
        if (self.IsInline)
        {
            self = self.WithInlineSymbol(symbol, metadata.Named, metadata.Visible);
        }
        else
        {
            SubtreeHeap heap = self.Heap!;
            heap.Symbol = symbol;
            heap.SetFlag(SubtreeFlags.Named, metadata.Named);
            heap.SetFlag(SubtreeFlags.Visible, metadata.Visible);
        }
    }

    /// <summary>ts_subtree_new_error</summary>
    internal static Subtree NewError(
        SubtreePool pool, int lookaheadChar, Length padding, Length size, uint bytesScanned, ushort parseState,
        Language language)
    {
        Subtree result = NewLeaf(
            pool,
            BuiltinSymbols.Error,
            padding,
            size,
            bytesScanned,
            parseState,
            false,
            false,
            false,
            language);

        // The error symbol never fits into the inline data, so the result is a heap node.
        SubtreeHeap heap = result.Heap!;
        heap.SetFlag(SubtreeFlags.FragileLeft, true);
        heap.SetFlag(SubtreeFlags.FragileRight, true);
        heap.LookaheadChar = lookaheadChar;
        return result;
    }

    /// <summary>ts_subtree_clone</summary>
    internal static Subtree Clone(Subtree self)
    {
        SubtreeHeap source = self.Heap!;
        SubtreeHeap result = new SubtreeHeap
        {
            RefCount = 1,
            Padding = source.Padding,
            Size = source.Size,
            LookaheadBytes = source.LookaheadBytes,
            ErrorCost = source.ErrorCost,
            ChildCount = source.ChildCount,
            Symbol = source.Symbol,
            ParseState = source.ParseState,
            Flags = source.Flags,
            VisibleChildCount = source.VisibleChildCount,
            NamedChildCount = source.NamedChildCount,
            VisibleDescendantCount = source.VisibleDescendantCount,
            DynamicPrecedence = source.DynamicPrecedence,
            RepeatDepth = source.RepeatDepth,
            ProductionId = source.ProductionId,
            FirstLeafSymbol = source.FirstLeafSymbol,
            FirstLeafParseState = source.FirstLeafParseState,
            LookaheadChar = source.LookaheadChar,
        };

        if (source.ChildCount > 0)
        {
            Subtree[] children = new Subtree[(int)source.ChildCount];
            for (uint i = 0; i < source.ChildCount; i++)
            {
                children[i] = source.Children![i];
                Retain(children[i]);
            }

            result.Children = children;
        }
        else if (source.HasFlag(SubtreeFlags.HasExternalTokens))
        {
            result.ExternalScannerState = source.ExternalScannerState.Copy();
        }

        return new Subtree(0, result);
    }

    /// <summary>ts_subtree_make_mut</summary>
    internal static Subtree MakeMut(SubtreePool pool, Subtree self)
    {
        if (self.IsInline)
        {
            return self;
        }

        if (self.Heap!.RefCount == 1)
        {
            return self;
        }

        Subtree result = Clone(self);
        Release(pool, self);
        return result;
    }

    /// <summary>ts_subtree_compress</summary>
    internal static void Compress(Subtree self, uint count, Language language, ref TsArray<Subtree> stack)
    {
        uint initialStackSize = stack.Size;
        Subtree tree = self;
        if (tree.IsInline || tree.IsNull)
        {
            return;
        }

        ushort symbol = tree.Heap!.Symbol;
        for (uint i = 0; i < count; i++)
        {
            SubtreeHeap treeHeap = tree.Heap!;
            if (treeHeap.RefCount > 1 || treeHeap.ChildCount < 2)
            {
                break;
            }

            Subtree child = treeHeap.Children![0];
            if (child.IsInline || child.Heap!.ChildCount < 2 || child.Heap!.RefCount > 1 || child.Heap!.Symbol != symbol)
            {
                break;
            }

            SubtreeHeap childHeap = child.Heap!;
            Subtree grandchild = childHeap.Children![0];
            if (grandchild.IsInline || grandchild.Heap!.ChildCount < 2 || grandchild.Heap!.RefCount > 1 || grandchild.Heap!.Symbol != symbol)
            {
                break;
            }

            SubtreeHeap grandchildHeap = grandchild.Heap!;
            treeHeap.Children![0] = grandchild;
            childHeap.Children![0] = grandchildHeap.Children![grandchildHeap.ChildCount - 1];
            grandchildHeap.Children![grandchildHeap.ChildCount - 1] = child;
            stack.Push(tree);
            tree = grandchild;
        }

        while (stack.Size > initialStackSize)
        {
            tree = stack.Pop();
            Subtree child = tree.Heap!.Children![0];
            Subtree grandchild = child.Heap!.Children![child.Heap!.ChildCount - 1];
            SummarizeChildren(grandchild, language);
            SummarizeChildren(child, language);
            SummarizeChildren(tree, language);
        }
    }

    /// <summary>ts_subtree_summarize_children</summary>
    internal static void SummarizeChildren(Subtree self, Language language)
    {
        SubtreeHeap heap = self.Heap!;

        heap.NamedChildCount = 0;
        heap.VisibleChildCount = 0;
        heap.ErrorCost = 0;
        heap.RepeatDepth = 0;
        heap.VisibleDescendantCount = 0;
        heap.SetFlag(SubtreeFlags.HasExternalTokens, false);
        heap.SetFlag(SubtreeFlags.DependsOnColumn, false);
        heap.SetFlag(SubtreeFlags.HasExternalScannerStateChange, false);
        heap.DynamicPrecedence = 0;

        uint structuralIndex = 0;
        bool hasAliasSequence = language.TryGetAliasSequence(heap.ProductionId, out ReadOnlySpan<ushort> aliasSequence);
        uint lookaheadEndByte = 0;

        ReadOnlySpan<Subtree> children = self.Children;
        for (uint i = 0; i < heap.ChildCount; i++)
        {
            Subtree child = children[(int)i];

            if ((i == 0 || heap.Size.Extent.Row == 0) && child.DependsOnColumn)
            {
                heap.SetFlag(SubtreeFlags.DependsOnColumn, true);
            }

            if (child.HasExternalScannerStateChange)
            {
                heap.SetFlag(SubtreeFlags.HasExternalScannerStateChange, true);
            }

            if (i == 0)
            {
                heap.Padding = child.Padding;
                heap.Size = child.Size;
            }
            else
            {
                heap.Size = Length.Add(heap.Size, child.TotalSize);
            }

            uint childLookaheadEndByte = heap.Padding.Bytes + heap.Size.Bytes + child.LookaheadBytes;
            if (childLookaheadEndByte > lookaheadEndByte)
            {
                lookaheadEndByte = childLookaheadEndByte;
            }

            uint grandchildCount = child.ChildCount;
            if (child.Symbol == BuiltinSymbols.ErrorRepeat)
            {
                // Refund an _ERROR child's extent penalty, which this node re-charges
                // as part of its own extent below, so that the grouping is cost-neutral.
                uint extentCost = ErrorExtentCost(child.Size);
                heap.ErrorCost += child.ErrorCost - extentCost;
            }
            else
            {
                heap.ErrorCost += child.ErrorCost;
                if (heap.Symbol == BuiltinSymbols.Error || heap.Symbol == BuiltinSymbols.ErrorRepeat)
                {
                    if (!child.Extra && !(child.IsError && grandchildCount == 0))
                    {
                        if (child.Visible)
                        {
                            heap.ErrorCost += ErrorCosts.ErrorCostPerSkippedTree;
                        }
                        else if (grandchildCount > 0)
                        {
                            heap.ErrorCost += ErrorCosts.ErrorCostPerSkippedTree * child.VisibleChildCount;
                        }
                    }
                }
            }

            heap.DynamicPrecedence += child.DynamicPrecedence;
            heap.VisibleDescendantCount += child.VisibleDescendantCount;

            ushort aliasSymbol = 0;
            if (!child.Extra &&
                child.Symbol != 0 &&
                hasAliasSequence &&
                structuralIndex < aliasSequence.Length)
            {
                aliasSymbol = aliasSequence[(int)structuralIndex];
            }

            if (aliasSymbol != 0)
            {
                heap.VisibleDescendantCount++;
                heap.VisibleChildCount++;
                if (language.GetSymbolMetadata(aliasSymbol).Named)
                {
                    heap.NamedChildCount++;
                }
            }
            else if (child.Visible)
            {
                heap.VisibleDescendantCount++;
                heap.VisibleChildCount++;
                if (child.Named)
                {
                    heap.NamedChildCount++;
                }
            }
            else if (grandchildCount > 0)
            {
                heap.VisibleChildCount += child.VisibleChildCount;
                heap.NamedChildCount += child.Heap!.NamedChildCount;
            }

            if (child.HasExternalTokens)
            {
                heap.SetFlag(SubtreeFlags.HasExternalTokens, true);
            }

            if (child.IsError)
            {
                heap.SetFlag(SubtreeFlags.FragileLeft, true);
                heap.SetFlag(SubtreeFlags.FragileRight, true);
                heap.ParseState = TreeStateNone;
            }

            if (!child.Extra)
            {
                structuralIndex++;
            }
        }

        heap.LookaheadBytes = unchecked(lookaheadEndByte - heap.Size.Bytes - heap.Padding.Bytes);

        if (heap.Symbol == BuiltinSymbols.Error || heap.Symbol == BuiltinSymbols.ErrorRepeat)
        {
            heap.ErrorCost += ErrorExtentCost(heap.Size);
        }

        if (heap.ChildCount > 0)
        {
            Subtree firstChild = children[0];
            Subtree lastChild = children[(int)heap.ChildCount - 1];

            heap.FirstLeafSymbol = firstChild.LeafSymbol;
            heap.FirstLeafParseState = firstChild.LeafParseState;

            if (firstChild.FragileLeft)
            {
                heap.SetFlag(SubtreeFlags.FragileLeft, true);
            }

            if (lastChild.FragileRight)
            {
                heap.SetFlag(SubtreeFlags.FragileRight, true);
            }

            if (heap.ChildCount >= 2 &&
                !heap.HasFlag(SubtreeFlags.Visible) &&
                !heap.HasFlag(SubtreeFlags.Named) &&
                firstChild.Symbol == heap.Symbol)
            {
                if (firstChild.RepeatDepth > lastChild.RepeatDepth)
                {
                    heap.RepeatDepth = (ushort)(firstChild.RepeatDepth + 1);
                }
                else
                {
                    heap.RepeatDepth = (ushort)(lastChild.RepeatDepth + 1);
                }
            }
        }
    }

    /// <summary>ts_subtree_new_node: takes the contents of children.</summary>
    internal static Subtree NewNode(ushort symbol, ref TsArray<Subtree> children, uint productionId, Language language)
    {
        SymbolMetadata metadata = language.GetSymbolMetadata(symbol);
        bool fragile = symbol == BuiltinSymbols.Error || symbol == BuiltinSymbols.ErrorRepeat;

        SubtreeHeap heap = new SubtreeHeap
        {
            RefCount = 1,
            Padding = Length.Zero,
            Size = Length.Zero,
            LookaheadBytes = 0,
            ErrorCost = 0,
            ChildCount = children.Size,
            Symbol = symbol,
            ParseState = 0,
            Flags = MakeFlags(
                metadata.Visible,
                metadata.Named,
                false,
                fragile,
                fragile,
                false,
                false,
                false,
                false,
                false,
                false),
            VisibleDescendantCount = 0,
            ProductionId = (ushort)productionId,
            FirstLeafSymbol = 0,
            FirstLeafParseState = 0,
        };

        // The node takes the children over: the array of the caller must not be used
        // any more (in C the node data is stored behind the children of the same block).
        Subtree[] childArray = new Subtree[(int)children.Size];
        for (uint i = 0; i < children.Size; i++)
        {
            childArray[i] = children.Get(i);
        }

        heap.Children = childArray;
        children.Contents = null;
        children.Size = 0;
        children.Capacity = 0;

        Subtree result = new Subtree(0, heap);
        SummarizeChildren(result, language);
        return result;
    }

    /// <summary>ts_subtree_new_error_node</summary>
    internal static Subtree NewErrorNode(ref TsArray<Subtree> children, bool extra, Language language)
    {
        Subtree result = NewNode(BuiltinSymbols.Error, ref children, 0, language);
        result.Heap!.SetFlag(SubtreeFlags.Extra, extra);
        return result;
    }

    /// <summary>ts_subtree_new_missing_leaf</summary>
    internal static Subtree NewMissingLeaf(
        SubtreePool pool, ushort symbol, ushort state, Length padding, uint lookaheadBytes, Language language)
    {
        Subtree result = NewLeaf(
            pool,
            symbol,
            padding,
            Length.Zero,
            lookaheadBytes,
            state,
            false,
            false,
            false,
            language);

        if (result.IsInline)
        {
            result = result.WithInlineMissing(true);
        }
        else
        {
            result.Heap!.SetFlag(SubtreeFlags.IsMissing, true);
        }

        return result;
    }

    /// <summary>ts_subtree_set_extra</summary>
    internal static void SetExtra(ref Subtree self, bool isExtra)
    {
        if (self.IsInline)
        {
            self = self.WithInlineExtra(isExtra);
        }
        else
        {
            self.Heap!.SetFlag(SubtreeFlags.Extra, isExtra);
        }
    }

    // ------------------------------------------------------------ ownership

    /// <summary>ts_subtree_retain</summary>
    internal static void Retain(Subtree self)
    {
        if (self.IsInline)
        {
            return;
        }

        SubtreeHeap heap = self.Heap!;
        Interlocked.Increment(ref heap.RefCount);
    }

    /// <summary>ts_subtree_release</summary>
    internal static void Release(SubtreePool pool, Subtree self)
    {
        if (self.IsInline)
        {
            return;
        }

        pool.TreeStack.Clear();

        SubtreeHeap heap = self.Heap!;
        if (Interlocked.Decrement(ref heap.RefCount) == 0)
        {
            pool.TreeStack.Push(self);
        }

        while (pool.TreeStack.Size > 0)
        {
            Subtree tree = pool.TreeStack.Pop();
            SubtreeHeap treeHeap = tree.Heap!;
            if (treeHeap.ChildCount > 0 && treeHeap.Children is not null)
            {
                for (uint i = 0; i < treeHeap.ChildCount; i++)
                {
                    Subtree child = treeHeap.Children[i];
                    if (child.IsInline)
                    {
                        continue;
                    }

                    SubtreeHeap childHeap = child.Heap!;
                    if (Interlocked.Decrement(ref childHeap.RefCount) == 0)
                    {
                        pool.TreeStack.Push(child);
                    }
                }

                // C frees the children of the node here.
                treeHeap.Children = null;
            }
            else
            {
                if (treeHeap.HasFlag(SubtreeFlags.HasExternalTokens))
                {
                    treeHeap.ExternalScannerState.Delete();
                }

                // C: ts_subtree_pool_free (the pool keeps at most TS_MAX_TREE_POOL_SIZE
                // objects). Recycling is off by decision D1: the garbage collector frees
                // the object.
            }
        }
    }

    // ------------------------------------------------------------ queries and edits

    /// <summary>ts_subtree_compare</summary>
    internal static int Compare(Subtree left, Subtree right, SubtreePool pool)
    {
        pool.TreeStack.Push(left);
        pool.TreeStack.Push(right);

        while (pool.TreeStack.Size > 0)
        {
            right = pool.TreeStack.Pop();
            left = pool.TreeStack.Pop();

            int result = 0;
            if (left.Symbol < right.Symbol)
            {
                result = -1;
            }
            else if (right.Symbol < left.Symbol)
            {
                result = 1;
            }
            else if (left.ChildCount < right.ChildCount)
            {
                result = -1;
            }
            else if (right.ChildCount < left.ChildCount)
            {
                result = 1;
            }

            if (result != 0)
            {
                pool.TreeStack.Clear();
                return result;
            }

            uint count = left.ChildCount;
            for (uint i = count; i > 0; i--)
            {
                Subtree leftChild = left.Children[(int)i - 1];
                Subtree rightChild = right.Children[(int)i - 1];
                pool.TreeStack.Push(leftChild);
                pool.TreeStack.Push(rightChild);
            }
        }

        return 0;
    }

    /// <summary>The frame of ts_subtree_edit: a pointer to a subtree slot and the edit in that slot's coordinates.</summary>
    private readonly struct EditEntry
    {
        internal EditEntry(SubtreeHeap? parent, int childIndex, Length start, Length oldEnd, Length newEnd)
        {
            Parent = parent;
            ChildIndex = childIndex;
            Start = start;
            OldEnd = oldEnd;
            NewEnd = newEnd;
        }

        internal SubtreeHeap? Parent { get; }

        internal int ChildIndex { get; }

        internal Length Start { get; }

        internal Length OldEnd { get; }

        internal Length NewEnd { get; }
    }

    /// <summary>ts_subtree_edit</summary>
    internal static Subtree Edit(Subtree self, in InputEdit edit, SubtreePool pool)
    {
        TsArray<EditEntry> stack = new TsArray<EditEntry>();
        Subtree root = self;
        stack.Push(new EditEntry(
            null,
            -1,
            new Length(edit.StartByte, edit.StartPoint),
            new Length(edit.OldEndByte, edit.OldEndPoint),
            new Length(edit.NewEndByte, edit.NewEndPoint)));

        while (stack.Size > 0)
        {
            EditEntry entry = stack.Pop();
            Subtree current = entry.Parent is null ? root : entry.Parent.Children![entry.ChildIndex];
            Length start = entry.Start;
            Length oldEnd = entry.OldEnd;
            Length newEnd = entry.NewEnd;
            bool isNoop = oldEnd.Bytes == start.Bytes && newEnd.Bytes == start.Bytes;
            bool isPureInsertion = oldEnd.Bytes == start.Bytes;
            bool parentDependsOnColumn = current.DependsOnColumn;
            bool columnShifted = newEnd.Extent.Column != oldEnd.Extent.Column;

            Length size = current.Size;
            Length padding = current.Padding;
            Length totalSize = Length.Add(padding, size);
            uint lookaheadBytes = current.LookaheadBytes;
            uint endByte = totalSize.Bytes + lookaheadBytes;
            if (start.Bytes > endByte || (isNoop && start.Bytes == endByte))
            {
                continue;
            }

            if (oldEnd.Bytes <= padding.Bytes)
            {
                padding = Length.Add(newEnd, Length.Sub(padding, oldEnd));
            }
            else if (start.Bytes < padding.Bytes)
            {
                size = Length.SaturatingSub(size, Length.Sub(oldEnd, padding));
                padding = newEnd;
            }
            else if (start.Bytes < totalSize.Bytes || (start.Bytes == totalSize.Bytes && isPureInsertion))
            {
                size = Length.Add(Length.Sub(newEnd, padding), Length.SaturatingSub(totalSize, oldEnd));
            }

            Subtree result = MakeMut(pool, current);
            if (result.IsInline)
            {
                if (CanInline(padding, size, lookaheadBytes))
                {
                    result = Subtree.NewInline(
                        result.Symbol,
                        result.ParseState,
                        result.Visible,
                        result.Named,
                        result.Extra,
                        result.HasChanges,
                        result.IsMissing,
                        result.IsKeyword,
                        result.DependsOnColumn,
                        (byte)padding.Extent.Column,
                        (byte)padding.Extent.Row,
                        (byte)lookaheadBytes,
                        (byte)padding.Bytes,
                        (byte)size.Bytes);
                }
                else
                {
                    SubtreeHeap heap = new SubtreeHeap
                    {
                        RefCount = 1,
                        Padding = padding,
                        Size = size,
                        LookaheadBytes = lookaheadBytes,
                        ErrorCost = 0,
                        ChildCount = 0,
                        Symbol = result.Symbol,
                        ParseState = result.ParseState,
                        Flags = MakeFlags(
                            result.Visible,
                            result.Named,
                            result.Extra,
                            false,
                            false,
                            false,
                            false,
                            false,
                            result.DependsOnColumn,
                            result.IsMissing,
                            result.IsKeyword),
                    };
                    result = new Subtree(0, heap);
                }
            }
            else
            {
                result.Heap!.Padding = padding;
                result.Heap!.Size = size;
            }

            if (result.IsInline)
            {
                result = result.WithInlineHasChanges(true);
            }
            else
            {
                result.Heap!.SetFlag(SubtreeFlags.HasChanges, true);
            }

            if (entry.Parent is null)
            {
                root = result;
            }
            else
            {
                entry.Parent.Children![entry.ChildIndex] = result;
            }

            uint childCount = result.ChildCount;
            Length childLeft = Length.Zero;
            Length childRight = Length.Zero;
            for (uint i = 0; i < childCount; i++)
            {
                Subtree child = result.Heap!.Children![(int)i];
                Length childSize = child.TotalSize;
                childLeft = childRight;
                childRight = Length.Add(childLeft, childSize);

                if (childRight.Bytes + child.LookaheadBytes < start.Bytes)
                {
                    continue;
                }

                if (((childLeft.Bytes > oldEnd.Bytes) || (childLeft.Bytes == oldEnd.Bytes && childSize.Bytes > 0 && i > 0)) &&
                    (!parentDependsOnColumn || childLeft.Extent.Row > padding.Extent.Row) &&
                    (!child.DependsOnColumn || !columnShifted || childLeft.Extent.Row > oldEnd.Extent.Row))
                {
                    break;
                }

                Length childStart = Length.SaturatingSub(start, childLeft);
                Length childOldEnd = Length.SaturatingSub(oldEnd, childLeft);
                Length childNewEnd = Length.SaturatingSub(newEnd, childLeft);
                if (childRight.Bytes > start.Bytes || (childRight.Bytes == start.Bytes && isPureInsertion))
                {
                    newEnd = start;
                }
                else
                {
                    childOldEnd = childStart;
                    childNewEnd = childStart;
                }

                stack.Push(new EditEntry(result.Heap, (int)i, childStart, childOldEnd, childNewEnd));
            }
        }

        stack.Delete();
        return root;
    }

    /// <summary>ts_subtree_last_external_token</summary>
    internal static Subtree LastExternalToken(Subtree tree)
    {
        if (!tree.HasExternalTokens)
        {
            return Subtree.Null;
        }

        while (tree.ChildCount > 0)
        {
            ReadOnlySpan<Subtree> children = tree.Children;
            for (uint i = tree.ChildCount; i > 0; i--)
            {
                Subtree child = children[(int)i - 1];
                if (child.HasExternalTokens)
                {
                    tree = child;
                    break;
                }
            }
        }

        return tree;
    }

    /// <summary>ts_subtree_string</summary>
    internal static string ToSExpression(Subtree self, ushort aliasSymbol, bool aliasIsNamed, Language language, bool includeAll)
    {
        StringBuilder builder = new StringBuilder();
        WriteToString(builder, self, language, includeAll, aliasSymbol, aliasIsNamed);
        return builder.ToString();
    }

    /// <summary>ts_subtree_print_dot_graph: the dot graph of the subtree and its descendants.</summary>
    /// <param name="self">The root subtree of the graph.</param>
    /// <param name="language">The language: symbol names and alias sequences.</param>
    /// <param name="writer">The text writer to print the graph to. The nodes are numbered
    ///   in the order of their first appearance (the address of a node in C): a heap node
    ///   keeps its number when it is visited again, an inline leaf always gets a fresh
    ///   number.</param>
    internal static void PrintDotGraph(Subtree self, Language language, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(writer);

        Dictionary<SubtreeHeap, uint> ids = new(ReferenceEqualityComparer.Instance);
        uint nextId = 0;

        // The identity of a node is the address of the Subtree value in C: a heap node
        // keeps its address (visited twice, it keeps the number), an inline leaf is a
        // copy and always gets a fresh number. The first-appearance order is the pre-order
        // of the tree walk, the same as in the C code.
        uint NodeId(Subtree subtree)
        {
            if (subtree.Heap is not null)
            {
                if (!ids.TryGetValue(subtree.Heap, out uint id))
                {
                    id = nextId++;
                    ids.Add(subtree.Heap, id);
                }

                return id;
            }

            return nextId++;
        }

        uint WriteTree(Subtree subtree, uint startOffset, ushort aliasSymbol)
        {
            ushort symbol = aliasSymbol != 0 ? aliasSymbol : subtree.Symbol;
            uint endOffset = startOffset + subtree.TotalBytes;
            uint id = NodeId(subtree);

            writer.Write("tree_" + Unsigned(id));
            writer.Write(" [label=\"");
            WriteSymbolAsDotString(language, writer, symbol);
            writer.Write("\"");
            if (subtree.ChildCount == 0) writer.Write(", shape=plaintext");
            if (subtree.Extra) writer.Write(", fontcolor=gray");
            if (subtree.HasChanges) writer.Write(", color=green, penwidth=2");

            writer.Write(", tooltip=\"range: " + Unsigned(startOffset) + " - " + Unsigned(endOffset));
            writer.Write("\nstate: " + Signed((int)subtree.ParseState));
            writer.Write("\nerror-cost: " + Unsigned(subtree.ErrorCost));
            writer.Write("\nhas-changes: " + Unsigned(subtree.HasChanges ? 1u : 0u));
            writer.Write("\ndepends-on-column: " + Unsigned(subtree.DependsOnColumn ? 1u : 0u));
            writer.Write("\ndescendant-count: " + Unsigned(subtree.VisibleDescendantCount));
            writer.Write("\nrepeat-depth: " + Unsigned(subtree.RepeatDepth));
            writer.Write("\nlookahead-bytes: " + Unsigned(subtree.LookaheadBytes));

            if (subtree.IsError && subtree.ChildCount == 0)
            {
                int lookaheadChar = 0;
                if (subtree.Heap is SubtreeHeap heap) lookaheadChar = heap.LookaheadChar;
                if (lookaheadChar != 0)
                {
                    // fprintf writes the character as an unsigned char byte; the oracle
                    // reads the text back as UTF-8, so a lone high byte becomes U+FFFD.
                    int value = lookaheadChar & 0xFF;
                    writer.Write("\ncharacter: '");
                    writer.Write(value < 0x80 ? (char)value : '\uFFFD');
                    writer.Write("'");
                }
            }

            writer.Write("\"]\n");

            uint childStartOffset = startOffset;
            uint childInfoOffset = (uint)language.MaxAliasSequenceLength * subtree.ProductionId;
            bool useAliases = childInfoOffset != 0;
            uint childAliasIndex = 0;
            uint childIndex = 0;
            foreach (Subtree child in subtree.Children)
            {
                ushort childAliasSymbol = 0;
                if (!child.Extra && useAliases)
                {
                    childAliasSymbol = language.AliasAt(subtree.ProductionId, childAliasIndex);
                    childAliasIndex++;
                }

                // The edge prints the same node the child's own line printed, so it
                // reuses that number (the C code prints the same address twice).
                uint childId = WriteTree(child, childStartOffset, childAliasSymbol);
                writer.Write("tree_" + Unsigned(id) + " -> tree_" + Unsigned(childId) + " [tooltip=" + Unsigned(childIndex) + "]\n");
                childStartOffset += child.TotalBytes;
                childIndex++;
            }

            return id;
        }

        writer.Write("digraph tree {\n");
        writer.Write("edge [arrowhead=none]\n");
        WriteTree(self, 0, 0);
        writer.Write("}\n");
    }

    /// <summary>ts_language_write_symbol_as_dot_string: the name of a symbol, escaped for a dot label.</summary>
    private static void WriteSymbolAsDotString(Language language, TextWriter writer, ushort symbol)
    {
        string name = language.SymbolName(symbol) ?? string.Empty;
        foreach (char c in name)
        {
            switch (c)
            {
                case '"':
                case '\\':
                    writer.Write('\\');
                    writer.Write(c);
                    break;
                case '\n':
                    writer.Write("\\n");
                    break;
                case '\t':
                    writer.Write("\\t");
                    break;
                default:
                    writer.Write(c);
                    break;
            }
        }
    }

    /// <summary>The text of an unsigned number, as printf %u.</summary>
    private static string Unsigned(uint value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The text of a signed number, as printf %d.</summary>
    private static string Signed(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>ts_subtree_external_scanner_state; an empty state when the subtree has none.</summary>
    internal static ExternalScannerState GetExternalScannerState(Subtree self)
    {
        if (self.IsNull || self.IsInline)
        {
            return default;
        }

        SubtreeHeap heap = self.Heap!;
        if (heap.HasFlag(SubtreeFlags.HasExternalTokens) && heap.ChildCount == 0)
        {
            return heap.ExternalScannerState.Copy();
        }

        return default;
    }

    /// <summary>ts_subtree_external_scanner_state_eq</summary>
    internal static bool ExternalScannerStateEq(Subtree self, Subtree other)
    {
        ExternalScannerState left = GetExternalScannerState(self);
        ExternalScannerState right = GetExternalScannerState(other);
        return left.Eq(right.Data);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>ts_subtree__error_extent_cost</summary>
    private static uint ErrorExtentCost(Length size) =>
        ErrorCosts.ErrorCostPerRecovery +
        (ErrorCosts.ErrorCostPerSkippedChar * size.Bytes) +
        (ErrorCosts.ErrorCostPerSkippedLine * size.Extent.Row);

    /// <summary>Builds the flag set of a heap node from the single bits of the C struct.</summary>
    private static SubtreeFlags MakeFlags(
        bool visible,
        bool named,
        bool extra,
        bool fragileLeft,
        bool fragileRight,
        bool hasChanges,
        bool hasExternalTokens,
        bool hasExternalScannerStateChange,
        bool dependsOnColumn,
        bool isMissing,
        bool isKeyword)
    {
        SubtreeFlags flags = SubtreeFlags.None;
        if (visible)
        {
            flags |= SubtreeFlags.Visible;
        }

        if (named)
        {
            flags |= SubtreeFlags.Named;
        }

        if (extra)
        {
            flags |= SubtreeFlags.Extra;
        }

        if (fragileLeft)
        {
            flags |= SubtreeFlags.FragileLeft;
        }

        if (fragileRight)
        {
            flags |= SubtreeFlags.FragileRight;
        }

        if (hasChanges)
        {
            flags |= SubtreeFlags.HasChanges;
        }

        if (hasExternalTokens)
        {
            flags |= SubtreeFlags.HasExternalTokens;
        }

        if (hasExternalScannerStateChange)
        {
            flags |= SubtreeFlags.HasExternalScannerStateChange;
        }

        if (dependsOnColumn)
        {
            flags |= SubtreeFlags.DependsOnColumn;
        }

        if (isMissing)
        {
            flags |= SubtreeFlags.IsMissing;
        }

        if (isKeyword)
        {
            flags |= SubtreeFlags.IsKeyword;
        }

        return flags;
    }

    /// <summary>ts_subtree__write_char_to_string</summary>
    private static void WriteCharToString(StringBuilder builder, int chr)
    {
        if (chr == -1)
        {
            builder.Append("INVALID");
        }
        else if (chr == '\0')
        {
            builder.Append("'\\0'");
        }
        else if (chr == '\n')
        {
            builder.Append("'\\n'");
        }
        else if (chr == '\t')
        {
            builder.Append("'\\t'");
        }
        else if (chr == '\r')
        {
            builder.Append("'\\r'");
        }
        else if (chr > 0 && chr < 128 && chr >= 32 && chr <= 126)
        {
            builder.Append('\'').Append((char)chr).Append('\'');
        }
        else
        {
            builder.Append(chr.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The frame of ts_subtree__write_to_string.</summary>
    private sealed class WriteFrame
    {
        internal Subtree Subtree;
        internal ushort AliasSymbol;
        internal bool AliasIsNamed;
        internal string? FieldName;
        internal bool IsRoot;
        internal bool PreWritten;
        internal bool IsVisible;
        internal uint ChildIndex;
        internal uint StructuralChildIndex;
        internal ushort ProductionId;
    }

    /// <summary>ts_subtree__write_to_string</summary>
    private static void WriteToString(
        StringBuilder builder,
        Subtree self,
        Language language,
        bool includeAll,
        ushort rootAliasSymbol,
        bool rootAliasIsNamed)
    {
        List<WriteFrame> stack = new List<WriteFrame>
        {
            new WriteFrame
            {
                Subtree = self,
                AliasSymbol = rootAliasSymbol,
                AliasIsNamed = rootAliasIsNamed,
                FieldName = RootField,
                IsRoot = true,
            },
        };

        while (stack.Count > 0)
        {
            int frameIndex = stack.Count - 1;
            WriteFrame frame = stack[frameIndex];
            Subtree node = frame.Subtree;

            if (node.IsNull)
            {
                if (!frame.IsRoot)
                {
                    builder.Append(' ');
                    if (frame.FieldName is not null)
                    {
                        builder.Append(frame.FieldName).Append(": ");
                    }
                }

                builder.Append("(NULL)");
                stack.RemoveAt(frameIndex);
                continue;
            }

            if (!frame.PreWritten)
            {
                bool isVisible =
                    includeAll ||
                    node.IsMissing ||
                    (frame.AliasSymbol != 0
                        ? frame.AliasIsNamed
                        : node.Visible && node.Named);

                if (isVisible)
                {
                    if (!frame.IsRoot)
                    {
                        builder.Append(' ');
                        if (frame.FieldName is not null)
                        {
                            builder.Append(frame.FieldName).Append(": ");
                        }
                    }

                    if (node.IsError && node.ChildCount == 0 && node.Heap!.Size.Bytes > 0)
                    {
                        builder.Append("(UNEXPECTED ");
                        WriteCharToString(builder, node.Heap!.LookaheadChar);
                    }
                    else
                    {
                        ushort symbol = frame.AliasSymbol != 0 ? frame.AliasSymbol : node.Symbol;
                        string symbolName = language.SymbolName(symbol) ?? "(null)";
                        if (node.IsMissing)
                        {
                            builder.Append("(MISSING ");
                            if (frame.AliasIsNamed || node.Named)
                            {
                                builder.Append(symbolName);
                            }
                            else
                            {
                                builder.Append('"').Append(symbolName).Append('"');
                            }
                        }
                        else
                        {
                            builder.Append('(').Append(symbolName);
                        }
                    }
                }
                else if (frame.IsRoot)
                {
                    ushort symbol = frame.AliasSymbol != 0 ? frame.AliasSymbol : node.Symbol;
                    string symbolName = language.SymbolName(symbol) ?? "(null)";
                    if (node.ChildCount > 0)
                    {
                        builder.Append('(').Append(symbolName);
                    }
                    else if (node.Named)
                    {
                        builder.Append('(').Append(symbolName).Append(')');
                    }
                    else
                    {
                        builder.Append('(').Append('"').Append(symbolName).Append('"').Append(')');
                    }
                }

                if (node.ChildCount > 0)
                {
                    frame.ProductionId = node.Heap!.ProductionId;
                }

                frame.IsVisible = isVisible;
                frame.PreWritten = true;
                stack[frameIndex] = frame;
            }

            if (frame.ChildIndex < node.ChildCount)
            {
                Subtree child = node.Children[(int)frame.ChildIndex];
                WriteFrame childFrame = new WriteFrame
                {
                    Subtree = child,
                    IsRoot = false,
                };

                if (!child.Extra)
                {
                    ushort subtreeAliasSymbol = 0;
                    if (frame.ProductionId != 0 &&
                        language.TryGetAliasSequence(frame.ProductionId, out ReadOnlySpan<ushort> aliasSequence) &&
                        frame.StructuralChildIndex < aliasSequence.Length)
                    {
                        subtreeAliasSymbol = aliasSequence[(int)frame.StructuralChildIndex];
                    }

                    bool subtreeAliasIsNamed =
                        subtreeAliasSymbol != 0 && language.GetSymbolMetadata(subtreeAliasSymbol).Named;

                    string? childFieldName = frame.IsVisible ? null : frame.FieldName;
                    foreach (FieldMapEntry map in language.FieldMap(frame.ProductionId))
                    {
                        if (!map.Inherited && (uint)map.ChildIndex == frame.StructuralChildIndex)
                        {
                            childFieldName = language.FieldNameForId(map.FieldId);
                            break;
                        }
                    }

                    childFrame.AliasSymbol = subtreeAliasSymbol;
                    childFrame.AliasIsNamed = subtreeAliasIsNamed;
                    childFrame.FieldName = childFieldName;
                    frame.StructuralChildIndex++;
                }

                frame.ChildIndex++;
                stack[frameIndex] = frame;
                stack.Add(childFrame);
                continue;
            }

            if (frame.IsVisible)
            {
                builder.Append(')');
            }

            stack.RemoveAt(frameIndex);
        }
    }
}
