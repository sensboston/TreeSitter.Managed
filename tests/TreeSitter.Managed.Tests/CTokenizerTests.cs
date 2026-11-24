using Xunit;
using TreeSitter.Generator.CSource;

namespace TreeSitter.Tests;

public class CTokenizerTests
{
    [Fact]
    public void TokenizesIdentifiersAndKeywords()
    {
        string source = "anon_sym_LBRACE sym_document ts_builtin_sym_end";
        var tokens = Tokenize(source);

        Assert.Equal(4, tokens.Count);
        Assert.Equal(CTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("anon_sym_LBRACE", tokens[0].Text);
        Assert.Equal(CTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("sym_document", tokens[1].Text);
        Assert.Equal(CTokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("ts_builtin_sym_end", tokens[2].Text);
        Assert.Equal(CTokenKind.EOF, tokens[3].Kind);
    }

    [Fact]
    public void TokenizesDecimalIntegerLiterals()
    {
        string source = "15 32 0 255u 65535L";
        var tokens = Tokenize(source);

        Assert.Equal(6, tokens.Count);
        foreach (CToken token in tokens.Take(5))
        {
            Assert.Equal(CTokenKind.NumberLiteral, token.Kind);
        }

        Assert.Equal("15", tokens[0].Text);
        Assert.Equal("32", tokens[1].Text);
        Assert.Equal("0", tokens[2].Text);
        Assert.Equal("255u", tokens[3].Text);
        Assert.Equal("65535L", tokens[4].Text);
        Assert.Equal(CTokenKind.EOF, tokens[5].Kind);
    }

    [Fact]
    public void TokenizesHexIntegerLiterals()
    {
        string source = "0x1F 0xFF 0x0";
        var tokens = Tokenize(source);

        Assert.Equal(4, tokens.Count);
        Assert.Equal("0x1F", tokens[0].Text);
        Assert.Equal("0xFF", tokens[1].Text);
        Assert.Equal("0x0", tokens[2].Text);
        Assert.Equal(CTokenKind.EOF, tokens[3].Kind);
    }

    [Fact]
    public void TokenizesIntegerSuffixCombinations()
    {
        var tokens = Tokenize("0XFFuLl 42LLU");

        Assert.Equal(3, tokens.Count);
        Assert.Equal("0XFFuLl", tokens[0].Text);
        Assert.Equal("42LLU", tokens[1].Text);
        Assert.Equal(CTokenKind.EOF, tokens[2].Kind);
    }

    [Fact]
    public void TokenizesStringLiteralsWithEscapes()
    {
        string source = "\"hello\" \"\\\"quoted\\\"\" \"\\n\\t\\\\\"";
        var tokens = Tokenize(source);

        Assert.Equal(4, tokens.Count);
        Assert.Equal(CTokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("\"hello\"", tokens[0].Text);
        Assert.Equal("\"\\\"quoted\\\"\"", tokens[1].Text);
        Assert.Equal("\"\\n\\t\\\\\"", tokens[2].Text);
        Assert.Equal(CTokenKind.EOF, tokens[3].Kind);
    }

    [Fact]
    public void TokenizesCharLiteralsWithEscapes()
    {
        string source = "'a' '\\n' '\\'' '\\\\' '\\0'";
        var tokens = Tokenize(source);

        Assert.Equal(6, tokens.Count);
        Assert.Equal(CTokenKind.CharLiteral, tokens[0].Kind);
        Assert.Equal("'a'", tokens[0].Text);
        Assert.Equal("'\\n'", tokens[1].Text);
        Assert.Equal("'\\''", tokens[2].Text);
        Assert.Equal("'\\\\'", tokens[3].Text);
        Assert.Equal("'\\0'", tokens[4].Text);
        Assert.Equal(CTokenKind.EOF, tokens[5].Kind);
    }

    [Fact]
    public void TokenizesComments()
    {
        string source = "// line comment\n/* block comment */\nident";
        var tokens = Tokenize(source);

        Assert.Equal(2, tokens.Count);
        Assert.Equal(CTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("ident", tokens[0].Text);
        Assert.Equal(CTokenKind.EOF, tokens[1].Kind);
    }

    [Fact]
    public void TokenizesMultiLineBlockComment()
    {
        string source = "/* multi\nline\ncomment */\nx";
        var tokens = Tokenize(source);

        Assert.Equal(2, tokens.Count);
        Assert.Equal(CTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("x", tokens[0].Text);
        Assert.Equal(CTokenKind.EOF, tokens[1].Kind);
    }

    [Fact]
    public void TokenizesPreprocessorDirective()
    {
        string source = "#define LANGUAGE_VERSION 15\nident";
        var tokens = Tokenize(source);

        Assert.Equal(3, tokens.Count);
        Assert.Equal(CTokenKind.PreprocessorDirective, tokens[0].Kind);
        Assert.Equal("#define LANGUAGE_VERSION 15", tokens[0].Text);
        Assert.Equal(CTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("ident", tokens[1].Text);
        Assert.Equal(CTokenKind.EOF, tokens[2].Kind);
    }

    [Fact]
    public void TokenizesPreprocessorWithLineContinuation()
    {
        string source = "#define FOO " + '\\' + "\n  15\nident";
        var tokens = Tokenize(source);

        Assert.Equal(3, tokens.Count);
        Assert.Equal(CTokenKind.PreprocessorDirective, tokens[0].Kind);
        Assert.Equal("#define FOO " + '\\' + "\n  15", tokens[0].Text);
        Assert.Equal(CTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("ident", tokens[1].Text);
        Assert.Equal(3, tokens[1].Line);
        Assert.Equal(CTokenKind.EOF, tokens[2].Kind);
    }

    [Fact]
    public void TokenizesCrLfPreprocessorContinuationAndMidLineHash()
    {
        string source = "#define NAME " + '\\' + "\r\n  1\r\nname # token";
        var tokens = Tokenize(source);

        Assert.Equal("#define NAME " + '\\' + "\r\n  1", tokens[0].Text);
        Assert.Equal(new SourcePos(3, 1), tokens[1].Pos);
        Assert.Equal("name", tokens[1].Text);
        Assert.Equal(CTokenKind.Punctuator, tokens[2].Kind);
        Assert.Equal("#", tokens[2].Text);
        Assert.Equal(CTokenKind.EOF, tokens[^1].Kind);
    }

    [Fact]
    public void TokenizesStandardMultiCharacterPunctuators()
    {
        string[] expected = ["<<=", ">>=", "...", "*=", "/=", "%=", "+=", "-=", "&=", "^=", "|=", "##", "<:", ":>", "<%", "%>", "%:", "%:%:"];
        var tokens = Tokenize(string.Join(" ", expected));

        Assert.Equal(expected, tokens.Take(expected.Length).Select(static token => token.Text));
        Assert.All(tokens.Take(expected.Length), static token => Assert.Equal(CTokenKind.Punctuator, token.Kind));
        Assert.Equal(CTokenKind.EOF, tokens[^1].Kind);
    }

    [Fact]
    public void TokenizesPunctuators()
    {
        string source = "== != <= >= && || ++ -- -> << >> . , ; : ( ) [ ] { } + - * / % ~ ! ? ^ |";
        string[] expected =
        [
            "==", "!=", "<=", ">=", "&&", "||", "++", "--", "->", "<<", ">>",
            ".", ",", ";", ":", "(", ")", "[", "]", "{", "}", "+", "-", "*", "/", "%", "~", "!", "?", "^", "|",
        ];
        var tokens = Tokenize(source);

        Assert.Equal(32, tokens.Count);
        Assert.Equal(expected, tokens.Take(expected.Length).Select(static token => token.Text));
        Assert.All(tokens.Take(expected.Length), static token => Assert.Equal(CTokenKind.Punctuator, token.Kind));
        Assert.Equal(CTokenKind.EOF, tokens[^1].Kind);
    }

    [Fact]
    public void UnterminatedLineCommentDoesNotThrow()
    {
        string source = "// comment without newline\nx";
        var tokens = Tokenize(source);

        Assert.Equal(2, tokens.Count);
        Assert.Equal(CTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("x", tokens[0].Text);
        Assert.Equal(CTokenKind.EOF, tokens[1].Kind);
    }

    [Fact]
    public void TokenTracksLineColumnTabsAndCarriageReturns()
    {
        var tokens = Tokenize("\tname\r\nx");

        Assert.Equal(new SourcePos(1, 2), tokens[0].Pos);
        Assert.Equal("name", tokens[0].Text);
        Assert.Equal(new SourcePos(2, 1), tokens[1].Pos);
        Assert.Equal("x", tokens[1].Text);
        Assert.Equal(new SourcePos(2, 2), tokens[2].Pos);
        Assert.Equal(CTokenKind.EOF, tokens[2].Kind);
    }

    [Fact]
    public void TreatsVerticalSpaceFormFeedAndBareCarriageReturnAsWhitespace()
    {
        var tokens = Tokenize("\v\f\rleft\rright");

        Assert.Equal(new SourcePos(2, 1), tokens[0].Pos);
        Assert.Equal("left", tokens[0].Text);
        Assert.Equal(new SourcePos(3, 1), tokens[1].Pos);
        Assert.Equal("right", tokens[1].Text);
        Assert.Equal(CTokenKind.EOF, tokens[^1].Kind);
    }

    [Fact]
    public void ConstructorRejectsNullTextAndFileName()
    {
        Assert.Throws<ArgumentNullException>(() => new CTokenizer(null!, "test.c"));
        Assert.Throws<ArgumentNullException>(() => new CTokenizer(string.Empty, null!));
    }

    [Fact]
    public void TokenizeReturnsOneEofAndIsRepeatable()
    {
        var tokenizer = new CTokenizer("name", "test.c");

        Assert.Equal("test.c", tokenizer.FileName);
        List<CToken> first = tokenizer.Tokenize();
        List<CToken> second = tokenizer.Tokenize();

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(CTokenKind.EOF, first[^1].Kind);
        Assert.Equal(CTokenKind.EOF, second[^1].Kind);
        Assert.Single(second, static token => token.Kind == CTokenKind.EOF);
    }

    [Fact]
    public void LineCommentAtEndOfFileProducesOnlyEof()
    {
        var tokens = Tokenize("// no final newline");

        Assert.Single(tokens);
        Assert.Equal(CTokenKind.EOF, tokens[0].Kind);
        Assert.Equal(new SourcePos(1, "// no final newline".Length + 1), tokens[0].Pos);
    }

    [Theory]
    [InlineData("x\n  /* open", 2, 3)]
    [InlineData("x\n\t\"open", 2, 2)]
    [InlineData("\"open\nnext\"", 1, 1)]
    [InlineData("\"x\\\n", 1, 1)]
    [InlineData("x\n  'open", 2, 3)]
    [InlineData("'open\nnext'", 1, 1)]
    [InlineData("'x\\\r\n", 1, 1)]
    [InlineData("x\n\t@", 2, 2)]
    public void LexicalErrorsReportStartPosition(string source, int line, int column)
    {
        CSourceException error = Assert.Throws<CSourceException>(() => Tokenize(source));

        Assert.Equal("test.c", error.FileName);
        Assert.Equal(new SourcePos(line, column), error.Pos);
        Assert.StartsWith($"test.c:{line}:{column}: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseIntegerHandlesDecimalHexAndSuffixes()
    {
        Assert.Equal(15, CTokenizer.ParseInteger("15"));
        Assert.Equal(31, CTokenizer.ParseInteger("0x1F"));
        Assert.Equal(255, CTokenizer.ParseInteger("255u"));
        Assert.Equal(255, CTokenizer.ParseInteger("0XFFuLl"));
        Assert.Equal(0, CTokenizer.ParseInteger("0"));
        Assert.Throws<ArgumentNullException>(() => CTokenizer.ParseInteger(null!));
        Assert.Throws<FormatException>(() => CTokenizer.ParseInteger("0x"));
        Assert.Throws<OverflowException>(() => CTokenizer.ParseInteger("9223372036854775808"));
    }

    [Fact]
    public void UnescapeStringHandlesSimpleOctalAndHexEscapes()
    {
        string literal = "\"\\n\\t\\r\\'\\\"\\\\\\0\\101\\x7f\"";
        Assert.Equal("\n\t\r'\"\\\0A\u007f", CTokenizer.UnescapeString(literal));
    }

    [Fact]
    public void UnescapeStringHandlesAdditionalAndUnicodeEscapes()
    {
        string literal = "\"\\a\\b\\f\\v\\?\\q\\x\\u123\\U123\"";
        Assert.Equal("\a\b\f\v?qxu123U123", CTokenizer.UnescapeString(literal));
        Assert.Equal("A😀", CTokenizer.UnescapeString("\"\\u0041\\U0001F600\""));
        Assert.Equal("\u00AF", CTokenizer.UnescapeString("\"\\xAF\""));
        Assert.Equal("\uFFFF", CTokenizer.UnescapeString("\"\\UFFFFFFFF\""));
        Assert.Equal(string.Empty, CTokenizer.UnescapeString("x"));
        Assert.Equal(string.Empty, CTokenizer.UnescapeString("\"\""));
    }

    [Fact]
    public void ParseCharLiteralReturnsSimpleOctalAndHexValues()
    {
        Assert.Equal('a', CTokenizer.ParseCharLiteral("'a'"));
        Assert.Equal('\n', CTokenizer.ParseCharLiteral("'\\n'"));
        Assert.Equal('\t', CTokenizer.ParseCharLiteral("'\\t'"));
        Assert.Equal('\r', CTokenizer.ParseCharLiteral("'\\r'"));
        Assert.Equal('\'', CTokenizer.ParseCharLiteral("'\\''"));
        Assert.Equal('"', CTokenizer.ParseCharLiteral("'\\\"'"));
        Assert.Equal('\\', CTokenizer.ParseCharLiteral("'\\\\'"));
        Assert.Equal('\0', CTokenizer.ParseCharLiteral("'\\0'"));
        Assert.Equal(83, CTokenizer.ParseCharLiteral("'\\123'"));
        Assert.Equal(127, CTokenizer.ParseCharLiteral("'\\x7f'"));
        Assert.Equal(175, CTokenizer.ParseCharLiteral("'\\xAF'"));
        Assert.Equal('\a', CTokenizer.ParseCharLiteral("'\\a'"));
        Assert.Equal('\b', CTokenizer.ParseCharLiteral("'\\b'"));
        Assert.Equal('\f', CTokenizer.ParseCharLiteral("'\\f'"));
        Assert.Equal('\v', CTokenizer.ParseCharLiteral("'\\v'"));
        Assert.Equal('?', CTokenizer.ParseCharLiteral("'\\?'"));
        Assert.Equal('q', CTokenizer.ParseCharLiteral("'\\q'"));
        Assert.Equal('x', CTokenizer.ParseCharLiteral("'\\x'"));
        Assert.Equal('A', CTokenizer.ParseCharLiteral("'\\u0041'"));
        Assert.Equal(0x1F600, CTokenizer.ParseCharLiteral("'\\U0001F600'"));
        Assert.Equal(0, CTokenizer.ParseCharLiteral("''"));
        Assert.Equal(0, CTokenizer.ParseCharLiteral("'"));
    }

    private static List<CToken> Tokenize(string source)
    {
        var tokenizer = new CTokenizer(source, "test.c");
        return tokenizer.Tokenize();
    }
}
