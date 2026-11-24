using TreeSitter.Generator.CSource;
using Xunit;

namespace TreeSitter.Tests.Generator;

// Contract tests for CParser (task M1a-T4a). The expected numbers were counted in the
// regenerated grammars/*/upstream/src/parser.c files with grep.
public class CParserTests
{
    [Theory]
    [InlineData("Json", 13, 2, 17, 1, 0, "tree_sitter_json")]
    [InlineData("C", 13, 2, 20, 2, 0, "tree_sitter_c")]
    [InlineData("Python", 13, 3, 22, 2, 5, "tree_sitter_python")]
    public void ParsesGrammarFiles(string grammar, int defines, int enums, int variables, int functions, int prototypes, string languageFunction)
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC(grammar));

        // 15 #define lines in each file; 2 of them (TS_PUBLIC) are in false #ifdef/#elif branches.
        Assert.Equal(defines, unit.Defines.Count);
        Assert.Equal(enums, unit.Enums.Count);
        Assert.Equal(variables, unit.Variables.Count);
        Assert.Equal(functions, unit.Functions.Count);
        Assert.Equal(prototypes, unit.Prototypes.Count);
        Assert.Equal(languageFunction, unit.Language.FunctionName);
        Assert.Equal("TSLanguage", unit.Language.Language.ElementType);
        Assert.Equal("language", unit.Language.Language.Name);
    }

    [Fact]
    public void ActiveTsPublicDefineIsTheElseBranch()
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC("Json"));
        CDefine tsPublic = Assert.Single(unit.Defines, d => d.Name == "TS_PUBLIC");
        // #else branch: #define TS_PUBLIC __attribute__((visibility("default")))
        Assert.Equal("__attribute__", tsPublic.Body[0].Text);
    }

    [Fact]
    public void ReadsDefineBody()
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC("Json"));
        CDefine version = Assert.Single(unit.Defines, d => d.Name == "LANGUAGE_VERSION");
        CToken value = Assert.Single(version.Body);
        Assert.Equal("15", value.Text);
    }

    [Fact]
    public void ReadsParseActionsDeclaration()
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC("Json"));
        CVariable actions = Assert.Single(unit.Variables, v => v.Name == "ts_parse_actions");
        Assert.Equal("TSParseActionEntry", actions.ElementType);
        Assert.Single(actions.Dimensions);
        Assert.Null(actions.Dimensions[0]);

        var list = Assert.IsType<CInitList>(actions.Initializer);
        // [0] = {.entry = {.count = 0, .reusable = false}},
        CInitItem first = list.Items[0];
        var index = Assert.IsType<CIndexDesignator>(Assert.Single(first.Designators));
        Assert.Equal(0, Assert.IsType<CIntLiteral>(index.Index).Value);
        var entry = Assert.IsType<CInitList>(first.Value);
        var entryField = Assert.IsType<CFieldDesignator>(Assert.Single(Assert.Single(entry.Items).Designators));
        Assert.Equal("entry", entryField.Name);
        // [1] = {.entry = {.count = 1, .reusable = false}}, RECOVER(),
        CInitItem recover = list.Items[2];
        Assert.Empty(recover.Designators);
        var call = Assert.IsType<CCall>(Assert.IsType<CInitExpr>(recover.Value).Expr);
        Assert.Equal("RECOVER", call.Name);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void ReadsTwoDimensionalParseTable()
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC("Json"));
        CVariable table = Assert.Single(unit.Variables, v => v.Name == "ts_parse_table");
        Assert.Equal("uint16_t", table.ElementType);
        Assert.Equal(2, table.Dimensions.Count);
        Assert.Equal("LARGE_STATE_COUNT", Assert.IsType<CIdentifier>(table.Dimensions[0]).Name);
        Assert.Equal("SYMBOL_COUNT", Assert.IsType<CIdentifier>(table.Dimensions[1]).Name);
        // [STATE(0)] = { [ts_builtin_sym_end] = ACTIONS(1), ...
        var list = Assert.IsType<CInitList>(table.Initializer);
        var stateIndex = Assert.IsType<CIndexDesignator>(Assert.Single(list.Items[0].Designators));
        Assert.Equal("STATE", Assert.IsType<CCall>(stateIndex.Index).Name);
    }

    [Fact]
    public void ReadsStringArrayType()
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC("Json"));
        CVariable names = Assert.Single(unit.Variables, v => v.Name == "ts_symbol_names");
        Assert.Equal("char * const", names.ElementType);
    }

    [Fact]
    public void ReadsLexFunctionsAsTokens()
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC("C"));
        CFunction lex = Assert.Single(unit.Functions, f => f.Name == "ts_lex");
        Assert.Equal("bool", lex.ReturnType);
        Assert.Equal("TSLexer * lexer , TSStateId state", lex.Parameters);
        Assert.Equal("START_LEXER", lex.Body[0].Text);
        Assert.Contains(unit.Functions, f => f.Name == "ts_lex_keywords");
    }

    [Fact]
    public void ReadsExternalScannerPrototypesAndStates()
    {
        CTranslationUnit unit = CSourceFile.Parse(GrammarParserC("Python"));
        Assert.Contains(unit.Prototypes, p => p.Name == "tree_sitter_python_external_scanner_scan");
        CVariable states = Assert.Single(unit.Variables, v => v.Name == "ts_external_scanner_states");
        Assert.Equal("bool", states.ElementType);
        Assert.Equal(2, states.Dimensions.Count);
    }

    [Theory]
    [InlineData("(TSSymbol)-1")]
    [InlineData("( TSSymbol ) - 1")]
    public void ParsesCastOfNegativeNumber(string source)
    {
        var cast = Assert.IsType<CCast>(ParseExpression(source));
        Assert.Equal("TSSymbol", cast.TypeName);
        var minus = Assert.IsType<CUnary>(cast.Operand);
        Assert.Equal('-', minus.Operator);
        Assert.Equal(1, Assert.IsType<CIntLiteral>(minus.Operand).Value);
    }

    [Fact]
    public void ParenthesizedNameIsNotACast()
    {
        // Body of SMALL_STATE in parser.h.
        var minus = Assert.IsType<CBinary>(ParseExpression("((id) - LARGE_STATE_COUNT)"));
        Assert.Equal('-', minus.Operator);
        Assert.Equal("id", Assert.IsType<CIdentifier>(minus.Left).Name);
        Assert.Equal("LARGE_STATE_COUNT", Assert.IsType<CIdentifier>(minus.Right).Name);
    }

    [Fact]
    public void AdditiveIsLeftAssociative()
    {
        var outer = Assert.IsType<CBinary>(ParseExpression("1 - 2 + 3"));
        Assert.Equal('+', outer.Operator);
        var inner = Assert.IsType<CBinary>(outer.Left);
        Assert.Equal('-', inner.Operator);
        Assert.Equal(3, Assert.IsType<CIntLiteral>(outer.Right).Value);
    }

    [Fact]
    public void ParsesPointerCast()
    {
        var cast = Assert.IsType<CCast>(ParseExpression("(const void*)ts_lex_modes"));
        // Type text is the tokens joined with single spaces.
        Assert.Equal("const void *", cast.TypeName);
        Assert.Equal("ts_lex_modes", Assert.IsType<CIdentifier>(cast.Operand).Name);
    }

    [Fact]
    public void ParsesAddressOfNestedIndex()
    {
        var address = Assert.IsType<CUnary>(ParseExpression("&ts_parse_table[0][0]"));
        Assert.Equal('&', address.Operator);
        var outer = Assert.IsType<CIndex>(address.Operand);
        var inner = Assert.IsType<CIndex>(outer.Target);
        Assert.Equal("ts_parse_table", Assert.IsType<CIdentifier>(inner.Target).Name);
        Assert.Equal(0, Assert.IsType<CIntLiteral>(inner.Index).Value);
        Assert.Equal(0, Assert.IsType<CIntLiteral>(outer.Index).Value);
    }

    [Fact]
    public void ParsesMacroCallWithArguments()
    {
        var call = Assert.IsType<CCall>(ParseExpression("REDUCE(sym_document, 1, 0, 0)"));
        Assert.Equal("REDUCE", call.Name);
        Assert.Equal(4, call.Arguments.Count);
        Assert.Equal("sym_document", Assert.IsType<CIdentifier>(call.Arguments[0]).Name);
    }

    [Fact]
    public void ParsesCharAndStringLiterals()
    {
        Assert.Equal('\\', Assert.IsType<CCharLiteral>(ParseExpression("'\\\\'")).Value);
        byte[] bytes = Assert.IsType<CStringLiteral>(ParseExpression("\"a\\\"b\\x7f\"")).Bytes;
        Assert.Equal(new byte[] { (byte)'a', (byte)'"', (byte)'b', 0x7f }, bytes);
    }

    [Fact]
    public void ParsesNestedDesignatedInitializer()
    {
        var list = Assert.IsType<CInitList>(ParseInitializer("{[3] = {.count = 1, .reusable = true}, SHIFT(16), }"));
        Assert.Equal(2, list.Items.Count);
        var index = Assert.IsType<CIndexDesignator>(Assert.Single(list.Items[0].Designators));
        Assert.Equal(3, Assert.IsType<CIntLiteral>(index.Index).Value);
        var inner = Assert.IsType<CInitList>(list.Items[0].Value);
        Assert.Equal("count", Assert.IsType<CFieldDesignator>(Assert.Single(inner.Items[0].Designators)).Name);
        Assert.Equal("true", Assert.IsType<CIdentifier>(Assert.IsType<CInitExpr>(inner.Items[1].Value).Expr).Name);
    }

    [Theory]
    [InlineData("int x;\n", 1, 1)]
    [InlineData("enum e { A, B };\nstatic const int t[] = {1, 2;\n", 2, 29)]
    [InlineData("#define F(x) x\n", 1, 1)]
    [InlineData("#if FOO > 1\n#endif\n", 1, 1)]
    [InlineData("static const int t[] = {1, 2}\n", 2, 1)]
    public void ReportsUnknownOrBrokenConstructsWithPosition(string source, int line, int column)
    {
        var tokens = new CTokenizer(source, "broken.c").Tokenize();
        var error = Assert.Throws<CSourceException>(() => new CParser(tokens, "broken.c").ParseTranslationUnit());
        Assert.Equal("broken.c", error.FileName);
        Assert.Equal(new SourcePos(line, column), error.Pos);
        Assert.StartsWith($"broken.c:{line}:{column}: ", error.Message, StringComparison.Ordinal);
    }

    private static CExpr ParseExpression(string source)
    {
        var tokens = new CTokenizer(source, "expr.c").Tokenize();
        return new CParser(tokens, "expr.c").ParseExpression();
    }

    private static CInitializer ParseInitializer(string source)
    {
        var tokens = new CTokenizer(source, "init.c").Tokenize();
        return new CParser(tokens, "init.c").ParseInitializer();
    }

    internal static string GrammarParserC(string grammar)
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "TreeSitter.Managed.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repository root not found");
        }
        return Path.Combine(dir, "grammars", grammar, "upstream", "src", "parser.c");
    }
}
