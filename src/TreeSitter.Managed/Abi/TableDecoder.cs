using System.ComponentModel;

namespace TreeSitter.Abi;

/// <summary>
/// Part of the grammar ABI, used by generated code: turns the packed primitive tables of a
/// generated grammar into the struct arrays of LanguageTables. The packing is fixed and does
/// not depend on the byte order of the machine.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TableDecoder
{
    /// <summary>Four words per entry, see ParseActionEntry.FromRaw.</summary>
    public static ParseActionEntry[] ParseActions(ReadOnlySpan<ushort> words)
    {
        var result = new ParseActionEntry[words.Length / 4];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = ParseActionEntry.FromRaw(words[4 * i], words[(4 * i) + 1], words[(4 * i) + 2], words[(4 * i) + 3]);
        }
        return result;
    }

    /// <summary>One byte per entry: bit 0 visible, bit 1 named, bit 2 supertype.</summary>
    public static SymbolMetadata[] SymbolMetadata(ReadOnlySpan<byte> bits)
    {
        var result = new SymbolMetadata[bits.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new SymbolMetadata((bits[i] & 1) != 0, (bits[i] & 2) != 0, (bits[i] & 4) != 0);
        }
        return result;
    }

    /// <summary>Two words per entry: index, length.</summary>
    public static MapSlice[] MapSlices(ReadOnlySpan<ushort> words)
    {
        var result = new MapSlice[words.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new MapSlice(words[2 * i], words[(2 * i) + 1]);
        }
        return result;
    }

    /// <summary>Two words per entry: field id, then child index in the low byte and inherited in bit 8.</summary>
    public static FieldMapEntry[] FieldMapEntries(ReadOnlySpan<ushort> words)
    {
        var result = new FieldMapEntry[words.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            ushort packed = words[(2 * i) + 1];
            result[i] = new FieldMapEntry(words[2 * i], (byte)packed, (packed & 0x100) != 0);
        }
        return result;
    }

    /// <summary>Three words per entry: lex state, external lex state, reserved word set id.</summary>
    public static LexerMode[] LexerModes(ReadOnlySpan<ushort> words)
    {
        var result = new LexerMode[words.Length / 3];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new LexerMode(words[3 * i], words[(3 * i) + 1], words[(3 * i) + 2]);
        }
        return result;
    }

    /// <summary>One byte per entry, 0 or 1.</summary>
    public static bool[] Booleans(ReadOnlySpan<byte> bytes)
    {
        var result = new bool[bytes.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = bytes[i] != 0;
        }
        return result;
    }
}
