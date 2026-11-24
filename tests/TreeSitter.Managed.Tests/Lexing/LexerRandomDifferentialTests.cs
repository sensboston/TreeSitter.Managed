using System;
using System.Globalization;
using System.Linq;
using System.Text;
using TreeSitter.Abi;
using TreeSitter.Generator.Model;
using TreeSitter.Tests.Generator;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Lexing;

public sealed class LexerRandomDifferentialTests
{
    private const int ChecksPerGrammar = 100000;
    private const int BatchSize = 500;

    [OracleTheory]
    [InlineData("json", "Json", 7341)]
    [InlineData("c", "C", 9127)]
    [InlineData("python", "Python", 14021)]
    public void RandomInputsMatchNative(string grammar, string directory, int seed)
    {
        // States, keyword lexer and character sets come from the model of parser.c, so every
        // check runs a real case of the generated switch (not the default branch).
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC(directory));
        int stateCount = model.Lex!.States.Count;
        bool hasKeywords = model.KeywordLex is not null;
        CharacterRange[] ranges = model.CharacterSets.Values.SelectMany(r => r).ToArray();
        Random random = new Random(seed);
        for (int batch = 0; batch < ChecksPerGrammar / BatchSize; batch++)
        {
            StringBuilder script = new StringBuilder(BatchSize * 80);
            for (int i = 0; i < BatchSize; i++)
            {
                string encoding = random.Next(3) switch { 0 => "utf8", 1 => "utf16le", _ => "utf16be" };
                int length = random.Next(13);
                byte[] bytes = RandomBytes(random, encoding, length, ranges);
                string hex = bytes.Length == 0 ? "-" : Convert.ToHexString(bytes);
                int chunk = random.Next(4);
                int state = random.Next(ushort.MaxValue + 1);
                script.Append("input ").Append(encoding).Append(' ').Append(chunk.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(hex)
                    .Append("\nreset 0:0:0\nlex ").Append(grammar).Append(" main ").Append(state.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            string text = script.ToString();
            string native = NativeOracle.OracleLexerScript(text)!;
            string managed = LexerScriptRunner.Run(text);
            Assert.True(string.Equals(native, managed, StringComparison.Ordinal),
                $"{grammar}, seed {seed}, batch {batch}: first difference: {FirstDifference(native, managed)}");
        }
    }

    private static byte[] RandomBytes(Random random, string encoding, int length, CharacterRange[] ranges)
    {
        System.Collections.Generic.List<byte> bytes = new(length);
        if (random.Next(10) == 0)
        {
            byte[] bom = encoding switch
            {
                "utf8" => [0xef, 0xbb, 0xbf],
                "utf16le" => [0xff, 0xfe],
                _ => [0xfe, 0xff],
            };
            for (int i = 0; i < bom.Length && bytes.Count < length; i++) bytes.Add(bom[i]);
        }
        while (bytes.Count < length)
        {
            int choice = random.Next(100);
            int remaining = length - bytes.Count;
            if (choice < 60)
            {
                bytes.Add((byte)random.Next(0x20, 0x7f));
            }
            else if (choice < 82)
            {
                int codePoint = random.Next(3) switch
                {
                    0 => random.Next(0x00a0, 0xd800),
                    1 => random.Next(0xe000, 0x10000),
                    _ => RangeCodePoint(random, ranges),
                };
                char value = (char)codePoint;
                byte[] encoded = encoding switch
                {
                    "utf8" => Encoding.UTF8.GetBytes(new[] { value }),
                    "utf16le" => Encoding.Unicode.GetBytes(new[] { value }),
                    _ => Encoding.BigEndianUnicode.GetBytes(new[] { value }),
                };
                for (int i = 0; i < encoded.Length && i < remaining; i++) bytes.Add(encoded[i]);
            }
            else
            {
                bytes.Add((byte)random.Next(256));
            }
        }
        return bytes.ToArray();
    }

    // A code point at or next to a bound of a character set range (BMP, no surrogates).
    private static int RangeCodePoint(Random random, CharacterRange[] ranges)
    {
        if (ranges.Length == 0)
        {
            return random.Next(0x20, 0x7f);
        }
        CharacterRange range = ranges[random.Next(ranges.Length)];
        int value = random.Next(4) switch
        {
            0 => range.Start - 1,
            1 => range.Start,
            2 => range.End,
            _ => range.End + 1,
        };
        return value is < 0 or > 0xffff or (>= 0xd800 and < 0xe000) ? 'a' : value;
    }

    private static string FirstDifference(string expected, string actual)
    {
        string[] left = expected.Split('\n');
        string[] right = actual.Split('\n');
        int i = 0;
        while (i < left.Length && i < right.Length && string.Equals(left[i], right[i], StringComparison.Ordinal)) i++;
        return $"line {i + 1}: native '{(i < left.Length ? left[i] : "<end>")}', managed '{(i < right.Length ? right[i] : "<end>")}'";
    }
}
