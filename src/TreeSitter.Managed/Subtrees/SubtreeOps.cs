// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/subtree.c (without the dot graph printer).
// Rules: docs/ARCHITECTURE.md D1. Each method has the C name above it and the same logic.
// - ref counts: Interlocked on SubtreeHeapData.RefCount, at exactly the places C changes them.
// - ts_subtree_pool_allocate always creates a new arena slot; ts_subtree_pool_free does not
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

/// <summary>SubtreePool: per-parser scratch (tree stack for iterative traversals).</summary>
internal sealed class SubtreePool
{
    /// <summary>ts_subtree_pool_new</summary>
    internal SubtreePool(uint capacity)
    {
        TreeStack = new TsArray<Subtree>();
    }

    internal TsArray<Subtree> TreeStack;

    /// <summary>ts_subtree_pool_delete</summary>
    internal void Delete()
    {
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

        Subtree result = SubtreeArena.Allocate();
        ref SubtreeHeapData heap = ref SubtreeArena.Get(result);
        heap.RefCount = 1;
        heap.Padding = padding;
        heap.Size = size;
        heap.LookaheadBytes = lookaheadBytes;
        heap.ErrorCost = 0;
        heap.ChildCount = 0;
        heap.Symbol = symbol;
        heap.ParseState = parseState;
        heap.Flags = MakeFlags(
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
            isKeyword);
        heap.ChildrenHandle = -1;

        // The union (offset 0) stays zero, as the zeroed SubtreeHeapData of C: a leaf that is
        // not an external token has lookahead_char 0, one that is gets its short data or its
        // long state handle written by WriteTo.
        return result;
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
            ref SubtreeHeapData heap = ref SubtreeArena.Get(self);
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
        ref SubtreeHeapData heap = ref SubtreeArena.Get(result);
        heap.SetFlag(SubtreeFlags.FragileLeft, true);
        heap.SetFlag(SubtreeFlags.FragileRight, true);
        heap.LookaheadChar = lookaheadChar;
        return result;
    }

    private static Subtree[] CopyChildren(ReadOnlySpan<Subtree> children)
    {
        Subtree[] array = SubtreeArena.RentChildren(children.Length);
        children.CopyTo(array);
        return array;
    }

    /// <summary>ts_subtree_clone</summary>
    internal static Subtree Clone(Subtree self)
    {
        ref SubtreeHeapData source = ref SubtreeArena.Get(self);
        Subtree resultSubtree = SubtreeArena.Allocate();
        ref SubtreeHeapData result = ref SubtreeArena.Get(resultSubtree);
        result.RefCount = 1;
        result.Padding = source.Padding;
        result.Size = source.Size;
        result.LookaheadBytes = source.LookaheadBytes;
        result.ErrorCost = source.ErrorCost;
        result.ChildCount = source.ChildCount;
        result.Symbol = source.Symbol;
        result.ParseState = source.ParseState;
        result.Flags = source.Flags;
        result.ChildrenHandle = -1;

        // The union holds one interpretation (subtree.h): reset it (the handle of a long
        // state and the short data under it are zero, as a zeroed SubtreeHeapData in C),
        // then copy the fields of the one the source has. A node with children has the
        // non-terminal fields, a leaf the external scanner state or the lookahead char.
        result.ExternalScannerState.Length = 0;

        if (source.ChildCount > 0)
        {
            result.VisibleChildCount = source.VisibleChildCount;
            result.NamedChildCount = source.NamedChildCount;
            result.VisibleDescendantCount = source.VisibleDescendantCount;
            result.DynamicPrecedence = source.DynamicPrecedence;
            result.RepeatDepth = source.RepeatDepth;
            result.ProductionId = source.ProductionId;
            result.FirstLeafSymbol = source.FirstLeafSymbol;
            result.FirstLeafParseState = source.FirstLeafParseState;

            Subtree[] srcChildren = SubtreeArena.GetChildren(source.ChildrenHandle);
            Subtree[] children = SubtreeArena.RentChildren((int)source.ChildCount);
            for (uint i = 0; i < source.ChildCount; i++)
            {
                children[i] = srcChildren[i];
                Retain(children[i]);
            }

            result.ChildrenHandle = SubtreeArena.StoreChildren(resultSubtree.Handle, children);
        }
        else if (source.HasFlag(SubtreeFlags.HasExternalTokens))
        {
            ExternalScannerState.CopyData(in source.ExternalScannerState, ref result.ExternalScannerState);
        }
        else
        {
            result.LookaheadChar = source.LookaheadChar;
        }

        return resultSubtree;
    }

