// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/unicode.h, lib/src/unicode/utf8.h and
// lib/src/unicode/utf16.h (ICU, see THIRD-PARTY-NOTICES.md).
//
// The decoders follow the ICU macros step by step. The number of bytes consumed on an
// error is not always 1: the macro leaves the index where it stopped, and the lexer relies
// on that. Do not replace this with System.Text.Rune or System.Text.Encoding: their error
// handling is different. Tests compare the results with the native decoders
// (tests/TreeSitter.Managed.Tests/Runtime/UnicodeOracleTests.cs).

namespace TreeSitter;

/// <summary>
/// UTF-8 and UTF-16 decoders with the same results as the tree-sitter runtime.
/// </summary>
internal static class Unicode
{
    // TS_DECODE_ERROR (unicode.h), equal to U_SENTINEL (umachine.h).
    internal const int DecodeError = -1;

    // U8_LEAD3_T1_BITS (utf8.h). Index is (lead byte & 0xF). Bit (first trail byte >> 5)
    // says whether that lead and first trail byte start a valid 3-byte sequence.
    private static ReadOnlySpan<byte> Lead3T1Bits =>
        [0x20, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x10, 0x30, 0x30];

    // U8_LEAD4_T1_BITS (utf8.h). Index is (first trail byte >> 4). Bit (lead byte - 0xF0)
    // says whether that lead and first trail byte start a valid 4-byte sequence.
    private static ReadOnlySpan<byte> Lead4T1Bits =>
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x0F, 0x0F, 0x0F, 0x00, 0x00, 0x00, 0x00];

    // U16_SURROGATE_OFFSET (utf16.h).
    private const int SurrogateOffset = (0xD800 << 10) + 0xDC00 - 0x10000;

    // ts_decode_utf8 -> U8_NEXT (utf8.h, U8_INTERNAL_NEXT_OR_SUB with sub = U_SENTINEL).
    // Returns the number of bytes consumed and stores the code point, or DecodeError.
    internal static uint DecodeUtf8(ReadOnlySpan<byte> input, out int codePoint)
    {
        // C always has at least one byte here: lexer.c returns before decoding an empty
        // chunk. Empty input is an error with nothing consumed.
        if (input.Length == 0)
        {
            codePoint = DecodeError;
            return 0;
        }

        uint length = (uint)input.Length;
        uint i = 1;

        // (c)=(uint8_t)(s)[(i)++]; a single-byte code point is done here.
        int c = input[0];
        if ((c & 0x80) == 0)
        {
            codePoint = c;
            return i;
        }

        // The rest of the macro is one && chain. `ok` is the value of the chain so far, and
        // `t` is the last trail byte read (C: __t, a uint8_t, so it stays in 0..255).
        bool ok = false;
        int t = 0;
        if (i != length)
        {
            if (c >= 0xE0)
            {
                // Fetch and validate all but the last trail byte.
                if (c < 0xF0)
                {
                    // U+0800..U+FFFF except surrogates
                    c &= 0xF;
                    t = input[(int)i];
                    ok = (Lead3T1Bits[c] & (1 << (t >> 5))) != 0;
                    if (ok)
                    {
                        t &= 0x3F;
                    }
                }
                else
                {
                    // U+10000..U+10FFFF
                    c -= 0xF0;
                    t = input[(int)i];
                    ok = c <= 4 && (Lead4T1Bits[t >> 4] & (1 << c)) != 0;
                    if (ok)
                    {
                        c = (c << 6) | (t & 0x3F);
                        i++;
                        ok = i != length;
                        if (ok)
                        {
                            t = (input[(int)i] - 0x80) & 0xFF;
                            ok = t <= 0x3F;
                        }
                    }
                }

                // valid second-to-last trail byte
                if (ok)
                {
                    c = (c << 6) | t;
                    i++;
                    ok = i != length;
                }
            }
            else if (c >= 0xC2)
            {
                // U+0080..U+07FF
                c &= 0x1F;
                ok = true;
            }
        }

        // last trail byte
        if (ok)
        {
            t = (input[(int)i] - 0x80) & 0xFF;
            ok = t <= 0x3F;
            if (ok)
            {
                c = (c << 6) | t;
                i++;
            }
        }

        if (ok)
        {
            codePoint = c;
            return i;
        }
        codePoint = DecodeError;
        return i;
    }

    // ts_decode_utf16_le -> U16_NEXT_LE (unicode.h). The input length is in bytes.
    internal static uint DecodeUtf16Le(ReadOnlySpan<byte> input, out int codePoint)
    {
        if (input.Length < 2)
        {
            codePoint = DecodeError;
            return (uint)input.Length;
        }

        // U16_NEXT_LE works in code units: the length is input.Length / 2, as in unicode.h.
        uint length = (uint)input.Length / 2;
        uint i = 1;

        // (c)=le16toh((s)[(i)++]);
        int c = ReadUnitLe(input, 0);
        // U16_IS_LEAD, then U16_IS_TRAIL on the next unit. A lone surrogate is returned as is.
        if ((c & 0xFC00) == 0xD800 && i != length)
        {
            int c2 = ReadUnitLe(input, i);
            if ((c2 & 0xFC00) == 0xDC00)
            {
                i++;
                c = (c << 10) + c2 - SurrogateOffset;
            }
        }

        codePoint = c;
        return i * 2;
    }

    // ts_decode_utf16_be -> U16_NEXT_BE (unicode.h). The input length is in bytes.
    internal static uint DecodeUtf16Be(ReadOnlySpan<byte> input, out int codePoint)
    {
        if (input.Length < 2)
        {
            codePoint = DecodeError;
            return (uint)input.Length;
        }

        // U16_NEXT_BE works in code units: the length is input.Length / 2, as in unicode.h.
        uint length = (uint)input.Length / 2;
        uint i = 1;

        // (c)=be16toh((s)[(i)++]);
        int c = ReadUnitBe(input, 0);
        // U16_IS_LEAD, then U16_IS_TRAIL on the next unit. A lone surrogate is returned as is.
        if ((c & 0xFC00) == 0xD800 && i != length)
        {
            int c2 = ReadUnitBe(input, i);
            if ((c2 & 0xFC00) == 0xDC00)
            {
                i++;
                c = (c << 10) + c2 - SurrogateOffset;
            }
        }

        codePoint = c;
        return i * 2;
    }

    // Code unit number `unit` of a little-endian UTF-16 buffer. The value is 0..0xFFFF, so
    // the masks in the callers match the 32-bit masks of the C macros.
    private static int ReadUnitLe(ReadOnlySpan<byte> input, uint unit)
    {
        int offset = (int)unit * 2;
        return input[offset] | (input[offset + 1] << 8);
    }

    // Code unit number `unit` of a big-endian UTF-16 buffer.
    private static int ReadUnitBe(ReadOnlySpan<byte> input, uint unit)
    {
        int offset = (int)unit * 2;
        return (input[offset] << 8) | input[offset + 1];
    }
}
