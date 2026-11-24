using System.Globalization;
using System.Text;

namespace TreeSitter.Tests.Lexing;

/// <summary>
/// Builds the scripts of <see cref="LexerScriptGenerator"/>: text, chunk sizes and positions
/// that the parser of the lexer can be in, so that every branch of lexer.c is reached.
/// </summary>
internal sealed class LexerScriptGeneratorCore
{
    private const int MaxChunkSize = 5;
    private const int MaxBytes = 48;
    private const int MaxRanges = 4;

    private static readonly string[] Encodings = ["utf8", "utf16le", "utf16be"];

    private readonly Random _random;
    private readonly StringBuilder _output = new StringBuilder();
    private readonly int _seed;
    private readonly int _commandCount;
    private int _lineCount;

    private LexerScriptGeneratorCore(int seed, int commandCount)
    {
        _random = new Random(seed);
        _seed = seed;
        _commandCount = Math.Max(2, commandCount);
    }

    internal static string Generate(int seed, int commandCount)
    {
        LexerScriptGeneratorCore generator = new LexerScriptGeneratorCore(seed, commandCount);
        return generator.Build();
    }

    // ------------------------------------------------------------ output

    private void Line(string text)
    {
        _output.Append(text).Append('\n');
        _lineCount++;
    }

    // ------------------------------------------------------------ build

    private string Build()
    {
        Line("# Lexer script generated for seed " + _seed.ToString(CultureInfo.InvariantCulture));
        while (_lineCount < _commandCount)
        {
            int step = _random.Next(0, 100);
            if (step < 30)
            {
                StartScenario();
            }
            else if (step < 50)
            {
                AddAdvances();
            }
            else if (step < 58)
            {
                // ts_lexer__get_column leaves a chunk loaded even at the end of the input, and
                // ts_lexer__advance then reads included_ranges[count] in C, which is past the
                // array: the behavior is undefined there. A new input after the column query
                // puts the lexer back into a state that is defined in C.
                Line("column");
                StartScenario();
            }
            else if (step < 64)
            {
                Line("eof");
            }
            else if (step < 70)
            {
                Line("at_range_start");
            }
            else if (step < 78)
            {
                Line("mark_end");
            }
            else if (step < 84)
            {
                Line("reset " + Position());
            }
            else if (step < 90)
            {
                Line("finish " + Number(_random.Next(0, 12)));
            }
            else if (step < 95)
            {
                Line("ranges " + Ranges());
            }
            else
            {
                Line("dump");
            }
        }

        return _output.ToString();
    }

    /// <summary>A new input (sometimes with new ranges), then start, and a few advances.</summary>
    private void StartScenario()
    {
        if (_random.Next(0, 4) == 0)
        {
            Line("ranges " + Ranges());
        }

        string encoding = Encodings[_random.Next(0, Encodings.Length)];
        int chunkSize = _random.Next(0, MaxChunkSize + 1);
        byte[] bytes = RandomBytes(encoding);
        Line(string.Concat(
            "input ",
            encoding,
            " ",
            Number(chunkSize),
            " ",
            bytes.Length == 0 ? "-" : Convert.ToHexString(bytes).ToLowerInvariant()));
        Line("reset " + PositionFor(0, 0, 0));
        Line("start");
        AddAdvances();
    }

    private void AddAdvances()
    {
        int count = _random.Next(1, 9);
        for (int i = 0; i < count; i++)
        {
            bool skip = _random.Next(0, 2) == 0;
            Line(skip ? "advance 1" : "advance 0");
        }

        if (_random.Next(0, 3) == 0)
        {
            Line("dump");
        }
    }

    // ------------------------------------------------------------ values

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A position of the current input; the row and column are plausible but the
    /// byte is what the lexer uses.</summary>
    private string Position()
    {
        int bytes = _random.Next(0, MaxBytes + 2);
        int row = _random.Next(0, 4);
        int column = _random.Next(0, 6);
        return PositionFor(bytes, row, column);
    }

    private static string PositionFor(int bytes, int row = 0, int column = 0) =>
        Number(bytes) + ":" + Number(row) + ":" + Number(column);

