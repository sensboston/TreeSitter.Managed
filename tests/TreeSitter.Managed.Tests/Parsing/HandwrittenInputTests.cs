using System.Text;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Parsing;

/// <summary>
/// Hand-written inputs for the branches of the TOML external scanner that neither the grammar's
/// corpus nor the fuzzer reaches: the fuzzer's piece list has no carriage return, and it rarely
/// builds four or five quotes in a row. Each input is parsed natively and by the port, and the
/// log, the S-expression and the subtree dump must be equal (ParserCorpusTests.AssertSame).
/// </summary>
public class HandwrittenInputTests
{
    /// <summary>
    /// Line endings and carriage returns after a value, after a comment and at the end of the file
    /// (scanner.c: LINE_ENDING_OR_EOF, the '\r' branch), and multiline basic strings that end with
    /// four or five quotes (scanner.c: ScanMultilineStringEnd, the content branches).
    /// </summary>
    [OracleTheory]
    [InlineData("toml", "a = 1\r\nb = 2\r\n")]
    [InlineData("toml", "a = 1\rb = 2\r")]
    [InlineData("toml", "a = 1\r")]
    [InlineData("toml", "a = 1 \r\n")]
    [InlineData("toml", "# comment\r\na = 1\r\n")]
    [InlineData("toml", "# comment\ra = 1\r")]
    [InlineData("toml", "a = 1 # comment\r\n")]
    [InlineData("toml", "a = 1\r\r\nb = 2")]
    [InlineData("toml", "a = \"\"\"x\"\"\"\"")]
    [InlineData("toml", "a = \"\"\"x\"\"\"\"\"")]
    [InlineData("toml", "a = '''x''''")]
    [InlineData("toml", "a = '''x'''''")]
    public void HandwrittenInputMatchesOracle(string grammar, string input) =>
        ParserCorpusTests.AssertSame(grammar, "handwritten", Encoding.UTF8.GetBytes(input), ParseRunner.LogFlag);
}
