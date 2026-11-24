using System.Text;
using TreeSitter.Tests.Lexing;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Parsing;

/// <summary>
/// Every entry of the grammar's own test corpus is parsed natively and by the port: the log,
/// the S-expression and the subtree dump must be equal, for a new parse and for a reparse
/// with the old tree.
/// </summary>
public class ParserCorpusTests
{
    [OracleTheory]
    [InlineData("json", "Json")]
    [InlineData("c", "C")]
    [InlineData("python", "Python")]
    [InlineData("go", "Go")]
    [InlineData("java", "Java")]
    [InlineData("css", "Css")]
    [InlineData("html", "Html")]
    [InlineData("javascript", "JavaScript")]
    [InlineData("rust", "Rust")]
    [InlineData("cpp", "Cpp")]
    [InlineData("c_sharp", "CSharp")]
    [InlineData("bash", "Bash")]
    [InlineData("toml", "Toml")]
    [InlineData("typescript", "TypeScript")]
    [InlineData("tsx", "Tsx")]
    [InlineData("yaml", "Yaml")]
    [InlineData("markdown_inline", "MarkdownInline")]
    [InlineData("markdown", "Markdown")]
    public void CorpusMatchesOracle(string grammar, string directory)
    {
        NativeOracle.EnsureAvailable();
        int count = 0;
        foreach (CorpusEntry entry in ReadCorpus(directory))
        {
            byte[] input = Encoding.UTF8.GetBytes(entry.Input);
            AssertSame(grammar, entry.Name, input, ParseRunner.LogFlag | ParseRunner.ReparseFlag);
            count++;
        }

        Assert.True(count > 0, "the corpus is empty");
    }

    internal static IEnumerable<CorpusEntry> ReadCorpus(string directory)
    {
        string root = FindRoot();
        string path = Path.Combine(root, "grammars", directory, "upstream", "test", "corpus");
        foreach (string file in Directory.GetFiles(path, "*.txt", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            foreach (CorpusEntry entry in CorpusReader.Read(file))
            {
                yield return entry;
            }
        }
    }

    internal static void AssertSame(string grammar, string name, byte[] input, uint flags)
    {
        string expected = NativeOracle.OracleParse(grammar, input, flags);
        string actual = ParseRunner.Run(grammar, input, flags);
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
