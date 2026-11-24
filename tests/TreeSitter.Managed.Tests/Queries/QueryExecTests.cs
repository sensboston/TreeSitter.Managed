using System.Globalization;
using System.Text;
using TreeSitter.Tests.Subtrees;
using TreeSitter.Tests.Oracle;
using TreeSitter.Tests.Parsing;
using Xunit;

namespace TreeSitter.Tests.Queries;

/// <summary>
/// Runs queries on parsed trees natively and with the port (ts_query_cursor_*): the
/// matches, the captures and the cursor results must be equal (tests/Oracle/FORMATS.md,
/// section "oracle_query_exec").
/// </summary>
public class QueryExecTests
{
    /// <summary>The query files of the grammars on every corpus entry, as matches and as captures.</summary>
    [OracleTheory]
    [InlineData("json", "Json", 40)]
    [InlineData("c", "C", 100)]
    [InlineData("go", "Go", 100)]
    [InlineData("java", "Java", 100)]
    [InlineData("css", "Css", 100)]
    [InlineData("html", "Html", 142)]
    [InlineData("javascript", "JavaScript", 100)]
    [InlineData("rust", "Rust", 100)]
    [InlineData("cpp", "Cpp", 989)]
    [InlineData("c_sharp", "CSharp", 11125)]
    [InlineData("bash", "Bash", 2152)]
    [InlineData("toml", "Toml", 175)]
    [InlineData("typescript", "TypeScript", 1424)]
    [InlineData("tsx", "Tsx", 1418)]
    [InlineData("yaml", "Yaml", 1196)]
    [InlineData("markdown_inline", "MarkdownInline", 1450)]
    [InlineData("markdown", "Markdown", 1521)]
    public void QueryFilesOnCorpusMatchOracle(string grammar, string directory, int minimumMatches)
    {
        NativeOracle.EnsureAvailable();
        List<byte[]> queries = QueryFiles(directory);
        int matches = 0;
        foreach (byte[] source in Corpus(directory))
        {
            foreach (byte[] query in queries)
            {
                string expected = AssertSame(grammar, "corpus", source, query, "matches\ncaptures\n");
                matches += CountLines(expected, "match ");
            }
        }

        Assert.True(matches >= minimumMatches, $"only {matches} matches");
    }

    /// <summary>
    /// Random queries (the hand-written ones of QueryCompileTests and pieces of the query
    /// files) on random corpus entries, with random cursor settings: ranges, match limits,
    /// start depth, disabled captures and patterns, removed matches and a stopped execution.
    /// </summary>
    [OracleTheory]
    [InlineData("json", "Json", 1500)]
    [InlineData("c", "C", 1500)]
    public void RandomExecutionsMatchOracle(string grammar, string directory, int count)
    {
        NativeOracle.EnsureAvailable();
        List<byte[]> sources = Corpus(directory).ToList();
        List<byte[]> queries = QueryPool(grammar, directory);
        int matches = 0;
        int exceeded = 0;
        for (int seed = 0; seed < count; seed++)
        {
            Random random = new Random(seed);
            byte[] source = sources[random.Next(sources.Count)];
            byte[] query = queries[random.Next(queries.Count)];
            string commands = Commands(random, source.Length, DisablablePatterns(grammar, query));
            string expected = AssertSame(grammar, "seed " + seed.ToString(CultureInfo.InvariantCulture), source, query, commands);
            matches += CountLines(expected, "match ") + CountLines(expected, "capture ");
            exceeded += CountLines(expected, "exceeded = 1");
        }

        Assert.True(matches > count, $"only {matches} matches and captures");
        Assert.True(exceeded > 0, "the match limit was never exceeded");
    }

    /// <summary>
    /// The patterns that may be disabled. ts_query_disable_pattern of v0.27.2 removes the
    /// pattern from pattern_map but keeps wildcard_root_pattern_count, so disabling a
    /// pattern with an entry among the first wildcard_root_pattern_count entries makes the
    /// cursor read pattern_map past its end (an assert in the native oracle, undefined
    /// behavior without asserts). Such patterns are left out; the pattern map comes from
    /// the compiled query (Query is checked against the native one by QueryCompileTests).
    /// </summary>
    private static List<int> DisablablePatterns(string grammar, byte[] query)
    {
        List<int> result = new List<int>();
        Query? compiled = Query.TryCreate(ScriptLanguages.Get(grammar)!, query, out _, out _);
        if (compiled is null)
        {
            return result;
        }

        ReadOnlySpan<PatternEntry> map = compiled.PatternMap;
        for (int pattern = 0; pattern < compiled.PatternCount; pattern++)
        {
            bool wildcardRoot = false;
            for (int i = 0; i < compiled.WildcardRootPatternCount && i < map.Length; i++)
            {
                wildcardRoot |= map[i].PatternIndex == pattern;
            }

            if (!wildcardRoot)
            {
                result.Add(pattern);
            }
        }

        return result;
    }

