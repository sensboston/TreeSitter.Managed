using System.Globalization;
using System.IO;
using System.Text;
using TreeSitter.Tests.Lexing;
using TreeSitter.Tests.Oracle;
using TreeSitter.Tests.Subtrees;
using Xunit;

namespace TreeSitter.Tests.Parsing;

/// <summary>
/// The dot graphs printed by the port must be equal to the native oracle's output
/// byte for byte: the parser dot graphs (`ts_parser_print_dot_graphs`: the log, the
/// stack graphs and the tree graph) and the tree dot graph (`ts_tree_print_dot_graph`).
/// The native oracle normalizes the addresses (tests/Oracle/FORMATS.md, sections
/// "oracle_parser_dot_graphs" and "oracle_tree_dot_graph"); the port prints sequence
/// numbers instead of addresses, so the two texts are compared directly.
/// </summary>
public class DotGraphTests
{
    // A grammar's corpus is limited when the parser dot graphs of its entries exceed
    // 50 MB in total: entries are taken in file order, and an entry whose (raw, never
    // smaller than the compared normalized) parser dot graphs would take the running
    // total over the limit is skipped. The size of every entry is read without building its text, so an
    // entry that does not fit costs only the parse. The first entry is always taken,
    // because the test needs at least one entry per grammar. Measured with the native
    // oracle:
    //   json: main.txt is about 0.7 MB of dot graphs.
    //   c: ambiguities.txt entries are about 12 MB each, seven entries 86 MB.
    //   python: errors.txt is about 2.4 MB.
    //   bash: commands.txt is 44 MB, crlf.txt and programs.txt add 1 MB.
    //   markdown: six entries add 23 MB; spec.txt alone is 403 MB.
    private const ulong MaxCorpusBytesPerGrammar = 50ul * 1024 * 1024;

    // A mutated input's parser dot graphs are compared only while they stay within
    // the limit (the C grammar gives tens to hundreds of megabytes for large inputs
    // with errors). The size is read without building the text, because
    // materializing those outputs only to discard them exhausts the process on
    // Windows. The tree dot graph is compared for every mutated input.
    private const ulong MaxMutatedParserBytes = 50ul * 1024 * 1024;

    // Entries that do not fit the corpus budget are skipped, not the rest of the
    // corpus, so every grammar compares at least this many entries (or all of a
    // smaller corpus).
    private const int MinCorpusEntries = 10;

    // The mutated inputs start from the corpus entries up to this size: the parser
    // dot graphs grow with the input and with every syntax error, and the graphs of
    // larger C inputs never fit the cap above.
    private const int MaxMutatedSourceBytes = 512;

