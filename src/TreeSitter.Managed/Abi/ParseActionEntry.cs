namespace TreeSitter.Abi;

using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>
/// Part of the grammar ABI, used by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Explicit, Size = 8)]
public readonly struct ParseActionEntry : IEquatable<ParseActionEntry>
{
    [FieldOffset(0)]
    private readonly byte _b0;

    [FieldOffset(1)]
    private readonly byte _b1;

    [FieldOffset(2)]
    private readonly ushort _b23;

    [FieldOffset(4)]
    private readonly byte _b4;

    [FieldOffset(5)]
    private readonly byte _b5;

    [FieldOffset(6)]
    private readonly ushort _b67;

    private ParseActionEntry(byte b0, byte b1, ushort b23, byte b4, byte b5, ushort b67)
    {
        _b0 = b0;
        _b1 = b1;
        _b23 = b23;
        _b4 = b4;
        _b5 = b5;
        _b67 = b67;
    }

    /// <summary>
    /// Part of the grammar ABI, used by generated code: builds an entry from its 8 bytes as
    /// four little-endian 16-bit words (bytes 0-1, 2-3, 4-5, 6-7 of the C union).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ParseActionEntry FromRaw(ushort word0, ushort word1, ushort word2, ushort word3) =>
        new((byte)word0, (byte)(word0 >> 8), word1, (byte)word2, (byte)(word2 >> 8), word3);

    /// <summary>The four 16-bit words that FromRaw takes.</summary>
    internal (ushort Word0, ushort Word1, ushort Word2, ushort Word3) ToRaw() =>
        ((ushort)(_b0 | (_b1 << 8)), _b23, (ushort)(_b4 | (_b5 << 8)), _b67);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ParseActionType Type => (ParseActionType)_b0;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public byte EntryCount => _b0;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool EntryReusable => _b1 != 0;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public byte ReduceChildCount => _b1;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort ShiftState => _b23;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort ReduceSymbol => _b23;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShiftExtra => _b4 != 0;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShiftRepetition => _b5 != 0;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public short ReduceDynamicPrecedence => (short)((ushort)(_b4 | (ushort)(_b5 << 8)));

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort ReduceProductionId => _b67;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ParseActionEntry Entry(byte count, bool reusable) =>
        new(count, reusable ? (byte)1 : (byte)0, 0, 0, 0, 0);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ParseActionEntry Shift(ushort state, bool extra, bool repetition) =>
        new((byte)ParseActionType.Shift, 0, state, extra ? (byte)1 : (byte)0, repetition ? (byte)1 : (byte)0, 0);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ParseActionEntry Reduce(byte childCount, ushort symbol, short dynamicPrecedence, ushort productionId) =>
        new((byte)ParseActionType.Reduce, childCount, symbol, (byte)(dynamicPrecedence & 0xFF), (byte)((dynamicPrecedence >> 8) & 0xFF), productionId);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ParseActionEntry Recover() => new((byte)ParseActionType.Recover, 0, 0, 0, 0, 0);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ParseActionEntry Accept() => new((byte)ParseActionType.Accept, 0, 0, 0, 0, 0);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool Equals(ParseActionEntry other) =>
        _b0 == other._b0 && _b1 == other._b1 && _b23 == other._b23 &&
        _b4 == other._b4 && _b5 == other._b5 && _b67 == other._b67;

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override bool Equals(object? obj) => obj is ParseActionEntry other && Equals(other);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override int GetHashCode()
    {
        int hash = 17;
        hash = hash * 31 + _b0.GetHashCode();
        hash = hash * 31 + _b1.GetHashCode();
        hash = hash * 31 + _b23.GetHashCode();
        hash = hash * 31 + _b4.GetHashCode();
        hash = hash * 31 + _b5.GetHashCode();
        hash = hash * 31 + _b67.GetHashCode();
        return hash;
    }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool operator ==(ParseActionEntry left, ParseActionEntry right) => left.Equals(right);

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool operator !=(ParseActionEntry left, ParseActionEntry right) => !left.Equals(right);
}