    /// <summary>ts_subtree_make_mut</summary>
    internal static Subtree MakeMut(SubtreePool pool, Subtree self)
    {
        if (self.IsInline)
        {
            return self;
        }

        ref SubtreeHeapData heap = ref SubtreeArena.Get(self);
        if (heap.RefCount == 1)
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
        if (count == 0 || self.IsInline || self.IsNull)
        {
            return;
        }

        uint initialStackSize = stack.Size;
        Subtree tree = self;
        ref SubtreeHeapData treeHeap = ref SubtreeArena.Get(tree);
        if (treeHeap.RefCount > 1 || treeHeap.ChildCount < 2)
        {
            return;
        }

        ushort symbol = treeHeap.Symbol;
        Subtree[] treeChildren = SubtreeArena.GetChildren(treeHeap.ChildrenHandle);

        for (uint i = 0; i < count; i++)
        {
            ref Subtree treeChild0 = ref MemoryMarshal.GetArrayDataReference(treeChildren);
            Subtree child = treeChild0;
            if ((child.Value & 1UL) != 0)
            {
                break;
            }

            ref SubtreeHeapData childHeap = ref SubtreeArena.Get(child);
            if (childHeap.ChildCount < 2 || childHeap.RefCount > 1 || childHeap.Symbol != symbol)
            {
                break;
            }

            Subtree[] childChildren = SubtreeArena.GetChildren(childHeap.ChildrenHandle);
            ref Subtree childChild0 = ref MemoryMarshal.GetArrayDataReference(childChildren);
            Subtree grandchild = childChild0;
            if ((grandchild.Value & 1UL) != 0)
            {
                break;
            }

            ref SubtreeHeapData grandchildHeap = ref SubtreeArena.Get(grandchild);
            uint grandchildChildCount = grandchildHeap.ChildCount;
            if (grandchildChildCount < 2 || grandchildHeap.RefCount > 1 || grandchildHeap.Symbol != symbol)
            {
                break;
            }

            Subtree[] grandchildChildren = SubtreeArena.GetChildren(grandchildHeap.ChildrenHandle);
            ref Subtree grandchildLast = ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(grandchildChildren),
                (nint)(grandchildChildCount - 1));

            treeChild0 = grandchild;
            childChild0 = grandchildLast;
            grandchildLast = child;

            stack.Push(tree);
            tree = grandchild;
            treeChildren = grandchildChildren;
        }

