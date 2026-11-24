using System;
using System.Text;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Lexing;

public sealed class LexerCommandTests
{
    [OracleTheory]
    [InlineData("json", "main", "{}")]
    [InlineData("c", "main", "int x;")]
    [InlineData("python", "main", "x = 1")]
    [InlineData("python", "keyword", "if")]
    public void GeneratedLexersMatchNative(string language, string kind, string input)
    {
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(input));
        string script = $"input utf8 0 {hex}\nreset 0:0:0\nlex {language} {kind} 0\n";
        string expected = NativeOracle.OracleLexerScript(script)!;
        string actual = LexerScriptRunner.Run(script);
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"{language}/{kind}: native and managed lexer-script output differ.");
    }

    [OracleTheory]
    [InlineData("lex json main 65536\n", "state must be in 0..65535")]
    [InlineData("lex json keyword 0\n", "language has no keyword lexer")]
    [InlineData("lex c invalid 0\n", "lexer kind must be main or keyword")]
    public void InvalidLexCommandsAreReported(string script, string message)
    {
        string expected = NativeOracle.OracleLexerScript(script)!;
        string actual = LexerScriptRunner.Run(script);
        Assert.Contains("error line 1: " + message, expected, StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }
}
