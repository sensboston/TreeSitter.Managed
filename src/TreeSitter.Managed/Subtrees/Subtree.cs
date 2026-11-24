// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/subtree.h (types and inline accessors).
// Memory model: docs/ARCHITECTURE.md decision D1.
//
// Subtree = { ulong Inline; SubtreeHeap? Heap }:
// - NULL_SUBTREE: Heap == null and bit 0 of Inline is 0 (Inline == 0).
// - inline leaf: Heap == null and bit 0 of Inline is 1. Bits follow the little-endian
//   SubtreeInlineData of subtree.h:
//     bit 0 is_inline, 1 visible, 2 named, 3 extra, 4 has_changes, 5 is_missing,
//     6 is_keyword, 7 depends_on_column,
//     bits 8-15 symbol, 16-31 parse_state, 32-39 padding_columns,
//     40-43 padding_rows, 44-47 lookahead_bytes, 48-55 padding_bytes, 56-63 size_bytes.
// - heap subtree: Heap != null (Inline is 0).
// Every accessor below is the inline function of subtree.h with the same name and logic.
//
// The bit positions of the inline data are the ones of the little-endian C layout:
// the first bit field byte holds is_inline, visible, named, extra, has_changes,
// is_missing, is_keyword and depends_on_column, then the symbol byte, then the
// parse state (2 bytes), then the size bytes.

using System.Runtime.CompilerServices;

namespace TreeSitter.Subtrees;

/// <summary>Flags of SubtreeHeapData (the 11 bit fields, same order).</summary>
[Flags]
internal enum SubtreeFlags : ushort
{
    None = 0,
    Visible = 1 << 0,
    Named = 1 << 1,
    Extra = 1 << 2,
    FragileLeft = 1 << 3,
    FragileRight = 1 << 4,
    HasChanges = 1 << 5,
    HasExternalTokens = 1 << 6,
    HasExternalScannerStateChange = 1 << 7,
    DependsOnColumn = 1 << 8,
    IsMissing = 1 << 9,
    IsKeyword = 1 << 10,
}

/// <summary>SubtreeHeapData. RefCount is changed only with Interlocked (ts_subtree_retain/release).</summary>
internal sealed class SubtreeHeap
{
    internal int RefCount;
    internal Length Padding;
    internal Length Size;
    internal uint LookaheadBytes;
    internal uint ErrorCost;
    internal uint ChildCount;
    internal ushort Symbol;
    internal ushort ParseState;
    internal SubtreeFlags Flags;

    /// <summary>The children (C: stored before the header). Length may be larger than ChildCount.</summary>
    internal Subtree[]? Children;

    // Union part, non-terminal (ChildCount > 0):
    internal uint VisibleChildCount;
    internal uint NamedChildCount;
    internal uint VisibleDescendantCount;
    internal int DynamicPrecedence;
    internal ushort RepeatDepth;
    internal ushort ProductionId;
    internal ushort FirstLeafSymbol;
    internal ushort FirstLeafParseState;

    // Union part, external token (ChildCount == 0 and HasExternalTokens):
    internal ExternalScannerState ExternalScannerState;

    // Union part, error leaf (ChildCount == 0 and Symbol == BuiltinSymbols.Error):
    internal int LookaheadChar;

    /// <summary>Sets or clears one flag of the heap data.</summary>
    /// <param name="flag">The flag to change.</param>
    /// <param name="value">True to set the flag, false to clear it.</param>
    internal void SetFlag(SubtreeFlags flag, bool value) =>
        Flags = value ? Flags | flag : Flags & ~flag;

    /// <summary>True when the flag is set.</summary>
    /// <param name="flag">The flag to test.</param>
    internal bool HasFlag(SubtreeFlags flag) => (Flags & flag) != 0;
}

