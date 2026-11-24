using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Lex;
using Xunit;

namespace TreeSitter.Tests.Generator;

// Contract tests for LexBodyParser (task M1b-B1). Numbers counted in the regenerated parser.c files.
public class LexBodyParserTests
{
    private static LexProgram Load(string grammar, string function)
    {
        CTranslationUnit unit = CSourceFile.Parse(CParserTests.GrammarParserC(grammar));
        return LexBodyParser.Parse(unit.Functions.Single(f => f.Name == function), unit.FileName);
    }

    [Theory]
    [InlineData("Json", "ts_lex", 44, 23, 3, 58)]
    [InlineData("C", "ts_lex", 324, 203, 28, 627)]
    [InlineData("C", "ts_lex_keywords", 512, 81, 1, 526)]
    [InlineData("Python", "ts_lex", 150, 96, 4, 496)]
    [InlineData("Python", "ts_lex_keywords", 162, 42, 0, 163)]
    public void ParsesAllLexFunctions(string grammar, string function, int states, int accepts, int advanceMaps, int ifs)
    {
        LexProgram program = Load(grammar, function);
        Assert.Equal(function, program.FunctionName);
        Assert.Equal(states, program.States.Count);
        Assert.Equal(Enumerable.Range(0, states), program.States.Select(s => s.Number));
        LexStatement[] all = program.States.SelectMany(s => s.Statements).ToArray();
        Assert.Equal(accepts, all.OfType<LexAccept>().Count());
        Assert.Equal(advanceMaps, all.OfType<LexAdvanceMap>().Count());
        Assert.Equal(ifs, all.OfType<LexIf>().Count());
    }

    [Fact]
    public void JsonState0()
    {
        // case 0:
        //   if (eof) ADVANCE(21);
        //   ADVANCE_MAP('"', 28, ',', 23, '-', 6, '/', 3, '0', 35, ':', 25, '[', 26, '\\', 18,
        //               ']', 27, 'f', 7, 'n', 16, 't', 13, '{', 22, '}', 24, );
        //   if (('\t' <= lookahead && lookahead <= '\r') || lookahead == ' ') SKIP(20);
        //   if (('1' <= lookahead && lookahead <= '9')) ADVANCE(36);
        //   END_STATE();
        LexState state = Load("Json", "ts_lex").States[0];
        Assert.Equal(4, state.Statements.Count);

        var first = Assert.IsType<LexIf>(state.Statements[0]);
        Assert.IsType<LexEof>(first.Condition);
        Assert.Equal(21, first.Target);
        Assert.False(first.Skip);

        var map = Assert.IsType<LexAdvanceMap>(state.Statements[1]);
        Assert.Equal(14, map.Keys.Count);
        Assert.Equal('"', map.Keys[0]);
        Assert.Equal(28, map.Targets[0]);
        Assert.Equal('\\', map.Keys[7]);
        Assert.Equal('}', map.Keys[13]);
        Assert.Equal(24, map.Targets[13]);

        var skip = Assert.IsType<LexIf>(state.Statements[2]);
        Assert.True(skip.Skip);
        Assert.Equal(20, skip.Target);
        var or = Assert.IsType<LexLogical>(skip.Condition);
        Assert.False(or.IsAnd);
        var range = Assert.IsType<LexLogical>(or.Left);
        Assert.True(range.IsAnd);
        var low = Assert.IsType<LexCompare>(range.Left);
        Assert.Equal("<=", low.Operator);
        Assert.Equal('\t', Assert.IsType<LexConstant>(low.Left).Value);
        Assert.IsType<LexLookahead>(low.Right);
        var space = Assert.IsType<LexCompare>(or.Right);
        Assert.Equal("==", space.Operator);
        Assert.Equal(' ', Assert.IsType<LexConstant>(space.Right).Value);
    }

    [Fact]
    public void AcceptAndNotEof()
    {
        // Python has "if ((!eof && lookahead == 0)) ADVANCE(N);" in ts_lex.
        LexProgram program = Load("Python", "ts_lex");
        LexStatement[] all = program.States.SelectMany(s => s.Statements).ToArray();
        LexIf notEof = all.OfType<LexIf>()
            .First(i => i.Condition is LexLogical { IsAnd: true, Left: LexNot { Operand: LexEof } });
        var compare = Assert.IsType<LexCompare>(((LexLogical)notEof.Condition).Right);
        Assert.Equal(0, Assert.IsType<LexConstant>(compare.Right).Value);
        Assert.Contains(all.OfType<LexAccept>(), a => a.Symbol == "sym_identifier");
    }

    [Fact]
    public void SetContains()
    {
        LexIf set = Load("C", "ts_lex").States.SelectMany(s => s.Statements).OfType<LexIf>()
            .First(i => i.Condition is LexSetContains);
        var contains = (LexSetContains)set.Condition;
        Assert.StartsWith("sym_identifier_character_set_", contains.SetName, StringComparison.Ordinal);
        Assert.True(contains.Length > 0);
    }

    [Theory]
    [InlineData("START_LEXER(); eof = lexer->eof(lexer); switch (state) { case 0: if (lookahead == 97) GOTO(1); END_STATE(); default: return false; }")]
    [InlineData("START_LEXER(); eof = lexer->eof(lexer); switch (state) { case 0: if (lookahead + 1) ADVANCE(1); END_STATE(); default: return false; }")]
    [InlineData("START_LEXER(); eof = lexer->eof(lexer); switch (state) { case 0: ADVANCE(1); END_STATE(); default: return false; }")]
    [InlineData("START_LEXER(); eof = lexer->eof(lexer); switch (state) { case 0: if (lookahead == 97) ADVANCE(1); default: return false; }")]
    [InlineData("START_LEXER(); switch (state) { case 0: END_STATE(); default: return false; }")]
    public void RejectsUnknownForms(string body)
    {
        string text = "static bool ts_lex(TSLexer *lexer, TSStateId state) {\n" + body + "\n}\n" +
            "TS_PUBLIC const TSLanguage *tree_sitter_t(void) { static const TSLanguage language = { .abi_version = 15 }; return &language; }\n";
        CTranslationUnit unit = new CParser(new CTokenizer(text, "t.c").Tokenize(), "t.c").ParseTranslationUnit();
        var error = Assert.Throws<CSourceException>(() => LexBodyParser.Parse(unit.Functions[0], "t.c"));
        Assert.Equal(2, error.Pos.Line);
    }
}
