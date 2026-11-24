using TreeSitter.Generator.Dump;
using TreeSitter.Generator.Model;
using TreeSitter.Tests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace TreeSitter.Tests.Generator;

// Contract tests for TableDumper (task M1a-T4c).
public class TableDumperTests
{
    private readonly ITestOutputHelper _output;

    public TableDumperTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(null, "null")]
    [InlineData("end", "\"end\"")]
    [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"")]
    [InlineData("\n\t", "\"\\n\\t\"")]
    [InlineData("\r\u007f", "\"\\x0d\\x7f\"")]
    [InlineData("é", "\"\\xc3\\xa9\"")]
    public void FormatsStringsLikeTheShim(string? value, string expected)
    {
        Assert.Equal(expected, TableDumper.FormatString(value));
    }

    [OracleFact]
    public void JsonTablesMatchOracle() => AssertMatchesOracle("Json", "json");

    [OracleFact]
    public void CTablesMatchOracle() => AssertMatchesOracle("C", "c");

    [OracleFact]
    public void PythonTablesMatchOracle() => AssertMatchesOracle("Python", "python");

    [OracleFact]
    public void GoTablesMatchOracle() => AssertMatchesOracle("Go", "go");

    [OracleFact]
    public void JavaTablesMatchOracle() => AssertMatchesOracle("Java", "java");

    [OracleFact]
    public void CppTablesMatchOracle() => AssertMatchesOracle("Cpp", "cpp");

    [OracleFact]
    public void CSharpTablesMatchOracle() => AssertMatchesOracle("CSharp", "c_sharp");

    [OracleFact]
    public void BashTablesMatchOracle() => AssertMatchesOracle("Bash", "bash");

    [OracleFact]
    public void TypeScriptTablesMatchOracle() => AssertMatchesOracle("TypeScript", "typescript");

    [OracleFact]
    public void TsxTablesMatchOracle() => AssertMatchesOracle("Tsx", "tsx");

    [OracleFact]
    public void YamlTablesMatchOracle() => AssertMatchesOracle("Yaml", "yaml");

    [OracleFact]
    public void TomlTablesMatchOracle() => AssertMatchesOracle("Toml", "toml");

    [OracleFact]
    public void MarkdownTablesMatchOracle() => AssertMatchesOracle("Markdown", "markdown");

    [OracleFact]
    public void MarkdownInlineTablesMatchOracle() => AssertMatchesOracle("MarkdownInline", "markdown_inline");

    [OracleFact]
    public void CssTablesMatchOracle() => AssertMatchesOracle("Css", "css");

    [OracleFact]
    public void RustTablesMatchOracle() => AssertMatchesOracle("Rust", "rust");

    [OracleFact]
    public void JavaScriptTablesMatchOracle() => AssertMatchesOracle("JavaScript", "javascript");

    [OracleFact]
    public void HtmlTablesMatchOracle() => AssertMatchesOracle("Html", "html");

    private void AssertMatchesOracle(string grammar, string oracleName)
    {
        NativeOracle.EnsureAvailable();
        string expected = NativeOracle.OracleDumpTables(oracleName)!;
        string actual = TableDumper.Dump(GrammarModelBuilder.Build(CParserTests.GrammarParserC(grammar)).Tables);
        if (expected == actual)
        {
            return;
        }

        // Print the first difference with 3 lines of context from both sides.
        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
        {
            i++;
        }
        for (int k = Math.Max(0, i - 3); k <= i + 3; k++)
        {
            _output.WriteLine($"{k + 1,7} oracle : {(k < e.Length ? e[k] : "<end>")}");
            _output.WriteLine($"{k + 1,7} managed: {(k < a.Length ? a[k] : "<end>")}");
        }
        Assert.Fail($"{grammar}: first difference at line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', managed '{(i < a.Length ? a[i] : "<end>")}'");
    }
}
