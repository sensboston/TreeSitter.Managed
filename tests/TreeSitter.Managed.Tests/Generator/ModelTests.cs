using TreeSitter.Abi;
using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Model;
using Xunit;

namespace TreeSitter.Tests.Generator;

// Contract tests for ConstEvaluator, MacroExpander, InitializerFlattener and
// GrammarModelBuilder (task M1a-T4b).
public class ModelTests
{
    // A snippet becomes a full translation unit: CParser needs the language function.
    private const string LanguageFunction =
        "\nTS_PUBLIC const TSLanguage *tree_sitter_t(void) { static const TSLanguage language = { .abi_version = 15 }; return &language; }\n";

    private static CTranslationUnit Unit(string snippet)
    {
        string text = snippet + LanguageFunction;
        return new CParser(new CTokenizer(text, "t.c").Tokenize(), "t.c").ParseTranslationUnit();
    }

    private static CExpr Expr(string text) => new CParser(new CTokenizer(text, "e.c").Tokenize(), "e.c").ParseExpression();

    private static CInitializer Init(string text) => new CParser(new CTokenizer(text, "i.c").Tokenize(), "i.c").ParseInitializer();

    private static long[] Ints(CArrayValue array) => array.Elements.Select(e => ((CIntValue)e).Value).ToArray();

    // ------------------------------------------------------------ ConstEvaluator

    [Fact]
    public void EnumMembersCountFromZeroOrLastValue()
    {
        var evaluator = new ConstEvaluator(Unit("enum e { A, B, C = 10, D };"));
        Assert.Equal(0, evaluator.EvaluateInt(Expr("A")));
        Assert.Equal(1, evaluator.EvaluateInt(Expr("B")));
        Assert.Equal(10, evaluator.EvaluateInt(Expr("C")));
        Assert.Equal(11, evaluator.EvaluateInt(Expr("D")));
    }

    [Fact]
    public void DefinesAndParserHNames()
    {
        var evaluator = new ConstEvaluator(Unit("#define LARGE_STATE_COUNT 7\n#define TWICE LARGE_STATE_COUNT\n"));
        Assert.Equal(7, evaluator.DefineValue("TWICE"));
        Assert.Equal(3, evaluator.EvaluateInt(Expr("SMALL_STATE(10)")));
        Assert.Equal(5, evaluator.EvaluateInt(Expr("STATE(5)")));
        Assert.Equal(9, evaluator.EvaluateInt(Expr("ACTIONS(9)")));
        Assert.Equal(65535, evaluator.EvaluateInt(Expr("ts_builtin_sym_error")));
        Assert.Equal(0, evaluator.EvaluateInt(Expr("ts_builtin_sym_end")));
        Assert.Equal(3, evaluator.EvaluateInt(Expr("TSParseActionTypeRecover")));
        Assert.Equal(1, evaluator.EvaluateInt(Expr("true")));
        Assert.Equal(0, evaluator.EvaluateInt(Expr("false")));
        Assert.Same(CNullValue.Instance, evaluator.Evaluate(Expr("NULL")));
    }

