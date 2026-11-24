using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Lexing;

public sealed class LexerStateDifferentialTests
{
    private const int SamplesPerState = 50;
    private const int BatchSize = 500;

    [OracleTheory]
    [InlineData("json", "Json", 21031)]
    [InlineData("c", "C", 33103)]
    [InlineData("python", "Python", 44887)]
    public void EveryGeneratedLexStateMatchesAtSampledCorpusPositions(string grammar, string directory, int seed)
    {
        string root = LexerCorpusDifferentialTests.FindRepositoryRoot();
        string source = Path.Combine(root, "grammars", directory, "Generated", directory + "Language.g.cs");
        int stateCount = CountMainLexerStates(File.ReadAllText(source));
        string corpusPath = Path.Combine(root, "grammars", directory, "upstream", "test", "corpus");
        List<byte[]> corpus = new();
        List<(byte[] Input, int Offset, uint Row, uint Column)> leafPositions = new();
        foreach (string file in Directory.GetFiles(corpusPath, "*.txt").OrderBy(file => file, StringComparer.Ordinal))
        {
            foreach (CorpusEntry entry in CorpusReader.Read(file))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(entry.Input);
                if (bytes.Length > 65536) continue;
                corpus.Add(bytes);
                foreach (string leaf in NativeOracle.OracleLeaves(grammar, bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = leaf.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length != 4 || fields[0].Length < 2) continue;
                    string[] point = fields[0][1..^1].Split(':');
                    if (point.Length == 3 && int.TryParse(point[0], NumberStyles.None, CultureInfo.InvariantCulture, out int offset) &&
                        uint.TryParse(point[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint row) &&
                        uint.TryParse(point[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint column))
                        leafPositions.Add((bytes, offset, row, column));
                }
            }
        }
        Assert.NotEmpty(corpus);
        Assert.NotEmpty(leafPositions);
        Random random = new Random(seed);
        StringBuilder batch = new StringBuilder();
        int commandCount = 0;
        int checkedCount = 0;
        for (int state = 0; state < stateCount; state++)
        {
            for (int sample = 0; sample < SamplesPerState; sample++)
            {
                byte[] input;
                int offset;
                uint row;
                uint column;
                if (random.Next(2) == 0)
                {
                    (input, offset, row, column) = leafPositions[random.Next(leafPositions.Count)];
                }
                else
                {
                    input = corpus[random.Next(corpus.Count)];
                    offset = input.Length == 0 ? 0 : random.Next(input.Length + 1);
                    (row, column) = PointAt(input, offset);
                }
                batch.Append("input utf8 0 ").Append(input.Length == 0 ? "-" : Convert.ToHexString(input)).Append('\n')
                    .Append("reset ").Append(offset.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(row.ToString(CultureInfo.InvariantCulture)).Append(':').Append(column.ToString(CultureInfo.InvariantCulture))
                    .Append("\nlex ").Append(grammar).Append(" main ").Append(state.ToString(CultureInfo.InvariantCulture)).Append('\n');
                commandCount++;
                checkedCount++;
                if (commandCount == BatchSize)
                {
                    Compare(grammar, seed, checkedCount - commandCount, batch.ToString());
                    batch.Clear();
                    commandCount = 0;
                }
            }
        }
        if (commandCount != 0) Compare(grammar, seed, checkedCount - commandCount, batch.ToString());
        Assert.Equal(stateCount * SamplesPerState, checkedCount);
    }

    private static void Compare(string grammar, int seed, int first, string script)
    {
        string native = NativeOracle.OracleLexerScript(script)!;
        string managed = LexerScriptRunner.Run(script);
        Assert.True(string.Equals(native, managed, StringComparison.Ordinal),
            $"{grammar}, state-position sample {first}, seed {seed}: {FirstDifference(native, managed)}");
    }

    // The main lexer is Lex, or Lex and its chunk methods Lex0, Lex1, ... when it is split.
    private static int CountMainLexerStates(string source)
    {
        MatchCollection methods = Regex.Matches(source,
            @"private static (bool Lex\(Lexer lexer, ushort state\)|int Lex\d+\()", RegexOptions.CultureInvariant);
        Assert.NotEmpty(methods);
        int max = -1;
        foreach (Match method in methods)
        {
            int open = source.IndexOf('{', method.Index);
            Assert.True(open >= 0, "Generated main lexer body was not found.");
            int depth = 0;
            int close = open;
            for (; close < source.Length; close++)
            {
                if (source[close] == '{') depth++;
                else if (source[close] == '}' && --depth == 0) break;
            }
            Assert.True(close < source.Length, "Generated main lexer body is unbalanced.");
            foreach (Match c in Regex.Matches(source[open..close], @"\bcase\s+(\d+)\s*:", RegexOptions.CultureInvariant))
            {
                max = Math.Max(max, int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        Assert.True(max >= 0, "Generated main lexer has no states.");
        return max + 1;
    }

    private static (uint Row, uint Column) PointAt(byte[] bytes, int end)
    {
        uint row = 0;
        uint column = 0;
        for (int i = 0; i < end; i++)
        {
            if (bytes[i] == (byte)'\n') { row++; column = 0; }
            else column++;
        }
        return (row, column);
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
