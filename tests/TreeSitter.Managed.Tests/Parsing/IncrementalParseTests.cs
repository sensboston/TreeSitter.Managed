using System.Globalization;
using System.Text;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Parsing;

/// <summary>
/// Incremental parsing: random edits of the corpus inputs (ts_tree_edit, ts_node_edit, a
/// reparse with the old tree, ts_tree_get_changed_ranges) natively and with the port. The
/// log, the changed ranges and the trees must be equal.
/// </summary>
public class IncrementalParseTests
{
    private static readonly string[] JsonPieces =
    [
        "1", "{}", "[]", "\"a\"", ",", ":", " ", "\n", "true", "null", "[1, 2]", "{\"k\": 0}", "\"", "]", "}",
    ];

    private static readonly string[] CPieces =
    [
        "x", "1", ";", " ", "\n", "(", ")", "{", "}", "int y;", "return 0;", "/* c */", "// c\n", "+", "*",
        "if (a) b();", "\"s\"", "#define Z 2\n", ",", "[", "]",
    ];

    [OracleTheory]
    [InlineData("json", "Json", 3000)]
    [InlineData("c", "C", 2000)]
    [InlineData("python", "Python", 2000)]
    [InlineData("go", "Go", 1000)]
    [InlineData("java", "Java", 1000)]
    [InlineData("css", "Css", 1000)]
    [InlineData("html", "Html", 1000)]
    [InlineData("javascript", "JavaScript", 1000)]
    [InlineData("rust", "Rust", 1000)]
    [InlineData("cpp", "Cpp", 1000)]
    [InlineData("c_sharp", "CSharp", 1000)]
    [InlineData("bash", "Bash", 1000)]
    [InlineData("toml", "Toml", 1000)]
    [InlineData("typescript", "TypeScript", 1000)]
    [InlineData("tsx", "Tsx", 1000)]
    [InlineData("yaml", "Yaml", 1000)]
    [InlineData("markdown_inline", "MarkdownInline", 1000)]
    [InlineData("markdown", "Markdown", 1000)]
    public void RandomEditsMatchOracle(string grammar, string directory, int count)
    {
        NativeOracle.EnsureAvailable();
        List<byte[]> inputs = ParserCorpusTests.ReadCorpus(directory)
            .Select(entry => Encoding.UTF8.GetBytes(entry.Input))
            .ToList();
        string[] pieces = grammar == "json" ? JsonPieces : CPieces;
        int changed = 0;
        for (int seed = 0; seed < count; seed++)
        {
            Random random = new Random(seed);
            byte[] input = inputs[random.Next(inputs.Count)];
            string edits = Edits(random, input.Length, pieces);
            uint flags = seed % 2 == 0 ? ParseRunner.LogFlag : 0u;
            string expected = NativeOracle.OracleEdit(grammar, input, edits, flags);
            string actual = EditRunner.Run(grammar, input, edits, flags);
            if (expected != actual)
            {
                string[] e = expected.Split('\n');
                string[] a = actual.Split('\n');
                int i = 0;
                while (i < e.Length && i < a.Length && e[i] == a[i])
                {
                    i++;
                }

                Assert.Fail(
                    $"{grammar} seed {seed}: first difference at line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', " +
                    $"managed '{(i < a.Length ? a[i] : "<end>")}'\nprevious: '{(i > 0 ? e[i - 1] : string.Empty)}'\n" +
                    $"edits:\n{edits}input: {Encoding.UTF8.GetString(input)}");
            }

            if (expected.Split('\n').Any(line =>
                line.StartsWith("changed_ranges = ", StringComparison.Ordinal) &&
                !line.Equals("changed_ranges = 0", StringComparison.Ordinal)))
            {
                changed++;
            }
        }

        // The edits must change the trees, otherwise the test checks little.
        Assert.True(changed > count / 2, $"only {changed} of {count} scripts changed the tree");
    }

    /// <summary>One to five edits: a start, a number of deleted bytes, inserted bytes in hex.</summary>
    private static string Edits(Random random, int length, string[] pieces)
    {
        StringBuilder edits = new StringBuilder();
        int textLength = length;
        for (int i = random.Next(1, 6); i > 0; i--)
        {
            int start = random.Next(textLength + 1);
            int deleted = random.Next(3) == 0 ? 0 : Math.Min(random.Next(1, 8), textLength - start);
            byte[] inserted = random.Next(4) == 0
                ? []
                : Encoding.UTF8.GetBytes(pieces[random.Next(pieces.Length)]);
            edits.Append(CultureInfo.InvariantCulture, $"{start} {deleted} {Convert.ToHexString(inserted).ToLowerInvariant()}\n");
            textLength += inserted.Length - deleted;
        }

        return edits.ToString();
    }
}