    [Fact]
    public void CastsConvertLikeC()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Equal(65535, evaluator.EvaluateInt(Expr("(TSSymbol)-1")));
        Assert.Equal(255, evaluator.EvaluateInt(Expr("(uint8_t)-1")));
        Assert.Equal(-1, evaluator.EvaluateInt(Expr("(int16_t)65535")));
        Assert.Equal(-5, evaluator.EvaluateInt(Expr("-5")));
        Assert.Equal('a', evaluator.EvaluateInt(Expr("'a'")));
    }

    [Fact]
    public void ReferencesToVariablesAndFunctions()
    {
        var evaluator = new ConstEvaluator(Unit(
            "static const uint16_t t[2][2] = {{1, 2}, {3, 4}};\n" +
            "static bool f(TSLexer *lexer, TSStateId state) { return false; }\n"));
        var address = Assert.IsType<CReferenceValue>(evaluator.Evaluate(Expr("&t[0][0]")));
        Assert.Equal("t", address.Name);
        Assert.Equal(new long[] { 0, 0 }, address.Indices);
        var cast = Assert.IsType<CReferenceValue>(evaluator.Evaluate(Expr("(const void*)t")));
        Assert.Equal("t", cast.Name);
        Assert.Empty(cast.Indices);
        Assert.Equal("f", Assert.IsType<CReferenceValue>(evaluator.Evaluate(Expr("f"))).Name);
    }

    [Theory]
    [InlineData("unknown_name")]
    [InlineData("SHIFT(3)")]
    [InlineData("STATE(1, 2)")]
    public void ErrorsHavePositions(string text)
    {
        var evaluator = new ConstEvaluator(Unit(""));
        var error = Assert.Throws<CSourceException>(() => evaluator.Evaluate(Expr(text)));
        Assert.Equal(new SourcePos(1, 1), error.Pos);
    }

    // ------------------------------------------------------------ MacroExpander

    [Fact]
    public void ExpandsReduceToUnionInitializer()
    {
        var call = (CCall)Expr("REDUCE(sym_x, 2, -1, 7)");
        var outer = Assert.IsType<CInitList>(MacroExpander.ExpandInitializer(call, "e.c"));
        // {{ .reduce = { ... }, }}: outer list -> one item (the action union) -> .reduce
        var action = Assert.IsType<CInitList>(Assert.Single(outer.Items).Value);
        var reduce = Assert.Single(action.Items);
        Assert.Equal("reduce", Assert.IsType<CFieldDesignator>(Assert.Single(reduce.Designators)).Name);
    }

    // ------------------------------------------------------------ InitializerFlattener

    [Fact]
    public void DesignatorThenPositionalContinuationAndZeroFill()
    {
        var unit = Unit("static const uint16_t a[5] = { [2] = 7, 8 };");
        var array = new InitializerFlattener(new ConstEvaluator(unit)).FlattenVariable(unit.Variables[0]);
        Assert.Equal(new long[] { 0, 0, 7, 8, 0 }, Ints(array));
        Assert.Equal(new[] { 5 }, array.Dimensions);
    }

    [Fact]
    public void UnknownSizeIsMaxIndexPlusOne()
    {
        var unit = Unit("static const uint16_t a[] = { [3] = 1, [1] = 2 };");
        var array = new InitializerFlattener(new ConstEvaluator(unit)).FlattenVariable(unit.Variables[0]);
        Assert.Equal(new long[] { 0, 2, 0, 1 }, Ints(array));
    }

    [Fact]
    public void TwoDimensionalRowMajor()
    {
        var unit = Unit("#define ROWS 2\nstatic const uint16_t t[ROWS][3] = { [STATE(1)] = { [2] = 5, }, [0] = { 1 } };");
        var array = new InitializerFlattener(new ConstEvaluator(unit)).FlattenVariable(unit.Variables[0]);
        Assert.Equal(new[] { 2, 3 }, array.Dimensions);
        Assert.Equal(new long[] { 1, 0, 0, 0, 0, 5 }, Ints(array));
    }

    [Fact]
    public void StructMembersByDesignatorAndZeroFill()
    {
        var flattener = new InitializerFlattener(new ConstEvaluator(Unit("")));
        var value = Assert.IsType<CAggregateValue>(flattener.FlattenValue(Init("{ .named = true }"), ParserHTypes.TSSymbolMetadata));
        Assert.Equal(0, ((CIntValue)value.Members["visible"]).Value);
        Assert.Equal(1, ((CIntValue)value.Members["named"]).Value);
        Assert.Equal(0, ((CIntValue)value.Members["supertype"]).Value);
        Assert.Null(value.ActiveMember);
    }

    [Fact]
    public void ParseActionEntriesFromDesignatorsAndMacros()
    {
        var unit = Unit("static const TSParseActionEntry a[] = { [0] = {.entry = {.count = 2, .reusable = true}}, SHIFT(4), REDUCE(3, 2, -1, 7), };");
        var array = new InitializerFlattener(new ConstEvaluator(unit)).FlattenVariable(unit.Variables[0]);
        Assert.Equal(3, array.Elements.Count);

        var header = Assert.IsType<CAggregateValue>(array.Elements[0]);
        Assert.Equal("entry", header.ActiveMember);
        var entry = (CAggregateValue)header.Members["entry"];
        Assert.Equal(2, ((CIntValue)entry.Members["count"]).Value);

        var shift = Assert.IsType<CAggregateValue>(array.Elements[1]);
        Assert.Equal("action", shift.ActiveMember);
        var shiftAction = (CAggregateValue)shift.Members["action"];
        Assert.Equal("shift", shiftAction.ActiveMember);
        var shiftStruct = (CAggregateValue)shiftAction.Members["shift"];
        Assert.Equal(4, ((CIntValue)shiftStruct.Members["state"]).Value);
        Assert.Equal(0, ((CIntValue)shiftStruct.Members["extra"]).Value);

        var reduce = (CAggregateValue)((CAggregateValue)((CAggregateValue)array.Elements[2]).Members["action"]).Members["reduce"];
        Assert.Equal(-1, ((CIntValue)reduce.Members["dynamic_precedence"]).Value);
        Assert.Equal(7, ((CIntValue)reduce.Members["production_id"]).Value);
    }

    [Fact]
    public void StringArrayWithNull()
    {
        var unit = Unit("static const char * const n[] = { [0] = \"end\", [2] = \"x\\\"y\" };");
        var array = new InitializerFlattener(new ConstEvaluator(unit)).FlattenVariable(unit.Variables[0]);
        Assert.Equal("end"u8.ToArray(), ((CStringValue)array.Elements[0]).Bytes);
        Assert.Same(CNullValue.Instance, array.Elements[1]);
        Assert.Equal("x\"y"u8.ToArray(), ((CStringValue)array.Elements[2]).Bytes);
    }

    [Theory]
    [InlineData("static const uint16_t a[2] = { [2] = 1 };")]
    [InlineData("static const uint16_t a[2] = { [0] = 1, [0] = 2 };")]
    [InlineData("static const TSSymbolMetadata a[1] = { [0] = { .no_such_member = 1 } };")]
    public void FlattenerErrors(string snippet)
    {
        var unit = Unit(snippet);
        Assert.Throws<CSourceException>(() => new InitializerFlattener(new ConstEvaluator(unit)).FlattenVariable(unit.Variables[0]));
    }

    // ------------------------------------------------------------ GrammarModelBuilder

    [Theory]
    [InlineData("Json", "json", 32u, 7u, 25u, 0u, 15u, 0u, 2u, 2u, 1u, 0, 24, 8)]
    [InlineData("C", "c", 2015u, 455u, 360u, 3u, 161u, 0u, 39u, 131u, 7u, 0, 24, 2)]
    [InlineData("Python", "python", 2788u, 185u, 273u, 2u, 108u, 12u, 32u, 150u, 5u, 0, 25, 0)]
    public void BuildsScalarsFromLanguageInitializer(
        string grammar, string name, uint states, uint largeStates, uint symbols, uint aliases, uint tokens,
        uint externalTokens, uint fields, uint productions, uint supertypes, int major, int minor, int patch)
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC(grammar));
        LanguageTables t = model.Tables;
        Assert.Equal(15u, t.AbiVersion);
        Assert.Equal(name, t.Name);
        Assert.Equal(states, t.StateCount);
        Assert.Equal(largeStates, t.LargeStateCount);
        Assert.Equal(symbols, t.SymbolCount);
        Assert.Equal(aliases, t.AliasCount);
        Assert.Equal(tokens, t.TokenCount);
        Assert.Equal(externalTokens, t.ExternalTokenCount);
        Assert.Equal(fields, t.FieldCount);
        Assert.Equal(productions, t.ProductionIdCount);
        Assert.Equal(supertypes, t.SupertypeCount);
        Assert.Equal(new LanguageMetadata((byte)major, (byte)minor, (byte)patch), t.Metadata);
        Assert.Equal((int)(largeStates * symbols), t.ParseTable!.Length);
        Assert.Equal((int)states, t.LexModes!.Length);
        Assert.Equal((int)(states - largeStates), t.SmallParseTableMap!.Length);
        Assert.NotNull(model.LexFunction);
        Assert.NotNull(t.LexFn);
    }

    [Fact]
    public void JsonHasNoKeywordLexerExternalScannerOrReservedWords()
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("Json"));
        Assert.Null(model.KeywordLexFunction);
        Assert.Null(model.Tables.KeywordLexFn);
        Assert.Null(model.Tables.ExternalScannerStates);
        Assert.Null(model.Tables.ReservedWords);
        Assert.Empty(model.CharacterSets);
        Assert.False(model.Tables.ExternalScannerScanIsSet);
    }

    [Fact]
    public void CHasKeywordLexerAndCharacterSets()
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("C"));
        Assert.Equal("ts_lex_keywords", model.KeywordLexFunction!.Name);
        Assert.Equal(1, model.Tables.KeywordCaptureToken); // sym_identifier = 1
        Assert.NotEmpty(model.CharacterSets);
    }

    [Fact]
    public void StringTablesStopAtTheFirstNulByte()
    {
        // tree-sitter-go: [anon_sym_NULL] = "\0", which the runtime reads as "".
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("Go"));
        Assert.Equal(string.Empty, model.Tables.SymbolNames![4]);
    }

    [Fact]
    public void PythonHasExternalScannerAndReservedWords()
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("Python"));
        LanguageTables t = model.Tables;
        Assert.Equal(35, t.MaxReservedWordSetSize);
        Assert.NotNull(t.ReservedWords);
        Assert.Equal(19 * 12, t.ExternalScannerStates!.Length); // ts_external_scanner_states[19][EXTERNAL_TOKEN_COUNT]
        Assert.Equal(12, t.ExternalScannerSymbolMap!.Length);
        Assert.True(t.ExternalScannerScanIsSet);
        Assert.Equal("tree_sitter_python_external_scanner_scan", model.ExternalScannerFunctions["scan"]);
    }

    [Fact]
    public void ParseActionsAreConvertedByActiveMember()
    {
        // JSON: [0] = {.entry = {.count = 0, .reusable = false}}, [1] = {.entry = {.count = 1, .reusable = false}}, RECOVER(),
        //       [3] = {.entry = {.count = 1, .reusable = true}}, SHIFT_EXTRA(),
        ParseActionEntry[] actions = GrammarModelBuilder.Build(CParserTests.GrammarParserC("Json")).Tables.ParseActions!;
        Assert.Equal(0, actions[0].EntryCount);
        Assert.Equal(1, actions[1].EntryCount);
        Assert.False(actions[1].EntryReusable);
        Assert.Equal(ParseActionType.Recover, actions[2].Type);
        Assert.True(actions[3].EntryReusable);
        Assert.Equal(ParseActionType.Shift, actions[4].Type);
        Assert.True(actions[4].ShiftExtra);
    }
}