    [OracleTheory]
    [InlineData("json", "Json")]
    [InlineData("c", "C")]
    [InlineData("python", "Python")]
    [InlineData("bash", "Bash")]
    [InlineData("markdown", "Markdown")]
    public void CorpusDotGraphsMatchOracle(string grammar, string directory)
    {
        NativeOracle.EnsureAvailable();
        ulong total = 0;
        int count = 0;
        int skipped = 0;
        bool any = false;
        foreach (CorpusEntry entry in ParserCorpusTests.ReadCorpus(directory))
        {
            byte[] input = Encoding.UTF8.GetBytes(entry.Input);
            // The size of the parser dot graphs decides whether the entry fits the
            // budget. It is read without building the text: an entry that does not fit
            // would otherwise be materialized only to be discarded, and some inputs
            // produce hundreds of megabytes. The raw size is never smaller than the
            // normalized text compared below, so an entry skipped here would not have
            // fit either. The first entry is always taken (see the note above).
            ulong parserSize = NativeOracle.OracleParserDotGraphsSize(grammar, input);
            if (any && total + parserSize > MaxCorpusBytesPerGrammar)
            {
                skipped++;
                continue;
            }

            string expectedParser = NativeOracle.OracleParserDotGraphs(grammar, input);
            string expectedTree = NativeOracle.OracleTreeDotGraph(grammar, input);
            total += parserSize;
            any = true;
            count++;
            AssertDotGraphsEqual(
                grammar, entry.Name + " parser", input,
                expectedParser, RunParserDotGraphs(grammar, input));
            AssertDotGraphsEqual(
                grammar, entry.Name + " tree", input,
                expectedTree, RunTreeDotGraph(grammar, input));
        }

        Assert.True(count >= Math.Min(MinCorpusEntries, count + skipped), $"only {count} corpus entries fit the budget");
        Console.WriteLine(
            string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} corpus entries compared, {2} skipped as too large, {3:N0} bytes of dot graphs",
                grammar, count, skipped, total));
    }

    [OracleTheory]
    [InlineData("json", "Json", 200)]
    [InlineData("c", "C", 200)]
    public void MutatedDotGraphsMatchOracle(string grammar, string directory, int count)
    {
        NativeOracle.EnsureAvailable();
        List<byte[]> inputs = ParserCorpusTests.ReadCorpus(directory)
            .Select(entry => Encoding.UTF8.GetBytes(entry.Input))
            .Where(bytes => bytes.Length <= MaxMutatedSourceBytes)
            .ToList();
        Assert.NotEmpty(inputs);
        string[] pieces = grammar == "json" ? ParserFuzzTests.JsonPieces : ParserFuzzTests.CPieces;
        int comparedTree = 0;
        int comparedParser = 0;
        int skippedParser = 0;
        for (int seed = 0; seed < count; seed++)
        {
            Random random = new Random(seed);
            byte[] input = ParserFuzzTests.Mutate(random, inputs[random.Next(inputs.Count)], pieces);

            string expectedTree = NativeOracle.OracleTreeDotGraph(grammar, input);
            AssertDotGraphsEqual(
                grammar, "seed " + seed.ToString(CultureInfo.InvariantCulture) + " tree", input,
                expectedTree, RunTreeDotGraph(grammar, input));
            comparedTree++;

            // Read the size without building the text, and build and compare only what
            // fits the cap. Materializing the skipped outputs would risk exhausting the
            // process, which aborts natively on Windows when malloc fails.
            ulong parserSize = NativeOracle.OracleParserDotGraphsSize(grammar, input);
            if (parserSize <= MaxMutatedParserBytes)
            {
                string expectedParser = NativeOracle.OracleParserDotGraphs(grammar, input);
                AssertDotGraphsEqual(
                    grammar, "seed " + seed.ToString(CultureInfo.InvariantCulture) + " parser", input,
                    expectedParser, RunParserDotGraphs(grammar, input));
                comparedParser++;
            }
            else
            {
                skippedParser++;
            }
        }

        Assert.True(comparedTree == count, "not every tree dot graph was compared");
        Assert.True(comparedParser >= count * 3 / 4, $"only {comparedParser} of {count} parser dot graphs fit the cap");
        Console.WriteLine(
            string.Format(CultureInfo.InvariantCulture,
                "{0}: tree {1}/{2} compared, parser {3}/{4} compared, {5} skipped as too large",
                grammar, comparedTree, count, comparedParser, count, skippedParser));
    }

    /// <summary>The managed side of oracle_parser_dot_graphs.</summary>
    private static string RunParserDotGraphs(string grammar, byte[] utf8)
    {
        StringWriter output = new(CultureInfo.InvariantCulture);
        Language? language = ScriptLanguages.Get(grammar);
        if (language is null)
        {
            return "error: unknown language\n";
        }

        Parser parser = new Parser();
        parser.SetLanguage(language);
        parser.PrintDotGraphs(output);
        Tree? tree = parser.ParseString(utf8);
        tree?.Dispose();
        return output.ToString();
    }

    /// <summary>The managed side of oracle_tree_dot_graph.</summary>
    private static string RunTreeDotGraph(string grammar, byte[] utf8)
    {
        StringWriter output = new(CultureInfo.InvariantCulture);
        Language? language = ScriptLanguages.Get(grammar);
        if (language is null)
        {
            return "error: unknown language\n";
        }

        Parser parser = new Parser();
        parser.SetLanguage(language);
        Tree? tree = parser.ParseString(utf8);
        if (tree is null)
        {
            return string.Empty;
        }

        tree.PrintDotGraph(output);
        tree.Dispose();
        return output.ToString();
    }

    private static void AssertDotGraphsEqual(string grammar, string name, byte[] input, string expected, string actual)
    {
        if (expected == actual)
        {
            return;
        }

        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
        {
            i++;
        }

        Assert.Fail(
            $"{grammar} '{name}': first difference at line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', " +
            $"managed '{(i < a.Length ? a[i] : "<end>")}'\nprevious: '{(i > 0 ? e[i - 1] : string.Empty)}'\ninput: {Encoding.UTF8.GetString(input)}");
    }
}