        while (stack.Size > initialStackSize)
        {
            tree = stack.Pop();
            ref SubtreeHeapData th = ref SubtreeArena.Get(tree);
            Subtree child = MemoryMarshal.GetArrayDataReference(SubtreeArena.GetChildren(th.ChildrenHandle));
            ref SubtreeHeapData ch = ref SubtreeArena.Get(child);
            Subtree grandchild = Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(SubtreeArena.GetChildren(ch.ChildrenHandle)),
                (nint)(ch.ChildCount - 1));
            SummarizeChildren(grandchild, language);
            SummarizeChildren(child, language);
            SummarizeChildren(tree, language);
        }
    }

    /// <summary>ts_subtree_summarize_children</summary>
    internal static void SummarizeChildren(Subtree self, Language language)
    {
        ref SubtreeHeapData heap = ref SubtreeArena.Get(self);

        // The summary is accumulated in locals and written into the node once, after the
        // loop. The node is the same for every child, and the C code simply writes it on
        // every step; the arithmetic here is the same, in the same order, and the values
        // written at the end are the ones the last step of the C code leaves behind.
        SubtreeFlags flags = heap.Flags & ~(
            SubtreeFlags.HasExternalTokens |
            SubtreeFlags.DependsOnColumn |
            SubtreeFlags.HasExternalScannerStateChange);
        uint errorCost = 0;
        uint namedChildCount = 0;
        uint visibleChildCount = 0;
        uint visibleDescendantCount = 0;
        int dynamicPrecedence = 0;
        ushort repeatDepth = 0;
        bool parseStateNone = false;

        // With no children the loop does not run, and the C code then leaves padding, size
        // and lookahead_bytes as they were, so start from the current values.
        Length padding = heap.Padding;
        Length size = heap.Size;
        ushort symbol = heap.Symbol;
        uint childCount = heap.ChildCount;

        if (childCount > 0)
        {
            uint structuralIndex = 0;
            uint productionId = heap.ProductionId;
            LanguageTables tables = language.Tables;
            uint maxAliasSequenceLength = (uint)tables.MaxAliasSequenceLength;
            ushort[]? aliasSequences = tables.AliasSequences;
            int aliasBase = productionId != 0 && aliasSequences is not null
                ? (int)(productionId * maxAliasSequenceLength)
                : -1;
            SymbolMetadata[] symbolMetadata = tables.SymbolMetadata!;
            uint lookaheadEndByte = 0;
            bool isErrorNode = symbol == BuiltinSymbols.Error || symbol == BuiltinSymbols.ErrorRepeat;

            ushort firstLeafSymbol = 0;
            ushort firstLeafParseState = 0;
            ushort firstSymbol = 0;
            uint firstRepeatDepth = 0;
            uint lastRepeatDepth = 0;
            bool firstFragileLeft = false;
            bool lastFragileRight = false;

            uint sizeBytesAcc = size.Bytes;
            uint sizeRowAcc = size.Extent.Row;
            uint sizeColAcc = size.Extent.Column;
            uint padBytes0 = padding.Bytes;

            ref Subtree childrenBase = ref MemoryMarshal.GetArrayDataReference(
                SubtreeArena.GetChildren(heap.ChildrenHandle));
            for (uint i = 0; i < childCount; i++)
            {
                Subtree child = Unsafe.Add(ref childrenBase, (nint)i);
                ulong raw = child.Value;

                ushort childSymbol;
                bool childExtra, childVisible, childNamed;
                uint childLookaheadBytes;

                if ((raw & 1UL) != 0)
                {
                    // Inline leaf: decode directly from the 64-bit value once, without
                    // calling properties that re-check IsInline or reading unused heap fields.
                    childSymbol = (ushort)((raw >> 8) & 0xFF);
                    childVisible = (raw & (1UL << 1)) != 0;
                    childNamed = (raw & (1UL << 2)) != 0;
                    childExtra = (raw & (1UL << 3)) != 0;

                    if ((i == 0 || sizeRowAcc == 0) && (raw & (1UL << 7)) != 0)
                    {
                        flags |= SubtreeFlags.DependsOnColumn;
                    }

                    uint padBytes = (uint)((raw >> 48) & 0xFF);
                    uint padRow = (uint)((raw >> 40) & 0x0F);
                    uint padCol = (uint)((raw >> 32) & 0xFF);
                    uint sizeBytes = (uint)((raw >> 56) & 0xFF);
                    childLookaheadBytes = (uint)((raw >> 44) & 0x0F);

                    if (i == 0)
                    {
                        padBytes0 = padBytes;
                        padding = new Length(padBytes, new Point(padRow, padCol));
                        sizeBytesAcc = sizeBytes;
                        sizeRowAcc = 0;
                        sizeColAcc = sizeBytes;
                        firstLeafSymbol = childSymbol;
                        firstLeafParseState = (ushort)((raw >> 16) & 0xFFFF);
                        firstSymbol = childSymbol;
                    }
                    else
                    {
                        sizeBytesAcc += padBytes + sizeBytes;
                        uint totalCol = padCol + sizeBytes;
                        if (padRow > 0)
                        {
                            sizeRowAcc += padRow;
                            sizeColAcc = totalCol;
                        }
                        else
                        {
                            sizeColAcc += totalCol;
                        }
                    }

                    if ((raw & (1UL << 5)) != 0)
                    {
                        errorCost += ErrorCosts.ErrorCostPerMissingTree + ErrorCosts.ErrorCostPerRecovery;
                    }

                    // Inline symbol fits in 8 bits, so it is never Error (0xFFFF) or ErrorRepeat (0xFFFE).
                    if (isErrorNode && !childExtra && childVisible)
                    {
                        errorCost += ErrorCosts.ErrorCostPerSkippedTree;
                    }

                    ushort aliasSymbol = 0;
                    if (!childExtra && childSymbol != 0 && aliasBase >= 0 && structuralIndex < maxAliasSequenceLength)
                    {
                        aliasSymbol = Unsafe.Add(
                            ref MemoryMarshal.GetArrayDataReference(aliasSequences!),
                            (nint)(aliasBase + (int)structuralIndex));
                    }

                    if (aliasSymbol != 0)
                    {
                        visibleDescendantCount++;
                        visibleChildCount++;
                        bool aliasNamed = aliasSymbol == BuiltinSymbols.Error ||
                            (aliasSymbol != BuiltinSymbols.ErrorRepeat &&
                             Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(symbolMetadata), (nint)aliasSymbol).Named);
                        if (aliasNamed)
                        {
                            namedChildCount++;
                        }
                    }
                    else if (childVisible)
                    {
                        visibleDescendantCount++;
                        visibleChildCount++;
                        if (childNamed)
                        {
                            namedChildCount++;
                        }
                    }

                    if (i + 1 == childCount)
                    {
                        lastFragileRight = false;
                        lastRepeatDepth = 0;
                    }
                }
                else
                {
                    ref SubtreeHeapData c = ref SubtreeArena.Get(child);
                    SubtreeFlags childFlags = c.Flags;
                    childSymbol = c.Symbol;
                    uint grandchildCount = c.ChildCount;
                    childVisible = (childFlags & SubtreeFlags.Visible) != 0;
                    childNamed = (childFlags & SubtreeFlags.Named) != 0;
                    childExtra = (childFlags & SubtreeFlags.Extra) != 0;

                    if ((i == 0 || sizeRowAcc == 0) && (childFlags & SubtreeFlags.DependsOnColumn) != 0)
                    {
                        flags |= SubtreeFlags.DependsOnColumn;
                    }

                    if ((childFlags & SubtreeFlags.HasExternalScannerStateChange) != 0)
                    {
                        flags |= SubtreeFlags.HasExternalScannerStateChange;
                    }

                    Length childPadding = c.Padding;
                    Length childSize = c.Size;
                    childLookaheadBytes = c.LookaheadBytes;

                    if (i == 0)
                    {
                        padBytes0 = childPadding.Bytes;
                        padding = childPadding;
                        sizeBytesAcc = childSize.Bytes;
                        sizeRowAcc = childSize.Extent.Row;
                        sizeColAcc = childSize.Extent.Column;
                        firstSymbol = childSymbol;
                        if (grandchildCount == 0)
                        {
                            firstLeafSymbol = childSymbol;
                            firstLeafParseState = c.ParseState;
                            firstRepeatDepth = (childFlags & SubtreeFlags.HasExternalTokens) != 0
                                ? c.ExternalScannerState.UnionRepeatDepth
                                : c.RepeatDepth;
                        }
                        else
                        {
                            firstLeafSymbol = c.FirstLeafSymbol;
                            firstLeafParseState = c.FirstLeafParseState;
                            firstRepeatDepth = c.RepeatDepth;
                        }

                        firstFragileLeft = (childFlags & SubtreeFlags.FragileLeft) != 0;
                    }
                    else
                    {
                        sizeBytesAcc += childPadding.Bytes + childSize.Bytes;
                        uint padRow = childPadding.Extent.Row;
                        uint csRow = childSize.Extent.Row;
                        uint totalRow = padRow + csRow;
                        uint totalCol = csRow > 0
                            ? childSize.Extent.Column
                            : childPadding.Extent.Column + childSize.Extent.Column;
                        if (totalRow > 0)
                        {
                            sizeRowAcc += totalRow;
                            sizeColAcc = totalCol;
                        }
                        else
                        {
                            sizeColAcc += totalCol;
                        }
                    }

                    uint childErrorCost = (childFlags & SubtreeFlags.IsMissing) != 0
                        ? ErrorCosts.ErrorCostPerMissingTree + ErrorCosts.ErrorCostPerRecovery
                        : c.ErrorCost;

                    if (childSymbol == BuiltinSymbols.ErrorRepeat)
                    {
                        // Refund an _ERROR child's extent penalty, which this node re-charges
                        // as part of its own extent below, so that the grouping is cost-neutral.
                        uint extentCost = ErrorExtentCost(childSize);
                        errorCost += childErrorCost - extentCost;
                    }
                    else
                    {
                        errorCost += childErrorCost;
                        if (isErrorNode)
                        {
                            if (!childExtra && !(childSymbol == BuiltinSymbols.Error && grandchildCount == 0))
                            {
                                if (childVisible)
                                {
                                    errorCost += ErrorCosts.ErrorCostPerSkippedTree;
                                }
                                else if (grandchildCount > 0)
                                {
                                    errorCost += ErrorCosts.ErrorCostPerSkippedTree * c.VisibleChildCount;
                                }
                            }
                        }
                    }

                    if (grandchildCount > 0)
                    {
                        dynamicPrecedence += c.DynamicPrecedence;
                        visibleDescendantCount += c.VisibleDescendantCount;
                    }

                    ushort aliasSymbol = 0;
                    if (!childExtra && childSymbol != 0 && aliasBase >= 0 && structuralIndex < maxAliasSequenceLength)
                    {
                        aliasSymbol = Unsafe.Add(
                            ref MemoryMarshal.GetArrayDataReference(aliasSequences!),
                            (nint)(aliasBase + (int)structuralIndex));
                    }

                    if (aliasSymbol != 0)
                    {
                        visibleDescendantCount++;
                        visibleChildCount++;
                        bool aliasNamed = aliasSymbol == BuiltinSymbols.Error ||
                            (aliasSymbol != BuiltinSymbols.ErrorRepeat &&
                             Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(symbolMetadata), (nint)aliasSymbol).Named);
                        if (aliasNamed)
                        {
                            namedChildCount++;
                        }
                    }
                    else if (childVisible)
                    {
                        visibleDescendantCount++;
                        visibleChildCount++;
                        if (childNamed)
                        {
                            namedChildCount++;
                        }
                    }
                    else if (grandchildCount > 0)
                    {
                        visibleChildCount += c.VisibleChildCount;
                        namedChildCount += c.NamedChildCount;
                    }

                    if ((childFlags & SubtreeFlags.HasExternalTokens) != 0)
                    {
                        flags |= SubtreeFlags.HasExternalTokens;
                    }

                    if (childSymbol == BuiltinSymbols.Error)
                    {
                        flags |= SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight;
                        parseStateNone = true;
                    }

                    if (i + 1 == childCount)
                    {
                        lastFragileRight = (childFlags & SubtreeFlags.FragileRight) != 0;
                        lastRepeatDepth = grandchildCount == 0 && (childFlags & SubtreeFlags.HasExternalTokens) != 0
                            ? c.ExternalScannerState.UnionRepeatDepth
                            : c.RepeatDepth;
                    }
                }

                uint childLookaheadEndByte = padBytes0 + sizeBytesAcc + childLookaheadBytes;
                if (childLookaheadEndByte > lookaheadEndByte)
                {
                    lookaheadEndByte = childLookaheadEndByte;
                }

                if (!childExtra)
                {
                    structuralIndex++;
                }
            }

            size = new Length(sizeBytesAcc, new Point(sizeRowAcc, sizeColAcc));
            uint lookaheadBytes = unchecked(lookaheadEndByte - sizeBytesAcc - padBytes0);

            if (isErrorNode)
            {
                errorCost += ErrorExtentCost(size);
            }

            heap.FirstLeafSymbol = firstLeafSymbol;
            heap.FirstLeafParseState = firstLeafParseState;

            if (firstFragileLeft)
            {
                flags |= SubtreeFlags.FragileLeft;
            }

            if (lastFragileRight)
            {
                flags |= SubtreeFlags.FragileRight;
            }

            if (childCount >= 2 &&
                (flags & (SubtreeFlags.Visible | SubtreeFlags.Named)) == 0 &&
                firstSymbol == symbol)
            {
                repeatDepth = firstRepeatDepth > lastRepeatDepth
                    ? (ushort)(firstRepeatDepth + 1)
                    : (ushort)(lastRepeatDepth + 1);
            }

            heap.Padding = padding;
            heap.Size = size;
            heap.LookaheadBytes = lookaheadBytes;
        }
        else
        {
            uint lookaheadBytes = unchecked(0u - size.Bytes - padding.Bytes);
            if (symbol == BuiltinSymbols.Error || symbol == BuiltinSymbols.ErrorRepeat)
            {
                errorCost += ErrorExtentCost(size);
            }

            heap.LookaheadBytes = lookaheadBytes;
        }

        // One write per field, into the same node (the union fields keep their own offsets).
        heap.ErrorCost = errorCost;
        heap.NamedChildCount = namedChildCount;
        heap.VisibleChildCount = visibleChildCount;
        heap.VisibleDescendantCount = visibleDescendantCount;
        heap.DynamicPrecedence = dynamicPrecedence;
        heap.RepeatDepth = repeatDepth;
        heap.Flags = flags;
        if (parseStateNone)
        {
            heap.ParseState = TreeStateNone;
        }
    }

    /// <summary>ts_subtree_new_node: takes the contents of children.</summary>
    internal static Subtree NewNode(ushort symbol, ref TsArray<Subtree> children, uint productionId, Language language)
    {
        SymbolMetadata metadata = language.GetSymbolMetadata(symbol);
        bool fragile = symbol == BuiltinSymbols.Error || symbol == BuiltinSymbols.ErrorRepeat;

        Subtree result = SubtreeArena.Allocate();
        ref SubtreeHeapData heap = ref SubtreeArena.Get(result);
        heap.RefCount = 1;
        heap.Padding = Length.Zero;
        heap.Size = Length.Zero;
        heap.LookaheadBytes = 0;
        heap.ErrorCost = 0;
        heap.ChildCount = children.Size;
        heap.Symbol = symbol;
        heap.ParseState = 0;
        heap.Flags = MakeFlags(
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
            false);
        // The union (offset 0) holds one interpretation (subtree.h); for a node with children
        // it is the non-terminal one, which SummarizeChildren writes in full. The reset of the
        // external scanner state handle that Allocate does is overwritten here, as in C.
        heap.VisibleDescendantCount = 0;
        heap.ProductionId = (ushort)productionId;
        heap.FirstLeafSymbol = 0;
        heap.FirstLeafParseState = 0;
        heap.ChildrenHandle = -1;

        // The node takes the children over: the array of the caller must not be used
        // any more (in C the node data is stored behind the children of the same block).
        // Readers use the array length as the child count, so the array is taken as it is
        // only when its length is exactly the child count, else copied to that length.
        Subtree[] childArray = children.Contents is { } contents && contents.Length == (int)children.Size
            ? contents
            : CopyChildren(children.AsSpan());
        heap.ChildrenHandle = SubtreeArena.StoreChildren(result.Handle, childArray);
        if (children.Contents is { } dropped && !ReferenceEquals(dropped, childArray))
        {
            // The node took a copy: the storage of the caller goes back to the pool (C:
            // new_node takes the array over, the caller must not use it any more).
            SubtreeArena.ReturnChildren(dropped);
        }

        children.Contents = null;
        children.Size = 0;
        children.Capacity = 0;

        SummarizeChildren(result, language);
        return result;
    }

    /// <summary>ts_subtree_new_error_node</summary>
    internal static Subtree NewErrorNode(ref TsArray<Subtree> children, bool extra, Language language)
    {
        Subtree result = NewNode(BuiltinSymbols.Error, ref children, 0, language);
        SubtreeArena.Get(result).SetFlag(SubtreeFlags.Extra, extra);
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
            SubtreeArena.Get(result).SetFlag(SubtreeFlags.IsMissing, true);
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
            SubtreeArena.Get(self).SetFlag(SubtreeFlags.Extra, isExtra);
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

        ref SubtreeHeapData heap = ref SubtreeArena.Get(self);
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

        ref SubtreeHeapData heap = ref SubtreeArena.Get(self);
        if (Interlocked.Decrement(ref heap.RefCount) == 0)
        {
            pool.TreeStack.Push(self);
        }

        while (pool.TreeStack.Size > 0)
        {
            Subtree tree = pool.TreeStack.Pop();
            ref SubtreeHeapData treeHeap = ref SubtreeArena.Get(tree);
            if (treeHeap.ChildCount > 0 && treeHeap.ChildrenHandle >= 0)
            {
                Subtree[] children = SubtreeArena.GetChildren(treeHeap.ChildrenHandle);
                uint childCount = treeHeap.ChildCount;
                for (uint i = 0; i < childCount; i++)
                {
                    Subtree child = children[i];
                    if (child.IsInline)
                    {
                        continue;
                    }

                    ref SubtreeHeapData childHeap = ref SubtreeArena.Get(child);
                    if (Interlocked.Decrement(ref childHeap.RefCount) == 0)
                    {
                        pool.TreeStack.Push(child);
                    }
                }

                SubtreeArena.FreeChildren(treeHeap.ChildrenHandle);
                treeHeap.ChildrenHandle = -1;
                SubtreeArena.Free(tree.Handle);
            }
            else
            {
                if (treeHeap.HasFlag(SubtreeFlags.HasExternalTokens))
                {
                    ExternalScannerState.DeleteData(ref treeHeap.ExternalScannerState);
                }

                SubtreeArena.Free(tree.Handle);
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
        internal EditEntry(int parentHandle, int childIndex, Length start, Length oldEnd, Length newEnd)
        {
            ParentHandle = parentHandle;
            ChildIndex = childIndex;
            Start = start;
            OldEnd = oldEnd;
            NewEnd = newEnd;
        }

        internal int ParentHandle { get; }

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
            -1,
            -1,
            new Length(edit.StartByte, edit.StartPoint),
            new Length(edit.OldEndByte, edit.OldEndPoint),
            new Length(edit.NewEndByte, edit.NewEndPoint)));

        while (stack.Size > 0)
        {
            EditEntry entry = stack.Pop();
            Subtree current = entry.ParentHandle < 0
                ? root
                : SubtreeArena.GetChildren(SubtreeArena.Get(entry.ParentHandle).ChildrenHandle)[entry.ChildIndex];
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
                    Subtree newHeap = SubtreeArena.Allocate();
                    ref SubtreeHeapData hd = ref SubtreeArena.Get(newHeap);
                    hd.RefCount = 1;
                    hd.Padding = padding;
                    hd.Size = size;
                    hd.LookaheadBytes = lookaheadBytes;
                    hd.ErrorCost = 0;
                    hd.ChildCount = 0;
                    hd.Symbol = result.Symbol;
                    hd.ParseState = result.ParseState;
                    hd.Flags = MakeFlags(
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
                        result.IsKeyword);
                    hd.ChildrenHandle = -1;
                    result = newHeap;
                }
            }
            else
            {
                ref SubtreeHeapData hd = ref SubtreeArena.Get(result);
                hd.Padding = padding;
                hd.Size = size;
            }

            if (result.IsInline)
            {
                result = result.WithInlineHasChanges(true);
            }
            else
            {
                SubtreeArena.Get(result).SetFlag(SubtreeFlags.HasChanges, true);
            }

            if (entry.ParentHandle < 0)
            {
                root = result;
            }
            else
            {
                SubtreeArena.GetChildren(SubtreeArena.Get(entry.ParentHandle).ChildrenHandle)[entry.ChildIndex] = result;
            }

            uint childCount = result.ChildCount;
            Length childLeft = Length.Zero;
            Length childRight = Length.Zero;
            Subtree[] resultChildren = childCount > 0
                ? SubtreeArena.GetChildren(SubtreeArena.Get(result).ChildrenHandle)
                : Array.Empty<Subtree>();
            int resultHandle = result.IsHeap ? result.Handle : -1;
            for (uint i = 0; i < childCount; i++)
            {
                Subtree child = resultChildren[(int)i];
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

                stack.Push(new EditEntry(resultHandle, (int)i, childStart, childOldEnd, childNewEnd));
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

        Dictionary<int, uint> ids = new Dictionary<int, uint>();
        uint nextId = 0;

        // The identity of a node is the address of the Subtree value in C: a heap node
        // keeps its address (visited twice, it keeps the number), an inline leaf is a
        // copy and always gets a fresh number. The first-appearance order is the pre-order
        // of the tree walk, the same as in the C code.
        uint NodeId(Subtree subtree)
        {
            if (subtree.IsHeap)
            {
                int handle = subtree.Handle;
                if (!ids.TryGetValue(handle, out uint id))
                {
                    id = nextId++;
                    ids.Add(handle, id);
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
                if (subtree.IsHeap) lookaheadChar = SubtreeArena.Get(subtree).LookaheadChar;
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

        ref SubtreeHeapData heap = ref SubtreeArena.Get(self);
        if (heap.HasFlag(SubtreeFlags.HasExternalTokens) && heap.ChildCount == 0)
        {
            return ExternalScannerState.FromData(in heap.ExternalScannerState);
        }

        return default;
    }

    /// <summary>ts_subtree_external_scanner_state_eq</summary>
    internal static bool ExternalScannerStateEq(Subtree self, Subtree other)
    {
        bool hasLeftState = false;
        uint leftLength = 0;
        if (!self.IsNull && !self.IsInline)
        {
            ref SubtreeHeapData heap = ref SubtreeArena.Get(self);
            hasLeftState = heap.HasFlag(SubtreeFlags.HasExternalTokens) && heap.ChildCount == 0;
            if (hasLeftState)
            {
                leftLength = heap.ExternalScannerState.Length;
            }
        }

        bool hasRightState = false;
        uint rightLength = 0;
        if (!other.IsNull && !other.IsInline)
        {
            ref SubtreeHeapData heap = ref SubtreeArena.Get(other);
            hasRightState = heap.HasFlag(SubtreeFlags.HasExternalTokens) && heap.ChildCount == 0;
            if (hasRightState)
            {
                rightLength = heap.ExternalScannerState.Length;
            }
        }

        if (!hasLeftState && !hasRightState)
        {
            return true;
        }

        if (!hasLeftState)
        {
            return rightLength == 0;
        }

        if (!hasRightState)
        {
            return leftLength == 0;
        }

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

                    if (node.IsError && node.ChildCount == 0 && node.Size.Bytes > 0)
                    {
                        builder.Append("(UNEXPECTED ");
                        WriteCharToString(builder, SubtreeArena.Get(node).LookaheadChar);
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
                    frame.ProductionId = SubtreeArena.Get(node).ProductionId;
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