    /// <summary>Included ranges: ordered, sometimes empty, sometimes overlapping.</summary>
    private string Ranges()
    {
        if (_random.Next(0, 5) == 0)
        {
            return "0";
        }

        int count = _random.Next(1, MaxRanges + 1);
        StringBuilder builder = new StringBuilder();
        builder.Append(Number(count));
        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            int start = previous + _random.Next(0, 3);
            int end = start + _random.Next(0, 5);
            builder.Append(' ').Append(PositionFor(start)).Append('-').Append(PositionFor(end));
            previous = end;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The bytes of a random input: ASCII, a BOM, line breaks, valid multi-byte characters and
    /// invalid sequences, in the byte order of the encoding.
    /// </summary>
    private byte[] RandomBytes(string encoding)
    {
        int length = _random.Next(0, MaxBytes + 1);
        List<byte> bytes = new List<byte>(length);
        if (encoding == "utf8")
        {
            if (_random.Next(0, 5) == 0)
            {
                bytes.AddRange([0xEF, 0xBB, 0xBF]);
            }

            while (bytes.Count < length)
            {
                int pick = _random.Next(0, 100);
                if (pick < 45)
                {
                    bytes.Add((byte)RandomAscii());
                }
                else if (pick < 55)
                {
                    bytes.Add(0x0A);
                }
                else if (pick < 70)
                {
                    bytes.AddRange(RandomUtf8Character());
                }
                else if (pick < 80)
                {
                    // An invalid byte or a truncated sequence.
                    bytes.Add((byte)_random.Next(0x80, 0x100));
                }
                else if (pick < 90)
                {
                    // A multi-byte character that the end of the chunk can cut.
                    bytes.AddRange(ValidUtf8Sequences[_random.Next(0, ValidUtf8Sequences.Length)]);
                }
                else
                {
                    bytes.AddRange([0xEF, 0xBB, 0xBF]);
                }
            }
        }
        else
        {
            bool littleEndian = encoding == "utf16le";
            if (_random.Next(0, 5) == 0)
            {
                bytes.AddRange(Unit(0xFEFF, littleEndian));
            }

            while (bytes.Count < length)
            {
                int pick = _random.Next(0, 100);
                if (pick < 45)
                {
                    bytes.AddRange(Unit((ushort)RandomAscii(), littleEndian));
                }
                else if (pick < 55)
                {
                    bytes.AddRange(Unit(0x000A, littleEndian));
                }
                else if (pick < 70)
                {
                    ushort[] pair = SurrogatePairs[_random.Next(0, SurrogatePairs.Length)];
                    bytes.AddRange(Unit(pair[0], littleEndian));
                    bytes.AddRange(Unit(pair[1], littleEndian));
                }
                else if (pick < 85)
                {
                    // A lone surrogate: U16_NEXT returns it as is.
                    ushort surrogate = (ushort)(_random.Next(0, 2) == 0
                        ? 0xD800 + _random.Next(0, 0x400)
                        : 0xDC00 + _random.Next(0, 0x400));
                    bytes.AddRange(Unit(surrogate, littleEndian));
                }
                else
                {
                    // An odd byte: the input can end between the halves of a code unit.
                    bytes.Add((byte)_random.Next(0, 0x100));
                }
            }
        }

        if (bytes.Count > length)
        {
            bytes.RemoveRange(length, bytes.Count - length);
        }

        return bytes.ToArray();
    }

    private int RandomAscii()
    {
        int pick = _random.Next(0, 100);
        if (pick < 60)
        {
            return 0x20 + _random.Next(0, 0x5F);
        }

        return _random.Next(0, 0x80);
    }

    private byte[] RandomUtf8Character() => ValidUtf8Sequences[_random.Next(0, ValidUtf8Sequences.Length)];

    private static byte[] Unit(ushort value, bool littleEndian) =>
        littleEndian
            ? [(byte)(value & 0xFF), (byte)(value >> 8)]
            : [(byte)(value >> 8), (byte)(value & 0xFF)];

    private static readonly byte[][] ValidUtf8Sequences =
    [
        [0xC3, 0xA9],
        [0xE2, 0x82, 0xAC],
        [0xF0, 0x9F, 0x98, 0x80],
        [0xED, 0x9F, 0xBF],
        [0xC2, 0x80],
        [0xDF, 0xBF],
        [0xEF, 0xBF, 0xBF],
        [0xF4, 0x8F, 0xBF, 0xBF],
    ];

    private static readonly ushort[][] SurrogatePairs =
    [
        [0xD83D, 0xDE00],
        [0xDBFF, 0xDFFF],
        [0xD800, 0xDC00],
    ];
}