    private static string Commands(Random random, int sourceLength, List<int> disablablePatterns)
    {
        StringBuilder commands = new StringBuilder();
        int settings = random.Next(4);
        for (int i = 0; i < settings; i++)
        {
            int start = random.Next(sourceLength + 2);
            int end = start + random.Next(sourceLength + 2);
            switch (random.Next(8))
            {
                case 0:
                    commands.Append(CultureInfo.InvariantCulture, $"match_limit {random.Next(1, 4)}\n");
                    break;
                case 1:
                    commands.Append(CultureInfo.InvariantCulture, $"max_start_depth {random.Next(0, 4)}\n");
                    break;
                case 2:
                    commands.Append(CultureInfo.InvariantCulture, $"byte_range {start} {(random.Next(6) == 0 ? start / 2 : end)}\n");
                    break;
                case 3:
                    commands.Append(CultureInfo.InvariantCulture, $"containing_byte_range {start} {end}\n");
                    break;
                case 4:
                    commands.Append(CultureInfo.InvariantCulture, $"point_range {random.Next(4)}:{random.Next(6)} {random.Next(2, 9)}:{random.Next(6)}\n");
                    break;
                case 5:
                    commands.Append(CultureInfo.InvariantCulture, $"containing_point_range {random.Next(3)}:{random.Next(6)} {random.Next(1, 6)}:{random.Next(6)}\n");
                    break;
                case 6:
                    if (disablablePatterns.Count > 0)
                    {
                        commands.Append(CultureInfo.InvariantCulture, $"disable_pattern {disablablePatterns[random.Next(disablablePatterns.Count)]}\n");
                    }

                    break;
                default:
                    commands.Append(random.Next(2) == 0 ? "disable_capture name\n" : "disable_capture x\n");
                    break;
            }
        }

        switch (random.Next(5))
        {
            case 0:
                commands.Append("captures\n");
                break;
            case 1:
                commands.Append(CultureInfo.InvariantCulture, $"captures remove_every {random.Next(1, 4)}\n");
                break;
            case 2:
                commands.Append(CultureInfo.InvariantCulture, $"matches remove_every {random.Next(1, 4)}\n");
                break;
            case 3:
                commands.Append(CultureInfo.InvariantCulture, $"cancel {random.Next(1, 5)}\n");
                break;
            default:
                commands.Append("matches\n");
                break;
        }

        // A second execution on the same cursor.
        if (random.Next(3) == 0)
        {
            commands.Append(random.Next(2) == 0 ? "matches\n" : "captures\n");
        }

        return commands.ToString();
    }

    /// <summary>The hand-written queries that compile, the query files and their single patterns.</summary>
    private static List<byte[]> QueryPool(string grammar, string directory)
    {
        List<byte[]> pool = new List<byte[]>();
        foreach (object[] row in QueryCompileTests.Queries())
        {
            if ((string)row[0] == grammar)
            {
                pool.Add(Encoding.UTF8.GetBytes((string)row[1]));
            }
        }

        foreach (byte[] file in QueryFiles(directory))
        {
            pool.Add(file);

            // Each top-level pattern of the file alone: the lines from one that starts at
            // column 0 with '(' or '[' up to the next such line.
            string[] lines = Encoding.UTF8.GetString(file).Split('\n');
            StringBuilder pattern = new StringBuilder();
            foreach (string line in lines)
            {
                if ((line.StartsWith('(') || line.StartsWith('[')) && pattern.Length > 0)
                {
                    pool.Add(Encoding.UTF8.GetBytes(pattern.ToString()));
                    pattern.Clear();
                }

                pattern.Append(line).Append('\n');
            }

            pool.Add(Encoding.UTF8.GetBytes(pattern.ToString()));
        }

        // Only the queries that compile: the errors are the subject of QueryCompileTests.
        Language language = ScriptLanguages.Get(grammar)!;
        return pool.Where(query => Query.TryCreate(language, query, out _, out _) is not null).ToList();
    }

    private static List<byte[]> QueryFiles(string directory)
    {
        string path = Path.Combine(FindRoot(), "grammars", directory, "upstream", "queries");
        return Directory.GetFiles(path, "*.scm").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
    }

    private static IEnumerable<byte[]> Corpus(string directory) =>
        ParserCorpusTests.ReadCorpus(directory).Select(entry => Encoding.UTF8.GetBytes(entry.Input));

    private static int CountLines(string text, string prefix) =>
        text.Split('\n').Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static string AssertSame(string grammar, string name, byte[] source, byte[] query, string commands)
    {
        string expected = NativeOracle.OracleQueryExec(grammar, source, query, commands);
        string actual = QueryExecRunner.Run(grammar, source, query, commands);
        if (expected == actual)
        {
            return expected;
        }

        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
        {
            i++;
        }

        Assert.Fail(
            $"{grammar} {name}: first difference at line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', " +
            $"managed '{(i < a.Length ? a[i] : "<end>")}'\ncommands:\n{commands}query:\n{Encoding.UTF8.GetString(query)}\n" +
            $"source:\n{Encoding.UTF8.GetString(source)}");
        return expected;
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "TreeSitter.Managed.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
