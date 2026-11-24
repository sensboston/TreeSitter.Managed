using System.Text;
using TreeSitter.Tests.Lexing;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Parsing;

/// <summary>
/// Random mutations of the corpus inputs (deleted, repeated and inserted pieces, bytes of
/// the grammar's punctuation, broken UTF-8), parsed natively and by the port: the log,
/// the S-expression and the subtree dump must be equal. Most inputs have syntax errors,
/// so the error recovery of parser.c runs on every one of them.
/// </summary>
public class ParserFuzzTests
{
    internal static readonly string[] JsonPieces =
    [
        "{", "}", "[", "]", ":", ",", "\"", "\\", " ", "\n", "-", "+", ".", "e", "0", "12", "true", "false",
        "null", "/* x */", "// c\n", "\"k\": 1",
    ];

    internal static readonly string[] CPieces =
    [
        "{", "}", "[", "]", "(", ")", ";", ",", ".", ":", "?", "*", "&", "|", "^", "!", "~", "<", ">", "=",
        "+", "-", "/", "%", "#", "\"", "'", "\\", " ", "\n", "\t", "x", "_a", "0", "1.5", "/* c */",
        "// c\n", "#if X\n", "#endif\n", "#define Y 1\n", "int", "if", "else", "return", "struct", "while",
        "\"s\"", "'c'", "->", "...",
    ];

    [OracleTheory]
    [InlineData("json", "Json", 10000)]
    [InlineData("c", "C", 3000)]
    [InlineData("python", "Python", 3000)]
    [InlineData("go", "Go", 2000)]
    [InlineData("java", "Java", 2000)]
    [InlineData("css", "Css", 2000)]
    [InlineData("html", "Html", 2000)]
    [InlineData("javascript", "JavaScript", 2000)]
    [InlineData("rust", "Rust", 2000)]
    [InlineData("cpp", "Cpp", 2000)]
    [InlineData("c_sharp", "CSharp", 2000)]
    [InlineData("bash", "Bash", 2000)]
    [InlineData("toml", "Toml", 2000)]
    [InlineData("typescript", "TypeScript", 2000)]
    [InlineData("tsx", "Tsx", 2000)]
    [InlineData("yaml", "Yaml", 2000)]
    // markdown_inline accepts almost any input as text: only an empty input, a
    // lone NUL byte and a BOM at the start produce errors, which the mutation
    // pieces reach only rarely.
    [InlineData("markdown_inline", "MarkdownInline", 2000, 0)]
    // markdown (block structure) likewise accepts almost any input as text; in
    // CI only 24 of 2000 mutated inputs reach the error recovery.
    [InlineData("markdown", "Markdown", 2000, 0)]
    public void MutatedCorpusMatchesOracle(string grammar, string directory, int count, int minimumWithErrors = -1)
    {
        NativeOracle.EnsureAvailable();
        List<byte[]> inputs = ParserCorpusTests.ReadCorpus(directory)
            .Select(entry => Encoding.UTF8.GetBytes(entry.Input))
            .ToList();
        string[] pieces = grammar == "json" ? JsonPieces : CPieces;
        int withErrors = 0;
        for (int seed = 0; seed < count; seed++)
        {
            Random random = new Random(seed);
            byte[] input = Mutate(random, inputs[random.Next(inputs.Count)], pieces);
            uint flags = ParseRunner.LogFlag | (seed % 5) switch
            {
                1 => ParseRunner.ReparseFlag,
                2 => ParseRunner.CancelFlag,
                3 => ParseRunner.RangesFlag,
                4 => ParseRunner.ReparseRangesFlag | ParseRunner.CancelFlag,
                _ => 0u,
            };
            ParserCorpusTests.AssertSame(grammar, "seed " + seed.ToString(System.Globalization.CultureInfo.InvariantCulture), input, flags);
            if (ParseRunner.Run(grammar, input, 0).Contains("ERROR", StringComparison.Ordinal) ||
                ParseRunner.Run(grammar, input, 0).Contains("MISSING", StringComparison.Ordinal))
            {
                withErrors++;
            }
        }

        // The mutations must reach the error recovery, otherwise the test checks little.
        // A third: bash accepts most mutated inputs as words (972 of 2000 have errors).
        Assert.True(withErrors > (minimumWithErrors < 0 ? count / 3 : minimumWithErrors),
            $"only {withErrors} of {count} inputs have errors");
    }

    /// <summary>One to four random edits of the input.</summary>
    internal static byte[] Mutate(Random random, byte[] source, string[] pieces)
    {
        List<byte> bytes = new List<byte>(source);
        int edits = random.Next(1, 5);
        for (int e = 0; e < edits; e++)
        {
            int position = random.Next(bytes.Count + 1);
            switch (random.Next(6))
            {
                case 0:
                case 1:
                    // Delete a piece.
                    if (bytes.Count > 0)
                    {
                        int start = random.Next(bytes.Count);
                        int length = Math.Min(random.Next(1, 8), bytes.Count - start);
                        bytes.RemoveRange(start, length);
                    }

                    break;
                case 2:
                case 3:
                    // Insert punctuation of the grammar.
                    for (int i = random.Next(1, 4); i > 0; i--)
                    {
                        bytes.InsertRange(position, Encoding.UTF8.GetBytes(pieces[random.Next(pieces.Length)]));
                    }

                    break;
                case 4:
                    // Repeat a piece of the input somewhere else.
                    if (bytes.Count > 0)
                    {
                        int start = random.Next(bytes.Count);
                        int length = Math.Min(random.Next(1, 16), bytes.Count - start);
                        byte[] piece = bytes.GetRange(start, length).ToArray();
                        bytes.InsertRange(Math.Min(position, bytes.Count), piece);
                    }

                    break;
                default:
                    // A byte that is not valid UTF-8, or a multi-byte character.
                    if (random.Next(2) == 0)
                    {
                        bytes.Insert(position, (byte)random.Next(0x80, 0x100));
                    }
                    else
                    {
                        bytes.InsertRange(position, Encoding.UTF8.GetBytes("é中"));
                    }

                    break;
            }
        }

        return bytes.ToArray();
    }
}
