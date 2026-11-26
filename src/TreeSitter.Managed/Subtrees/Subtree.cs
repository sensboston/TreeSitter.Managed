// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/subtree.h (types and inline accessors).
// Memory model: docs/ARCHITECTURE.md decision D1, ARENA.md A1/A2.
//
// Subtree = ulong Value:
// - NULL_SUBTREE: Value == 0.
// - inline leaf: bit 0 is 1. Bits follow the little-endian SubtreeInlineData of subtree.h:
//     bit 0 is_inline, 1 visible, 2 named, 3 extra, 4 has_changes, 5 is_missing,
//     6 is_keyword, 7 depends_on_column,
//     bits 8-15 symbol, 16-31 parse_state, 32-39 padding_columns,
//     40-43 padding_rows, 44-47 lookahead_bytes, 48-55 padding_bytes, 56-63 size_bytes.
// - heap subtree: bit 0 is 0, Value != 0. (Value >> 1) & 0xFFFFFFFF is the arena handle.
//   In DEBUG, high bits carry a generation counter.
//
// Subtree has no reference field, so Subtree[] is an array of an unmanaged type: the GC
// never scans it and stores into it have no write barrier.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

/// <summary>
/// ExternalScannerState: short data inline (24 bytes), longer data in an array.
/// Used as a managed convenience value; heap storage uses <see cref="ExternalScannerStateData"/>.
/// </summary>
internal struct ExternalScannerState
{
    private const int ShortCapacity = ExternalScannerStateData.ShortCapacity;

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

    /// <summary>Creates from arena heap storage.</summary>
    internal static ExternalScannerState FromData(in ExternalScannerStateData data)
    {
        ExternalScannerState state = default;
        state._length = data.Length;
        if (data.Length > ShortCapacity)
        {
            byte[] src = SubtreeArena.GetLongExternalState(data.LongHandle);
            state._long = src.AsSpan(0, (int)data.Length).ToArray();
        }
        else if (data.Length > 0)
        {
            Span<byte> dst = MemoryMarshal.CreateSpan(ref state._short[0], ShortCapacity);
            ReadOnlySpan<byte> src = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data.Short[0]), (int)data.Length);
            src.CopyTo(dst);
        }

        return state;
    }

    /// <summary>Writes this state into arena heap storage, allocating a long-state handle if needed.</summary>
    internal void WriteTo(ref ExternalScannerStateData data)
    {
        // In the union of ExternalScannerState (subtree.h) the long data handle covers the
        // short data, so only the length says which of the two is live: free the old long
        // state only when the current length makes it a long one, as ts_external_scanner_state_delete.
        if (data.Length > ShortCapacity && data.LongHandle > 0)
        {
            SubtreeArena.FreeLongExternalState(data.LongHandle);
        }

        if (_length > ShortCapacity)
        {
            byte[] copy = _long!.AsSpan(0, (int)_length).ToArray();
            data.Short = default;
            data.LongHandle = SubtreeArena.StoreLongExternalState(copy);
            data.Length = _length;
        }
        else
        {
            data.Short = default;
            if (_length > 0)
            {
                ReadOnlySpan<byte> src = MemoryMarshal.CreateReadOnlySpan(ref _short[0], (int)_length);
                Span<byte> dst = MemoryMarshal.CreateSpan(ref data.Short[0], ShortCapacity);
                src.CopyTo(dst);
            }

            data.Length = _length;
        }
    }

    /// <summary>Copies arena heap storage (clones long-state handle content).</summary>
    internal static void CopyData(in ExternalScannerStateData source, ref ExternalScannerStateData dest)
    {
        // See WriteTo: the length, not the overlapping handle, says whether the data is long.
        if (dest.Length > ShortCapacity && dest.LongHandle > 0)
        {
            SubtreeArena.FreeLongExternalState(dest.LongHandle);
        }

        dest = default;
        dest.Length = source.Length;
        if (source.Length > ShortCapacity)
        {
            byte[] src = SubtreeArena.GetLongExternalState(source.LongHandle);
            byte[] copy = src.AsSpan(0, (int)source.Length).ToArray();
            dest.LongHandle = SubtreeArena.StoreLongExternalState(copy);
        }
        else
        {
            if (source.Length > 0)
            {
                ReadOnlySpan<byte> src = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in source.Short[0]), (int)source.Length);
                Span<byte> dst = MemoryMarshal.CreateSpan(ref dest.Short[0], ShortCapacity);
                src.CopyTo(dst);
            }
        }
    }

    /// <summary>Frees long-state side table entry in arena storage.</summary>
    internal static void DeleteData(ref ExternalScannerStateData data)
    {
        // See WriteTo: for short data the handle is the first bytes of the data itself.
        if (data.Length > ShortCapacity && data.LongHandle > 0)
        {
            SubtreeArena.FreeLongExternalState(data.LongHandle);
        }

        data = default;
    }

    /// <summary>Returns the data span from arena storage.</summary>
    internal static ReadOnlySpan<byte> DataOf(in ExternalScannerStateData data)
    {
        if (data.Length > ShortCapacity)
        {
            return SubtreeArena.GetLongExternalState(data.LongHandle).AsSpan(0, (int)data.Length);
        }

        if (data.Length == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data.Short[0]), (int)data.Length);
    }

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
            result._long = _long!.AsSpan(0, (int)_length).ToArray();
        }

        return result;
    }

    /// <summary>ts_external_scanner_state_delete</summary>
    internal void Delete()
    {
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

            ref ExternalScannerState self = ref Unsafe.AsRef(in this);
            return MemoryMarshal.CreateReadOnlySpan(ref self._short[0], (int)_length);
        }
    }

    internal readonly uint Length => _length;

    /// <summary>ts_external_scanner_state_eq</summary>
    internal readonly bool Eq(ReadOnlySpan<byte> buffer) =>
        _length == (uint)buffer.Length && Data.SequenceEqual(buffer);
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

    internal readonly ulong Value;

    private Subtree(ulong value)
    {
        Value = value;
    }

    // A heap subtree holds the address of its SubtreeHeapData in the pinned segments of
    // SubtreeArena (bit 0 is 0, as for the pointer of the C union). Debug builds keep a
    // generation in the top 16 bits (user space addresses use fewer than 48 bits).