/// <summary>Subtree / MutableSubtree of subtree.h. Mutability is a convention, as in C.</summary>
internal readonly struct Subtree : IEquatable<Subtree>
{
    // Bit positions of the inline data (see the file header).
    private const ulong InlineBit = 1UL << 0;
    private const ulong VisibleBit = 1UL << 1;
    private const ulong NamedBit = 1UL << 2;
    private const ulong ExtraBit = 1UL << 3;
    private const ulong HasChangesBit = 1UL << 4;
    private const ulong IsMissingBit = 1UL << 5;
    private const ulong IsKeywordBit = 1UL << 6;
    private const ulong DependsOnColumnBit = 1UL << 7;
    private const int SymbolShift = 8;
    private const int ParseStateShift = 16;
    private const int PaddingColumnsShift = 32;
    private const int PaddingRowsShift = 40;
    private const int LookaheadBytesShift = 44;
    private const int PaddingBytesShift = 48;
    private const int SizeBytesShift = 56;

    internal readonly ulong Inline;
    internal readonly SubtreeHeap? Heap;

    internal Subtree(ulong inline, SubtreeHeap? heap)
    {
        Inline = inline;
        Heap = heap;
    }

    /// <summary>NULL_SUBTREE</summary>
    internal static Subtree Null => default;

    internal bool IsNull => Heap is null && (Inline & 1) == 0;

    /// <summary>data.is_inline</summary>
    internal bool IsInline => Heap is null && (Inline & 1) != 0;

    /// <summary>Builds an inline leaf from the SubtreeInlineData fields (bit layout in the file header).</summary>
    internal static Subtree NewInline(
        ushort symbol, ushort parseState, bool visible, bool named, bool extra, bool hasChanges,
        bool isMissing, bool isKeyword, bool dependsOnColumn, byte paddingColumns, byte paddingRows,
        byte lookaheadBytes, byte paddingBytes, byte sizeBytes)
    {
        ulong inline = InlineBit;
        inline |= visible ? VisibleBit : 0;
        inline |= named ? NamedBit : 0;
        inline |= extra ? ExtraBit : 0;
        inline |= hasChanges ? HasChangesBit : 0;
        inline |= isMissing ? IsMissingBit : 0;
        inline |= isKeyword ? IsKeywordBit : 0;
        inline |= dependsOnColumn ? DependsOnColumnBit : 0;
        inline |= (ulong)symbol << SymbolShift;
        inline |= (ulong)parseState << ParseStateShift;
        inline |= (ulong)paddingColumns << PaddingColumnsShift;
        inline |= (ulong)(paddingRows & 0x0F) << PaddingRowsShift;
        inline |= (ulong)(lookaheadBytes & 0x0F) << LookaheadBytesShift;
        inline |= (ulong)paddingBytes << PaddingBytesShift;
        inline |= (ulong)sizeBytes << SizeBytesShift;
        return new Subtree(inline, null);
    }

    /// <summary>Returns a copy with the is_inline extra bit set or cleared (ts_subtree_set_extra on an inline subtree).</summary>
    internal Subtree WithInlineExtra(bool extra)
    {
        ulong inline = extra ? Inline | ExtraBit : Inline & ~ExtraBit;
        return new Subtree(inline, Heap);
    }

    /// <summary>
    /// Returns a copy with another symbol, named and visible bit (ts_subtree_set_symbol on an
    /// inline subtree). The symbol is truncated to 8 bits, as the C code does (uint8_t symbol).
    /// </summary>
    internal Subtree WithInlineSymbol(ushort symbol, bool named, bool visible)
    {
        // ts_subtree_set_symbol assigns visible and named, so the old bits are cleared first.
        ulong inline = Inline & ~(((ulong)0xFF << SymbolShift) | NamedBit | VisibleBit);
        inline |= (ulong)(byte)symbol << SymbolShift;
        inline |= named ? NamedBit : 0;
        inline |= visible ? VisibleBit : 0;
        return new Subtree(inline, Heap);
    }

    /// <summary>Returns a copy with the is_missing bit set or cleared (ts_subtree_new_missing_leaf).</summary>
    internal Subtree WithInlineMissing(bool missing)
    {
        ulong inline = missing ? Inline | IsMissingBit : Inline & ~IsMissingBit;
        return new Subtree(inline, Heap);
    }

    /// <summary>Returns a copy with the has_changes bit set or cleared (ts_subtree_set_has_changes).</summary>
    internal Subtree WithInlineHasChanges(bool hasChanges)
    {
        ulong inline = hasChanges ? Inline | HasChangesBit : Inline & ~HasChangesBit;
        return new Subtree(inline, Heap);
    }

    // ts_subtree_symbol, ts_subtree_visible, ... (SUBTREE_GET)
    internal ushort Symbol => IsInline ? (ushort)((Inline >> SymbolShift) & 0xFF) : Heap!.Symbol;
    internal bool Visible => IsInline ? (Inline & VisibleBit) != 0 : Heap!.HasFlag(SubtreeFlags.Visible);
    internal bool Named => IsInline ? (Inline & NamedBit) != 0 : Heap!.HasFlag(SubtreeFlags.Named);
    internal bool Extra => IsInline ? (Inline & ExtraBit) != 0 : Heap!.HasFlag(SubtreeFlags.Extra);
    internal bool HasChanges => IsInline ? (Inline & HasChangesBit) != 0 : Heap!.HasFlag(SubtreeFlags.HasChanges);
    internal bool IsMissing => IsInline ? (Inline & IsMissingBit) != 0 : Heap!.HasFlag(SubtreeFlags.IsMissing);
    internal bool IsKeyword => IsInline ? (Inline & IsKeywordBit) != 0 : Heap!.HasFlag(SubtreeFlags.IsKeyword);
    internal ushort ParseState => IsInline ? (ushort)((Inline >> ParseStateShift) & 0xFFFF) : Heap!.ParseState;
    internal uint LookaheadBytes =>
        IsInline ? (uint)((Inline >> LookaheadBytesShift) & 0x0F) : Heap!.LookaheadBytes;

    /// <summary>ts_subtree_leaf_symbol</summary>
    internal ushort LeafSymbol
    {
        get
        {
            if (IsInline)
            {
                return (ushort)((Inline >> SymbolShift) & 0xFF);
            }

            SubtreeHeap heap = Heap!;
            return heap.ChildCount == 0 ? heap.Symbol : heap.FirstLeafSymbol;
        }
    }

    /// <summary>ts_subtree_leaf_parse_state</summary>
    internal ushort LeafParseState
    {
        get
        {
            if (IsInline)
            {
                return (ushort)((Inline >> ParseStateShift) & 0xFFFF);
            }

            SubtreeHeap heap = Heap!;
            return heap.ChildCount == 0 ? heap.ParseState : heap.FirstLeafParseState;
        }
    }

    /// <summary>ts_subtree_padding</summary>
    internal Length Padding
    {
        get
        {
            if (IsInline)
            {
                uint bytes = (uint)((Inline >> PaddingBytesShift) & 0xFF);
                uint row = (uint)((Inline >> PaddingRowsShift) & 0x0F);
                uint column = (uint)((Inline >> PaddingColumnsShift) & 0xFF);
                return new Length(bytes, new Point(row, column));
            }

            return Heap!.Padding;
        }
    }

    /// <summary>ts_subtree_size</summary>
    internal Length Size
    {
        get
        {
            if (IsInline)
            {
                uint bytes = (uint)((Inline >> SizeBytesShift) & 0xFF);
                return new Length(bytes, new Point(0, bytes));
            }

            return Heap!.Size;
        }
    }

    /// <summary>ts_subtree_total_size</summary>
    internal Length TotalSize => Length.Add(Padding, Size);

    /// <summary>ts_subtree_total_bytes</summary>
    internal uint TotalBytes => TotalSize.Bytes;

    /// <summary>ts_subtree_child_count</summary>
    internal uint ChildCount => IsInline ? 0 : Heap!.ChildCount;

    /// <summary>SubtreeHeapData.named_child_count; 0 for inline subtrees (no C accessor).</summary>
    internal uint NamedChildCount => IsInline ? 0 : Heap!.NamedChildCount;

    /// <summary>ts_subtree_children: ChildCount entries; empty for inline subtrees.</summary>
    internal ReadOnlySpan<Subtree> Children
    {
        get
        {
            if (IsInline)
            {
                return ReadOnlySpan<Subtree>.Empty;
            }

            SubtreeHeap heap = Heap!;
            return heap.Children is null
                ? ReadOnlySpan<Subtree>.Empty
                : heap.Children.AsSpan(0, (int)heap.ChildCount);
        }
    }

    /// <summary>ts_subtree_repeat_depth</summary>
    internal uint RepeatDepth =>
        IsInline ? 0u
        : Heap!.ChildCount == 0 && Heap.HasFlag(SubtreeFlags.HasExternalTokens) ? Heap.ExternalScannerState.UnionRepeatDepth
        : Heap.RepeatDepth;

    /// <summary>ts_subtree_is_repetition</summary>
    internal bool IsRepetition
    {
        get
        {
            if (IsInline)
            {
                return false;
            }

            SubtreeHeap heap = Heap!;
            return !heap.HasFlag(SubtreeFlags.Named) &&
                !heap.HasFlag(SubtreeFlags.Visible) &&
                heap.ChildCount != 0;
        }
    }

    /// <summary>ts_subtree_visible_descendant_count</summary>
    internal uint VisibleDescendantCount
    {
        get
        {
            if (IsInline)
            {
                return 0;
            }

            SubtreeHeap heap = Heap!;
            return heap.ChildCount == 0 ? 0 : heap.VisibleDescendantCount;
        }
    }

    /// <summary>ts_subtree_visible_child_count</summary>
    internal uint VisibleChildCount
    {
        get
        {
            if (IsInline)
            {
                return 0;
            }

            SubtreeHeap heap = Heap!;
            return heap.ChildCount > 0 ? heap.VisibleChildCount : 0;
        }
    }

    /// <summary>ts_subtree_error_cost</summary>
    internal uint ErrorCost
    {
        get
        {
            if (IsMissing)
            {
                return ErrorCosts.ErrorCostPerMissingTree + ErrorCosts.ErrorCostPerRecovery;
            }

            return IsInline ? 0 : Heap!.ErrorCost;
        }
    }

    /// <summary>ts_subtree_dynamic_precedence</summary>
    internal int DynamicPrecedence
    {
        get
        {
            if (IsInline)
            {
                return 0;
            }

            SubtreeHeap heap = Heap!;
            return heap.ChildCount == 0 ? 0 : heap.DynamicPrecedence;
        }
    }

    /// <summary>ts_subtree_production_id</summary>
    internal ushort ProductionId
    {
        get
        {
            if (IsInline)
            {
                return 0;
            }

            SubtreeHeap heap = Heap!;
            return heap.ChildCount > 0 ? heap.ProductionId : (ushort)0;
        }
    }

    /// <summary>ts_subtree_fragile_left</summary>
    internal bool FragileLeft => IsInline ? false : Heap!.HasFlag(SubtreeFlags.FragileLeft);

    /// <summary>ts_subtree_fragile_right</summary>
    internal bool FragileRight => IsInline ? false : Heap!.HasFlag(SubtreeFlags.FragileRight);

    /// <summary>ts_subtree_has_external_tokens</summary>
    internal bool HasExternalTokens => IsInline ? false : Heap!.HasFlag(SubtreeFlags.HasExternalTokens);

    /// <summary>ts_subtree_has_external_scanner_state_change</summary>
    internal bool HasExternalScannerStateChange =>
        IsInline ? false : Heap!.HasFlag(SubtreeFlags.HasExternalScannerStateChange);

    /// <summary>ts_subtree_depends_on_column</summary>
    internal bool DependsOnColumn =>
        IsInline ? (Inline & DependsOnColumnBit) != 0 : Heap!.HasFlag(SubtreeFlags.DependsOnColumn);

    /// <summary>ts_subtree_is_fragile</summary>
    internal bool IsFragile
    {
        get
        {
            if (IsInline)
            {
                return false;
            }

            SubtreeHeap heap = Heap!;
            return heap.HasFlag(SubtreeFlags.FragileLeft) || heap.HasFlag(SubtreeFlags.FragileRight);
        }
    }

    /// <summary>ts_subtree_is_error</summary>
    internal bool IsError => Symbol == BuiltinSymbols.Error;

    /// <summary>ts_subtree_is_eof</summary>
    internal bool IsEof => Symbol == BuiltinSymbols.End;

    /// <summary>Same subtree: same heap object, or the same inline bits (C compares the union).</summary>
    public bool Equals(Subtree other) => Inline == other.Inline && ReferenceEquals(Heap, other.Heap);

    public override bool Equals(object? obj) => obj is Subtree other && Equals(other);

    public override int GetHashCode() => Heap is null ? Inline.GetHashCode() : RuntimeHelpers.GetHashCode(Heap);
}
