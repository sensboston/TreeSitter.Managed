// Differential tests: the C# decoders in TreeSitter.Unicode against the native
// ts_decode_utf8 / ts_decode_utf16_le / ts_decode_utf16_be of tree-sitter v0.27.2 (3fb7be4),
// reached through oracle_decode in tests/Oracle/shim/oracle.c.
//
// Every case compares the code point and the number of bytes consumed. A mismatch fails
// the test and prints the input bytes as hex.
#pragma warning disable CA5394 // The seeded Random only makes deterministic test data.

using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Runtime;

public class UnicodeOracleTests
{
    private const int Utf8 = 0;
    private const int Utf16Le = 1;
    private const int Utf16Be = 2;
    private const int RandomSeed = 20261122;
    private const int RandomBufferCount = 1_000_000;
    private const int RandomMaxLength = 8;

    private static readonly int[] Encodings = { Utf8, Utf16Le, Utf16Be };

    private static (int CodePoint, uint Consumed) Mine(int encoding, byte[] input)
    {
        ReadOnlySpan<byte> span = input;
        int codePoint;
        uint consumed = encoding switch
        {
            Utf8 => Unicode.DecodeUtf8(span, out codePoint),
            Utf16Le => Unicode.DecodeUtf16Le(span, out codePoint),
            Utf16Be => Unicode.DecodeUtf16Be(span, out codePoint),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
        return (codePoint, consumed);
    }

    private static (int CodePoint, uint Consumed) Native(int encoding, byte[] input)
    {
        uint consumed = NativeOracle.OracleDecode(encoding, input, (uint)input.Length, out int codePoint);
        return (codePoint, consumed);
    }

    private static string Hex(byte[] input) => Convert.ToHexString(input);

    // Compares one input. The first mismatch fails the test with the input in hex.
    private static void Compare(int encoding, byte[] input)
    {
        (int codePoint, uint consumed) mine = Mine(encoding, input);
        (int codePoint, uint consumed) native = Native(encoding, input);
        if (mine.codePoint != native.codePoint || mine.consumed != native.consumed)
        {
            Assert.Fail(
                "Decoder mismatch. Encoding " + encoding.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ", input " + Hex(input) +
                ": C# gives code point " + mine.codePoint.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " consumed " + mine.consumed.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ", native gives code point " + native.codePoint.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " consumed " + native.consumed.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }
    }

    // Builds a UTF-16 buffer from code units in the byte order of the given encoding.
    private static byte[] Units(int encoding, params ushort[] units)
    {
        byte[] bytes = new byte[units.Length * 2];
        for (int k = 0; k < units.Length; k++)
        {
            byte high = (byte)(units[k] >> 8);
            byte low = (byte)(units[k] & 0xFF);
            bool bigEndian = encoding == Utf16Be;
            bytes[k * 2] = bigEndian ? high : low;
            bytes[k * 2 + 1] = bigEndian ? low : high;
        }
        return bytes;
    }

    [OracleFact]
    public void AllOneByteValuesMatchTheNativeDecoders()
    {
        NativeOracle.EnsureAvailable();
        for (int value = 0; value <= 0xFF; value++)
        {
            foreach (int encoding in Encodings)
            {
                Compare(encoding, new[] { (byte)value });
            }
        }
    }

    [OracleFact]
    public void AllTwoByteSequencesMatchTheNativeDecoders()
    {
        // 65536 pairs for each encoding.
        NativeOracle.EnsureAvailable();
        for (int first = 0; first <= 0xFF; first++)
        {
            for (int second = 0; second <= 0xFF; second++)
            {
                byte[] pair = { (byte)first, (byte)second };
                foreach (int encoding in Encodings)
                {
                    Compare(encoding, pair);
                }
            }
        }
    }

    [OracleFact]
    public void AllUtf8ThreeByteSequencesWithLeadE0ToEFMatchTheNativeDecoder()
    {
        // Lead E0..EF, every first and second trail byte: 16 * 256 * 256 = 1048576 cases.
        NativeOracle.EnsureAvailable();
        for (int lead = 0xE0; lead <= 0xEF; lead++)
        {
            for (int first = 0; first <= 0xFF; first++)
            {
                for (int second = 0; second <= 0xFF; second++)
                {
                    Compare(Utf8, new[] { (byte)lead, (byte)first, (byte)second });
                }
            }
        }
    }

    [OracleFact]
    public void AllUtf8FourByteSequencesWithLeadF0ToF4AndTrail80ToBfMatchTheNativeDecoder()
    {
        // Lead F0..F4 and first trail 80..BF are checked fully. The other two trail bytes
        // are sampled with a fixed seed (256 samples per pair).
        NativeOracle.EnsureAvailable();
        var random = new Random(RandomSeed);
        for (int lead = 0xF0; lead <= 0xF4; lead++)
        {
            for (int first = 0x80; first <= 0xBF; first++)
            {
                for (int sample = 0; sample < 256; sample++)
                {
                    byte[] input =
                    {
                        (byte)lead,
                        (byte)first,
                        (byte)random.Next(256),
                        (byte)random.Next(256),
                    };
                    Compare(Utf8, input);
                }
            }
        }
    }

    [OracleFact]
    public void Utf8FourByteLeadsAboveF4AndCommonTailsMatchTheNativeDecoder()
    {
        // Leads F5..FF and the valid trail byte 80 / BF in the 2nd to 4th positions.
        NativeOracle.EnsureAvailable();
        for (int lead = 0xF5; lead <= 0xFF; lead++)
        {
            for (int first = 0; first <= 0xFF; first++)
            {
                Compare(Utf8, new[] { (byte)lead, (byte)first, (byte)0x80, (byte)0x80 });
                Compare(Utf8, new[] { (byte)lead, (byte)first, (byte)0xBF, (byte)0xBF });
            }
        }
    }

    [OracleFact]
    public void Utf8EdgeCasesMatchTheNativeDecoder()
    {
        NativeOracle.EnsureAvailable();
        byte[][] edges =
        {
            // overlong
            new byte[] { 0xC0, 0x80 },
            new byte[] { 0xC1, 0xBF },
            new byte[] { 0xE0, 0x80, 0x80 },
            new byte[] { 0xE0, 0x9F, 0xBF },
            new byte[] { 0xF0, 0x80, 0x80, 0x80 },
            new byte[] { 0xF0, 0x8F, 0xBF, 0xBF },
            // surrogates
            new byte[] { 0xED, 0xA0, 0x80 },
            new byte[] { 0xED, 0xBF, 0xBF },
            new byte[] { 0xED, 0x9F, 0xBF },
            // above U+10FFFF
            new byte[] { 0xF4, 0x90, 0x80, 0x80 },
            new byte[] { 0xF4, 0x8F, 0xBF, 0xBF },
            new byte[] { 0xF5, 0x80, 0x80, 0x80 },
            new byte[] { 0xFF, 0xBF, 0xBF, 0xBF },
            // stray trail bytes
            new byte[] { 0x80 },
            new byte[] { 0xBF, 0x41 },
            // valid boundary values
            new byte[] { 0x7F },
            new byte[] { 0xC2, 0x80 },
            new byte[] { 0xDF, 0xBF },
            new byte[] { 0xE0, 0xA0, 0x80 },
            new byte[] { 0xEF, 0xBF, 0xBF },
            new byte[] { 0xF0, 0x90, 0x80, 0x80 },
            new byte[] { 0xF4, 0x8F, 0xBF, 0xBF },
            // empty input: the shim returns the error before calling a decoder (len == 0),
            // so this case checks the shim guard and the C# empty-input rule.
            Array.Empty<byte>(),
        };
        foreach (byte[] edge in edges)
        {
            foreach (int encoding in Encodings)
            {
                Compare(encoding, edge);
            }
        }
    }

    [OracleFact]
    public void Utf8TruncationAtEveryPositionMatchesTheNativeDecoder()
    {
        // Each prefix of a 2, 3 and 4 byte sequence, lengths 1 to 3.
        NativeOracle.EnsureAvailable();
        byte[][] sequences =
        {
            new byte[] { 0xC3, 0xA9 },
            new byte[] { 0xE2, 0x82, 0xAC },
            new byte[] { 0xF0, 0x9F, 0x98, 0x80 },
            new byte[] { 0xE0, 0xA0, 0x80 },
            new byte[] { 0xED, 0xA0, 0x80 },
            new byte[] { 0xF4, 0x90, 0x80, 0x80 },
        };
        foreach (byte[] sequence in sequences)
        {
            for (int length = 1; length <= Math.Min(3, sequence.Length - 1); length++)
            {
                byte[] prefix = sequence.AsSpan(0, length).ToArray();
                Compare(Utf8, prefix);
            }
        }
    }

    [OracleFact]
    public void Utf16LoneSurrogatesAndSurrogatePairsMatchTheNativeDecoders()
    {
        // Lone surrogates at the start, middle and end, and pairs in both byte orders.
        NativeOracle.EnsureAvailable();
        ushort[][] unitLists =
        {
            new ushort[] { 0xD800 },
            new ushort[] { 0xDC00 },
            new ushort[] { 0xD800, 0x0041 },
            new ushort[] { 0xDC00, 0x0041 },
            new ushort[] { 0x0041, 0xD800, 0x0041 },
            new ushort[] { 0x0041, 0xDC00, 0x0041 },
            new ushort[] { 0x0041, 0xD800 },
            new ushort[] { 0x0041, 0xDC00 },
            new ushort[] { 0xD800, 0xD800 },
            new ushort[] { 0xD800, 0xDC00 },
            new ushort[] { 0xD83D, 0xDE00, 0xD83D },
            new ushort[] { 0xDBFF, 0xDFFF },
            new ushort[] { 0xDBFF, 0xDFFF, 0xDC00 },
            new ushort[] { 0xFFFF },
            new ushort[] { 0xFFFE, 0x0000 },
        };
        foreach (ushort[] units in unitLists)
        {
            Compare(Utf16Le, Units(Utf16Le, units));
            Compare(Utf16Be, Units(Utf16Be, units));
        }
    }

    [OracleFact]
    public void Utf16OddLengthsMatchTheNativeDecoders()
    {
        NativeOracle.EnsureAvailable();
        byte[] baseLe = Units(Utf16Le, 0xD83D, 0xDE00, 0x0041);
        byte[] baseBe = Units(Utf16Be, 0xD83D, 0xDE00, 0x0041);
        for (int length = 0; length <= baseLe.Length; length++)
        {
            Compare(Utf16Le, baseLe.AsSpan(0, length).ToArray());
            Compare(Utf16Be, baseBe.AsSpan(0, length).ToArray());
        }
        Compare(Utf16Le, new byte[] { 0x41, 0x00, 0x3D });
        Compare(Utf16Be, new byte[] { 0x00, 0x41, 0xD8 });
        Compare(Utf16Le, new byte[] { 0x00 });
        Compare(Utf16Be, new byte[] { 0xD8 });
    }

    [OracleFact]
    public void RandomBuffersOfLengthZeroToEightMatchTheNativeDecoders()
    {
        // 1,000,000 buffers, lengths 0 to 8, seeded with 20261122. Each buffer is decoded
        // with all three encodings. On a mismatch the input bytes are printed in hex.
        NativeOracle.EnsureAvailable();
        var random = new Random(RandomSeed);
        for (int index = 0; index < RandomBufferCount; index++)
        {
            int length = random.Next(0, RandomMaxLength + 1);
            byte[] input = new byte[length];
            random.NextBytes(input);
            foreach (int encoding in Encodings)
            {
                Compare(encoding, input);
            }
        }
    }
}
