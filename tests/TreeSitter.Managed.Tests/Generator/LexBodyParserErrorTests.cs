using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Lex;
using Xunit;

namespace TreeSitter.Tests.Generator;

// Error-path tests for LexBodyParser (task M1b-B1): every rejected form must surface as a
// CSourceException carrying the position of the offending token. The well-formed forms are
// covered by LexBodyParserTests.
public class LexBodyParserErrorTests
{
    private const string Head = "START_LEXER(); eof = lexer->eof(lexer); switch (state) { ";
    private const string Tail = " default: return false; }";

    private static CFunction Function(string body)
    {
        string text = "static bool ts_lex(TSLexer *lexer, TSStateId state) {\n" + body + "\n}\n" +
            "TS_PUBLIC const TSLanguage *tree_sitter_t(void) { static const TSLanguage language = { .abi_version = 15 }; return &language; }\n";
        CTranslationUnit unit = new CParser(new CTokenizer(text, "t.c").Tokenize(), "t.c").ParseTranslationUnit();
        return unit.Functions[0];
    }

    private static CSourceException Error(string body) =>
        Assert.Throws<CSourceException>(() => LexBodyParser.Parse(Function(body), "t.c"));

    [Fact]
    public void EmptyBodyIsRejectedAtFunctionStart()
    {
        CSourceException error = Error(string.Empty);
        Assert.Equal(new SourcePos(1, 1), error.Pos);
        Assert.Equal("t.c", error.FileName);
        Assert.Contains("START_LEXER", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchRequiresCaseOrDefault()
    {
        CSourceException error = Error(Head + "case 0: END_STATE(); }");
        Assert.Equal(2, error.Pos.Line);
        Assert.Equal("Expected 'case' or 'default' in the switch.", error.Text);
    }

    [Fact]
    public void CaseNumbersMustBeConsecutive()
    {
        CSourceException error = Error(Head + "case 0: END_STATE(); case 2: END_STATE();" + Tail);
        Assert.Equal(2, error.Pos.Line);
        Assert.Equal("Expected case 1, found case 2.", error.Text);
    }

    [Fact]
    public void FirstCaseMustBeZero()
    {
        CSourceException error = Error(Head + "case 1: END_STATE();" + Tail);
        Assert.Equal("Expected case 0, found case 1.", error.Text);
    }

    [Fact]
    public void CaseLabelMustBeNumber()
    {
        CSourceException error = Error(Head + "case x: END_STATE();" + Tail);
        Assert.Equal("Expected a number.", error.Text);
    }

    [Fact]
    public void ConditionRejectsUnknownIdentifier()
    {
        CSourceException error = Error(Head + "case 0: if (foo) ADVANCE(1); END_STATE();" + Tail);
        Assert.Equal(2, error.Pos.Line);
        Assert.Equal("Expected a condition.", error.Text);
    }

    [Fact]
    public void ConditionRequiresClosingParenthesis()
    {
        CSourceException error = Error(Head + "case 0: if (lookahead == 'a' ADVANCE(1); END_STATE();" + Tail);
        Assert.Equal("Expected ')'.", error.Text);
    }

    [Fact]
    public void SetContainsNeedsANameIdentifier()
    {
        CSourceException error = Error(Head + "case 0: if (set_contains(1, 2, lookahead)) ADVANCE(1); END_STATE();" + Tail);
        Assert.Equal("Expected a set name identifier.", error.Text);
    }

    [Fact]
    public void SetContainsThirdArgumentMustBeLookahead()
    {
        CSourceException error = Error(Head + "case 0: if (set_contains(sym_a, 2, eof)) ADVANCE(1); END_STATE();" + Tail);
        Assert.Equal("Expected 'lookahead'.", error.Text);
    }

    [Fact]
    public void AdvanceMapRequiresCommaBetweenKeyAndTarget()
    {
        CSourceException error = Error(Head + "case 0: ADVANCE_MAP('a' 1); END_STATE();" + Tail);
        Assert.Equal("Expected ','.", error.Text);
    }

    [Fact]
    public void AdvanceMapKeyMustBeLiteral()
    {
        CSourceException error = Error(Head + "case 0: ADVANCE_MAP(a, 1); END_STATE();" + Tail);
        Assert.Equal("Expected a character or number key.", error.Text);
    }

    [Fact]
    public void AdvanceMapTargetMustBeNumber()
    {
        CSourceException error = Error(Head + "case 0: ADVANCE_MAP('a', b); END_STATE();" + Tail);
        Assert.Equal("Expected a number.", error.Text);
    }

    [Fact]
    public void AcceptTokenNeedsASymbolIdentifier()
    {
        CSourceException error = Error(Head + "case 0: ACCEPT_TOKEN(); END_STATE();" + Tail);
        Assert.Equal("Expected a symbol name identifier.", error.Text);
    }

    [Fact]
    public void CaseBodyMustEndWithEndState()
    {
        // CParser only hands over brace-balanced bodies, so the reachable truncation of a
        // case is a body that runs into the switch's closing brace without END_STATE().
        CSourceException error = Error(Head + "case 0: }");
        Assert.Equal(2, error.Pos.Line);
        Assert.Contains("Unsupported lex statement", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchCloseMustFollowDefault()
    {
        CSourceException error = Error(Head + "default: return false; x }");
        Assert.Equal(2, error.Pos.Line);
        Assert.Equal("Expected '}'.", error.Text);
    }

    [Fact]
    public void TrailingTokenAfterSwitchIsRejected()
    {
        CSourceException error = Error(Head + "default: return false; } extra");
        Assert.Equal("Unexpected token after the switch.", error.Text);
    }

    [Fact]
    public void IntegerOutOfRangeIsRejected()
    {
        CSourceException error = Error(Head + "case 0: if (lookahead == 5000000000) ADVANCE(1); END_STATE();" + Tail);
        Assert.Equal("Integer literal '5000000000' is out of range.", error.Text);
    }

    [Fact]
    public void MalformedHexLiteralIsRejected()
    {
        CSourceException error = Error(Head + "case 0: if (lookahead == 0x) ADVANCE(1); END_STATE();" + Tail);
        Assert.Equal("Invalid integer literal '0x'.", error.Text);
    }

    [Fact]
    public void IntegerOverflowIsRejected()
    {
        CSourceException error = Error(Head + "case 0: if (lookahead == 99999999999999999999) ADVANCE(1); END_STATE();" + Tail);
        Assert.Equal("Invalid integer literal '99999999999999999999'.", error.Text);
    }
}