#if DEBUG
    private const ulong PointerMask = 0x0000_FFFF_FFFF_FFFFUL;

    internal static Subtree FromHeapPointer(nint pointer, ushort generation) =>
        new Subtree((ulong)pointer | ((ulong)generation << 48));

    internal ushort Generation => (ushort)(Value >> 48);

    internal nint Pointer => (nint)(Value & PointerMask);
#else
    internal static Subtree FromHeapPointer(nint pointer) => new Subtree((ulong)pointer);

    internal nint Pointer => (nint)Value;
#endif

    /// <summary>The arena handle of a heap subtree (segment and slot).</summary>
    internal int Handle => SubtreeArena.Get(this).SelfHandle;

    /// <summary>NULL_SUBTREE</summary>
    internal static Subtree Null => default;

#if DEBUG
    /// <summary>Debug only: the value written into child arrays returned to the arena pool.</summary>
    internal static Subtree Poison => new Subtree(0xFFFF_FFFF_FFFF_FF01UL);
#endif

    internal bool IsNull => Value == 0;

    /// <summary>data.is_inline</summary>
    internal bool IsInline => (Value & 1) != 0;

    internal bool IsHeap => (Value & 1) == 0 && Value != 0;

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
        return new Subtree(inline);
    }

    /// <summary>Returns a copy with the is_inline extra bit set or cleared (ts_subtree_set_extra on an inline subtree).</summary>
    internal Subtree WithInlineExtra(bool extra)
    {
        ulong inline = extra ? Value | ExtraBit : Value & ~ExtraBit;
        return new Subtree(inline);
    }

    /// <summary>
    /// Returns a copy with another symbol, named and visible bit (ts_subtree_set_symbol on an
    /// inline subtree). The symbol is truncated to 8 bits, as the C code does (uint8_t symbol).
    /// </summary>
    internal Subtree WithInlineSymbol(ushort symbol, bool named, bool visible)
    {
        ulong inline = Value & ~(((ulong)0xFF << SymbolShift) | NamedBit | VisibleBit);
        inline |= (ulong)(byte)symbol << SymbolShift;
        inline |= named ? NamedBit : 0;
        inline |= visible ? VisibleBit : 0;
        return new Subtree(inline);
    }

    /// <summary>Returns a copy with the is_missing bit set or cleared (ts_subtree_new_missing_leaf).</summary>
    internal Subtree WithInlineMissing(bool missing)
    {
        ulong inline = missing ? Value | IsMissingBit : Value & ~IsMissingBit;
        return new Subtree(inline);
    }

    /// <summary>Returns a copy with the has_changes bit set or cleared (ts_subtree_set_has_changes).</summary>
    internal Subtree WithInlineHasChanges(bool hasChanges)
    {
        ulong inline = hasChanges ? Value | HasChangesBit : Value & ~HasChangesBit;
        return new Subtree(inline);
    }

    // ts_subtree_symbol, ts_subtree_visible, ... (SUBTREE_GET)
    internal ushort Symbol => IsInline ? (ushort)((Value >> SymbolShift) & 0xFF) : SubtreeArena.Get(this).Symbol;
    internal bool Visible => IsInline ? (Value & VisibleBit) != 0 : SubtreeArena.Get(this).HasFlag(SubtreeFlags.Visible);
    internal bool Named => IsInline ? (Value & NamedBit) != 0 : SubtreeArena.Get(this).HasFlag(SubtreeFlags.Named);
    internal bool Extra => IsInline ? (Value & ExtraBit) != 0 : SubtreeArena.Get(this).HasFlag(SubtreeFlags.Extra);
    internal bool HasChanges => IsInline ? (Value & HasChangesBit) != 0 : SubtreeArena.Get(this).HasFlag(SubtreeFlags.HasChanges);
    internal bool IsMissing => IsInline ? (Value & IsMissingBit) != 0 : SubtreeArena.Get(this).HasFlag(SubtreeFlags.IsMissing);
    internal bool IsKeyword => IsInline ? (Value & IsKeywordBit) != 0 : SubtreeArena.Get(this).HasFlag(SubtreeFlags.IsKeyword);
    internal ushort ParseState => IsInline ? (ushort)((Value >> ParseStateShift) & 0xFFFF) : SubtreeArena.Get(this).ParseState;
    internal uint LookaheadBytes =>
        IsInline ? (uint)((Value >> LookaheadBytesShift) & 0x0F) : SubtreeArena.Get(this).LookaheadBytes;

    /// <summary>ts_subtree_leaf_symbol</summary>
    internal ushort LeafSymbol
    {
        get
        {
            if (IsInline)
            {
                return (ushort)((Value >> SymbolShift) & 0xFF);
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
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
                return (ushort)((Value >> ParseStateShift) & 0xFFFF);
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
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
                uint bytes = (uint)((Value >> PaddingBytesShift) & 0xFF);
                uint row = (uint)((Value >> PaddingRowsShift) & 0x0F);
                uint column = (uint)((Value >> PaddingColumnsShift) & 0xFF);
                return new Length(bytes, new Point(row, column));
            }

            return SubtreeArena.Get(this).Padding;
        }
    }

    /// <summary>ts_subtree_size</summary>
    internal Length Size
    {
        get
        {
            if (IsInline)
            {
                uint bytes = (uint)((Value >> SizeBytesShift) & 0xFF);
                return new Length(bytes, new Point(0, bytes));
            }

            return SubtreeArena.Get(this).Size;
        }
    }

    /// <summary>ts_subtree_total_size</summary>
    internal Length TotalSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (IsInline)
            {
                return Length.Add(Padding, Size);
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
            return Length.Add(heap.Padding, heap.Size);
        }
    }

    /// <summary>ts_subtree_total_bytes</summary>
    internal uint TotalBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TotalSize.Bytes;
    }

    /// <summary>ts_subtree_child_count</summary>
    internal uint ChildCount => IsInline ? 0 : SubtreeArena.Get(this).ChildCount;

    /// <summary>
    /// SubtreeHeapData.named_child_count; 0 for inline subtrees and for leaves (in the union
    /// the field is only valid for a node with children, as in C).
    /// </summary>
    internal uint NamedChildCount
    {
        get
        {
            if (IsInline)
            {
                return 0;
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
            return heap.ChildCount > 0 ? heap.NamedChildCount : 0;
        }
    }

    /// <summary>ts_subtree_children: ChildCount entries; empty for inline subtrees.</summary>
    internal ReadOnlySpan<Subtree> Children
    {
        get
        {
            if (IsInline)
            {
                return ReadOnlySpan<Subtree>.Empty;
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
            if (heap.ChildCount == 0 || heap.ChildrenHandle < 0)
            {
                return ReadOnlySpan<Subtree>.Empty;
            }

            return SubtreeArena.GetChildren(heap.ChildrenHandle).AsSpan(0, (int)heap.ChildCount);
        }
    }

    /// <summary>Children array for mutation (heap nodes with children only).</summary>
    internal Subtree[] ChildrenArray
    {
        get
        {
            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
            return SubtreeArena.GetChildren(heap.ChildrenHandle);
        }
    }

    /// <summary>ts_subtree_repeat_depth</summary>
    internal uint RepeatDepth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (IsInline)
            {
                return 0u;
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
            return heap.ChildCount == 0 && heap.HasFlag(SubtreeFlags.HasExternalTokens)
                ? heap.ExternalScannerState.UnionRepeatDepth
                : heap.RepeatDepth;
        }
    }

    /// <summary>ts_subtree_is_repetition</summary>
    internal bool IsRepetition
    {
        get
        {
            if (IsInline)
            {
                return false;
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
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

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
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

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
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

            return IsInline ? 0 : SubtreeArena.Get(this).ErrorCost;
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

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
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

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
            return heap.ChildCount > 0 ? heap.ProductionId : (ushort)0;
        }
    }

    /// <summary>ts_subtree_fragile_left</summary>
    internal bool FragileLeft => !IsInline && SubtreeArena.Get(this).HasFlag(SubtreeFlags.FragileLeft);

    /// <summary>ts_subtree_fragile_right</summary>
    internal bool FragileRight => !IsInline && SubtreeArena.Get(this).HasFlag(SubtreeFlags.FragileRight);

    /// <summary>ts_subtree_has_external_tokens</summary>
    internal bool HasExternalTokens => !IsInline && SubtreeArena.Get(this).HasFlag(SubtreeFlags.HasExternalTokens);

    /// <summary>ts_subtree_has_external_scanner_state_change</summary>
    internal bool HasExternalScannerStateChange =>
        !IsInline && SubtreeArena.Get(this).HasFlag(SubtreeFlags.HasExternalScannerStateChange);

    /// <summary>ts_subtree_depends_on_column</summary>
    internal bool DependsOnColumn =>
        IsInline ? (Value & DependsOnColumnBit) != 0 : SubtreeArena.Get(this).HasFlag(SubtreeFlags.DependsOnColumn);

    /// <summary>ts_subtree_is_fragile</summary>
    internal bool IsFragile
    {
        get
        {
            if (IsInline)
            {
                return false;
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(this);
            return heap.HasFlag(SubtreeFlags.FragileLeft) || heap.HasFlag(SubtreeFlags.FragileRight);
        }
    }

    /// <summary>ts_subtree_is_error</summary>
    internal bool IsError => Symbol == BuiltinSymbols.Error;

    /// <summary>ts_subtree_is_eof</summary>
    internal bool IsEof => Symbol == BuiltinSymbols.End;

    /// <summary>Same subtree: same value (handle+generation or inline bits).</summary>
    public bool Equals(Subtree other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is Subtree other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();
}
