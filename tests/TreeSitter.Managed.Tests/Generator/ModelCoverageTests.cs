using TreeSitter.Abi;
using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Model;
using Xunit;

namespace TreeSitter.Tests.Generator;

// Extra tests for the M1a-T4b model: branches of ConstEvaluator, MacroExpander,
// InitializerFlattener and GrammarModelBuilder that ModelTests does not reach.
public class ModelCoverageTests
{
    private const string LanguageFunction =
        "\nTS_PUBLIC const TSLanguage *tree_sitter_t(void) { static const TSLanguage language = { .abi_version = 15 }; return &language; }\n";

    private sealed record EvilExpr(SourcePos Pos) : CExpr(Pos);

    private sealed record EvilInit(SourcePos Pos) : CInitializer(Pos);

    private sealed record EvilType(string Name) : CTypeInfo(Name);

    private static CTranslationUnit Unit(string snippet)
    {
        string text = snippet + LanguageFunction;
        return new CParser(new CTokenizer(text, "t.c").Tokenize(), "t.c").ParseTranslationUnit();
    }

    private static CTranslationUnit UnitWithLanguage(string declarations, string languageMembers)
    {
        string text = declarations +
            "\nTS_PUBLIC const TSLanguage *tree_sitter_t(void) { static const TSLanguage language = { " +
            languageMembers + " }; return &language; }\n";
        return new CParser(new CTokenizer(text, "t.c").Tokenize(), "t.c").ParseTranslationUnit();
    }

    private static CExpr Expr(string text) => new CParser(new CTokenizer(text, "e.c").Tokenize(), "e.c").ParseExpression();

    private static CInitializer Init(string text) => new CParser(new CTokenizer(text, "i.c").Tokenize(), "i.c").ParseInitializer();

    private static InitializerFlattener FlattenerOf(CTranslationUnit unit) => new(new ConstEvaluator(unit));

    private static GrammarModel BuildSnippet(string declarations, string languageMembers) =>
        GrammarModelBuilder.Build(UnitWithLanguage(declarations, languageMembers));

    private static Dictionary<string, CExpr> Param(string name, CExpr argument) =>
        new(StringComparer.Ordinal) { [name] = argument };

    // ------------------------------------------------------------ ConstEvaluator

    [Fact]
    public void EnumValuesUseExplicitExpressionsAndContinueAfterThem()
    {
        var evaluator = new ConstEvaluator(Unit("enum e { A = 2 + 3, B, C = 10, D };"));
        Assert.Equal(5, evaluator.EvaluateInt(Expr("A")));
        Assert.Equal(6, evaluator.EvaluateInt(Expr("B")));
        Assert.Equal(11, evaluator.EvaluateInt(Expr("D")));
    }

    [Fact]
    public void EnumMemberWinsOverDefine()
    {
        var evaluator = new ConstEvaluator(Unit("#define X 9\nenum e { X = 1 };\n"));
        Assert.Equal(1, evaluator.EvaluateInt(Expr("X")));
    }

    [Fact]
    public void CastsCoverEveryIntegerWidth()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Equal(4294967295L, evaluator.EvaluateInt(Expr("(uint32_t)-1")));
        Assert.Equal(-1, evaluator.EvaluateInt(Expr("(int32_t)-1")));
        Assert.Equal(5, evaluator.EvaluateInt(Expr("(int32_t)5")));
        Assert.Equal(5, evaluator.EvaluateInt(Expr("(int16_t)5")));
        Assert.Equal(7, evaluator.EvaluateInt(Expr("(TSStateId)7")));
    }

    [Fact]
    public void BinaryAdditionIsEvaluated()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Equal(3, evaluator.EvaluateInt(Expr("1 + 2")));
    }

    [Fact]
    public void IndexingAppendsOneSubscript()
    {
        var evaluator = new ConstEvaluator(Unit("static const uint16_t t[4] = { 1, 2, 3, 4 };"));
        var reference = Assert.IsType<CReferenceValue>(evaluator.Evaluate(Expr("t[2]")));
        Assert.Equal("t", reference.Name);
        Assert.Equal(new long[] { 2 }, reference.Indices);
    }

    [Fact]
    public void PrototypeNameIsAReference()
    {
        var evaluator = new ConstEvaluator(Unit("void f(void *);\n"));
        Assert.Equal("f", Assert.IsType<CReferenceValue>(evaluator.Evaluate(Expr("f"))).Name);
    }

    [Fact]
    public void UnknownExpressionKindIsAnError()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(new EvilExpr(new SourcePos(1, 1))));
    }

    [Fact]
    public void EvaluateIntRejectsNonIntegers()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<CSourceException>(() => evaluator.EvaluateInt(Expr("NULL")));
    }

    [Fact]
    public void DefineValueRejectsUnknownName()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<KeyNotFoundException>(() => evaluator.DefineValue("NO_SUCH_DEFINE"));
    }

    [Fact]
    public void UnknownCallNameIsAnError()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(Expr("g(1)")));
    }

    [Fact]
    public void AddressOfNeedsAReference()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(Expr("&5")));
    }

    [Fact]
    public void UnsupportedUnaryOperatorIsAnError()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        var unary = new CUnary(new SourcePos(1, 1), '~', new CIntLiteral(new SourcePos(1, 1), 5, "5"));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(unary));
    }

    [Fact]
    public void UnsupportedBinaryOperatorIsAnError()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        var left = new CIntLiteral(new SourcePos(1, 1), 5, "5");
        var binary = new CBinary(new SourcePos(1, 1), '*', left, left);
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(binary));
    }

    [Fact]
    public void UnsupportedCastTypeIsAnError()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(Expr("(short)5")));
    }

    [Fact]
    public void PointerCastRejectsNonReferences()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(Expr("(const void*)5")));
    }

    [Fact]
    public void IntegerCastRejectsReferences()
    {
        var evaluator = new ConstEvaluator(Unit("static const uint16_t t[1] = { 5 };"));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(Expr("(uint8_t)t")));
    }

    [Fact]
    public void IndexOnNonReferenceIsAnError()
    {
        var evaluator = new ConstEvaluator(Unit(""));
        Assert.Throws<CSourceException>(() => evaluator.Evaluate(Expr("5[0]")));
    }

    // ------------------------------------------------------------ MacroExpander

    [Fact]
    public void ExpandExpressionRejectsUnknownMacro()
    {
        var call = (CCall)Expr("FOO(1)");
        Assert.Throws<CSourceException>(() => MacroExpander.ExpandExpression(call, "e.c"));
    }

    [Fact]
    public void ExpandInitializerRejectsUnknownMacro()
    {
        var call = (CCall)Expr("FOO(1)");
        Assert.Throws<CSourceException>(() => MacroExpander.ExpandInitializer(call, "e.c"));
    }

    [Fact]
    public void ParseObjectMacroRejectsUnknownName()
    {
        Assert.Throws<CSourceException>(() => MacroExpander.ParseObjectMacro("no_such", new SourcePos(2, 3), "e.c"));
    }

    [Fact]
    public void ParseObjectMacroPutsUsePosOnAllNodes()
    {
        var cast = Assert.IsType<CCast>(MacroExpander.ParseObjectMacro("ts_builtin_sym_error", new SourcePos(3, 4), "e.c"));
        Assert.Equal(new SourcePos(3, 4), cast.Pos);
        Assert.Equal(new SourcePos(3, 4), cast.Operand.Pos);
    }

    [Fact]
    public void SubstituteRewritesEveryExpressionKindAndPosition()
    {
        var pos = new SourcePos(3, 4);
        CExpr argument = new CIntLiteral(new SourcePos(9, 9), 7, "7");
        var parameters = Param("p", argument);

        CExpr binary = MacroExpander.Substitute(Expr("p + 1"), parameters, pos);
        Assert.Equal(pos, binary.Pos);
        Assert.Equal(pos, ((CBinary)binary).Left.Pos);

        CExpr unary = MacroExpander.Substitute(Expr("-p"), parameters, pos);
        Assert.Equal(pos, ((CUnary)unary).Operand.Pos);

        CExpr cast = MacroExpander.Substitute(Expr("(uint8_t)p"), parameters, pos);
        Assert.Equal(pos, ((CCast)cast).Operand.Pos);

        CExpr indexed = MacroExpander.Substitute(Expr("q[p]"), parameters, pos);
        var index = (CIndex)indexed;
        Assert.Equal(pos, index.Target.Pos);
        Assert.Equal(pos, index.Index.Pos);

        CExpr call = MacroExpander.Substitute(Expr("f(p)"), parameters, pos);
        var nested = (CCall)call;
        Assert.Equal(pos, nested.Arguments[0].Pos);

        CExpr identifier = MacroExpander.Substitute(Expr("q"), parameters, pos);
        Assert.Equal(pos, identifier.Pos);

        CExpr literal = MacroExpander.Substitute(Expr("'a' + \"s\""), parameters, pos);
        Assert.Equal(pos, literal.Pos);
    }

    [Fact]
    public void SubstituteDoesNotReplaceParameterNamesInsideArguments()
    {
        var pos = new SourcePos(3, 4);
        CExpr argument = Expr("p + 1");
        CExpr result = MacroExpander.Substitute(Expr("STATE(p)"), Param("p", argument), pos);
        var call = (CCall)result;
        var inner = (CBinary)call.Arguments[0];
        Assert.Equal("p", Assert.IsType<CIdentifier>(inner.Left).Name);
    }

    [Fact]
    public void SubstituteRewritesInitializersItemsAndDesignators()
    {
        var pos = new SourcePos(3, 4);
        var parameters = Param("p", new CIntLiteral(new SourcePos(9, 9), 7, "7"));

        CInitializer list = MacroExpander.Substitute(Init("{ [p] = { .x = p } }"), parameters, pos);
        Assert.Equal(pos, list.Pos);
        var outer = (CInitList)list;
        Assert.Equal(pos, outer.Items[0].Pos);
        var designator = (CIndexDesignator)outer.Items[0].Designators[0];
        Assert.Equal(pos, designator.Pos);
        Assert.Equal(pos, designator.Index.Pos);
        var inner = (CInitList)outer.Items[0].Value;
        Assert.Equal(pos, inner.Items[0].Designators[0].Pos);

        CInitializer expression = MacroExpander.Substitute(Init("p"), parameters, pos);
        Assert.Equal(pos, expression.Pos);
    }

    [Fact]
    public void SubstituteRejectsUnknownInitializerKind()
    {
        CInitializer result = MacroExpander.Substitute(new EvilInit(new SourcePos(1, 1)), Param("p", Expr("1")), new SourcePos(2, 2));
        Assert.Equal(new SourcePos(2, 2), result.Pos);
    }

    // ------------------------------------------------------------ InitializerFlattener

    [Fact]
    public void UnknownElementTypeIsAnError()
    {
        CTranslationUnit unit = Unit("static const bogus_t a[] = { 1 };");
        Assert.Throws<KeyNotFoundException>(() => FlattenerOf(unit).FlattenVariable(unit.Variables[0]));
    }

    [Fact]
    public void ResolveMapsEveryScalarTypeNameUsedByParserC()
    {
        CTranslationUnit unit = Unit(
            "static const int16_t a[1] = { -1 };\n" +
            "static const int32_t b[1] = { -2 };\n" +
            "static const TSStateId c[1] = { 3 };\n" +
            "static const TSFieldId d[1] = { 4 };\n");
        var flattener = FlattenerOf(unit);
        Assert.Equal(-1, ((CIntValue)flattener.FlattenVariable(unit.Variables[0]).Elements[0]).Value);
        Assert.Equal(-2, ((CIntValue)flattener.FlattenVariable(unit.Variables[1]).Elements[0]).Value);
        Assert.Equal(3, ((CIntValue)flattener.FlattenVariable(unit.Variables[2]).Elements[0]).Value);
        Assert.Equal(4, ((CIntValue)flattener.FlattenVariable(unit.Variables[3]).Elements[0]).Value);
    }

    [Fact]
    public void OnlyFirstDimensionMayBeUnknown()
    {
        CTranslationUnit unit = Unit("static const uint16_t a[2][] = { { 1 } };");
        Assert.Throws<CSourceException>(() => FlattenerOf(unit).FlattenVariable(unit.Variables[0]));
    }

    [Fact]
    public void ArrayInitializerMustBeAList()
    {
        CTranslationUnit unit = Unit("static const uint16_t a[1] = 5;");
        Assert.Throws<CSourceException>(() => FlattenerOf(unit).FlattenVariable(unit.Variables[0]));
    }

    [Fact]
    public void NestedDimensionNeedsAList()
    {
        CTranslationUnit unit = Unit("static const uint16_t a[2][2] = { 1 };");
        Assert.Throws<CSourceException>(() => FlattenerOf(unit).FlattenVariable(unit.Variables[0]));
    }

    [Fact]
    public void NegativeIndexIsAnError()
    {
        CTranslationUnit unit = Unit("static const uint16_t a[2] = { [-1] = 5 };");
        Assert.Throws<CSourceException>(() => FlattenerOf(unit).FlattenVariable(unit.Variables[0]));
    }

    [Fact]
    public void ChainedDesignatorsAreAnError()
    {
        CTranslationUnit unit = Unit("static const uint16_t a[2] = { [0][0] = 5 };");
        Assert.Throws<CSourceException>(() => FlattenerOf(unit).FlattenVariable(unit.Variables[0]));
    }

    [Fact]
    public void FieldDesignatorOnArrayIsAnError()
    {
        CTranslationUnit unit = Unit("static const uint16_t a[2] = { .x = 5 };");
        Assert.Throws<CSourceException>(() => FlattenerOf(unit).FlattenVariable(unit.Variables[0]));
    }

    [Fact]
    public void ScalarFromOneElementBraceList()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Equal(5, ((CIntValue)flattener.FlattenValue(Init("{5}"), ParserHTypes.UInt8)).Value);
    }

    [Fact]
    public void ScalarBraceListMustHaveExactlyOneValue()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{}"), ParserHTypes.UInt8));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{1, 2}"), ParserHTypes.UInt8));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{[0] = 1}"), ParserHTypes.UInt8));
    }

    [Fact]
    public void StructContinuesAfterDesignatedMember()
    {
        var flattener = FlattenerOf(Unit(""));
        var value = Assert.IsType<CAggregateValue>(flattener.FlattenValue(Init("{ 1, .named = true }"), ParserHTypes.TSSymbolMetadata));
        Assert.Equal(1, ((CIntValue)value.Members["visible"]).Value);
        Assert.Equal(1, ((CIntValue)value.Members["named"]).Value);
    }

    [Fact]
    public void StructRejectsChainedDesignatorsAndIndexDesignators()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ .named.supertype = true }"), ParserHTypes.TSSymbolMetadata));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ [0] = true }"), ParserHTypes.TSSymbolMetadata));
    }

    [Fact]
    public void StructRejectsDuplicateAndExtraMembers()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ .named = true, .named = false }"), ParserHTypes.TSSymbolMetadata));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ true, true, true, true }"), ParserHTypes.TSSymbolMetadata));
    }

    [Fact]
    public void StructInitializerMustBeAList()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("5"), ParserHTypes.TSSymbolMetadata));
    }

    [Fact]
    public void UnionWithoutDesignatorInitializesFirstMember()
    {
        var flattener = FlattenerOf(Unit(""));
        var value = Assert.IsType<CAggregateValue>(flattener.FlattenValue(Init("{ { .type = 3 } }"), ParserHTypes.TSParseActionEntry));
        Assert.Equal("action", value.ActiveMember);
        var action = (CAggregateValue)value.Members["action"];
        Assert.Equal("type", action.ActiveMember);
        Assert.Equal(3, ((CIntValue)action.Members["type"]).Value);
    }

    [Fact]
    public void UnionRejectsWrongItemCountAndDesignators()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{}"), ParserHTypes.TSParseActionEntry));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ 1, 2 }"), ParserHTypes.TSParseActionEntry));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ [0] = 5 }"), ParserHTypes.TSParseActionEntry));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ .zz = 1 }"), ParserHTypes.TSParseActionEntry));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("{ { [0].x = 1 } }"), ParserHTypes.TSParseActionEntry));
    }

    [Fact]
    public void MacroCallOutsideParseActionEntryIsNotExpanded()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("SHIFT(1)"), ParserHTypes.UInt16));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("STATE(1)"), ParserHTypes.TSParseActionEntry));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("1"), ParserHTypes.TSParseActionEntry));
    }

    [Fact]
    public void StringAndPointerScalarsAcceptNull()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Same(CNullValue.Instance, flattener.FlattenValue(Init("NULL"), ParserHTypes.CString));
        Assert.Same(CNullValue.Instance, flattener.FlattenValue(Init("NULL"), ParserHTypes.Pointer));
    }

    [Fact]
    public void ScalarKindMustMatchTheValue()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("1"), ParserHTypes.CString));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("1"), ParserHTypes.Pointer));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("NULL"), ParserHTypes.UInt8));
    }

    [Fact]
    public void UnsupportedScalarKindIsAnError()
    {
        var flattener = FlattenerOf(Unit(""));
        var weird = new CScalarType("weird", (CScalarKind)99);
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("1"), weird));
    }

    [Fact]
    public void ZeroValueRejectsUnknownAggregateKind()
    {
        var evil = new CStructType("evil", new List<CMember> { new("m", new EvilType("x")) });
        Assert.Throws<InvalidOperationException>(() => InitializerFlattener.ZeroValue(evil));
    }

    [Fact]
    public void FlattenValueRejectsUnknownTypeKind()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Throws<CSourceException>(() => flattener.FlattenValue(Init("1"), new EvilType("x")));
    }

    [Fact]
    public void ZeroFillsUnionsWithFirstMember()
    {
        CTranslationUnit unit = Unit("static const TSParseActionEntry a[2] = { [1] = RECOVER() };");
        var array = FlattenerOf(unit).FlattenVariable(unit.Variables[0]);
        var zero = Assert.IsType<CAggregateValue>(array.Elements[0]);
        Assert.Equal("action", zero.ActiveMember);
        var action = (CAggregateValue)zero.Members["action"];
        Assert.Equal("shift", action.ActiveMember);
    }

    [Fact]
    public void BoolValuesAreConvertedToZeroOrOne()
    {
        var flattener = FlattenerOf(Unit(""));
        Assert.Equal(1, ((CIntValue)flattener.FlattenValue(Init("2"), ParserHTypes.Bool)).Value);
        Assert.Equal(0, ((CIntValue)flattener.FlattenValue(Init("0"), ParserHTypes.Bool)).Value);
    }

    // ------------------------------------------------------------ GrammarModelBuilder

    [Fact]
    public void BuildReadsAFileFromDisk()
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("Json"));
        Assert.Equal("json", model.Tables.Name);
    }

    [Fact]
    public void PlaceholderLexFunctionThrowsWhenCalled()
    {
        Assert.Throws<InvalidOperationException>(() => GrammarModelBuilder.PlaceholderLexFunction(null!, 0));
    }

    [Fact]
    public void NameAndScannersDefaultToNull()
    {
        GrammarModel model = BuildSnippet("", ".abi_version = 15");
        Assert.Null(model.Tables.Name);
        Assert.Null(model.LexFunction);
        Assert.Null(model.KeywordLexFunction);
        Assert.Null(model.Tables.ParseActions);
        Assert.All(model.ExternalScannerFunctions, entry => Assert.Null(entry.Value));
    }

    [Fact]
    public void ParseActionReduceIsConverted()
    {
        GrammarModel model = BuildSnippet(
            "static const TSParseActionEntry a[] = { [0] = REDUCE(7, 2, -3, 9) };",
            ".abi_version = 15, .parse_actions = a");
        ParseActionEntry action = model.Tables.ParseActions![0];
        Assert.Equal(ParseActionType.Reduce, action.Type);
        Assert.Equal(2, action.ReduceChildCount);
        Assert.Equal(7, action.ReduceSymbol);
        Assert.Equal(-3, action.ReduceDynamicPrecedence);
        Assert.Equal(9, action.ReduceProductionId);
    }

    [Fact]
    public void ParseActionAcceptIsConverted()
    {
        GrammarModel model = BuildSnippet(
            "static const TSParseActionEntry a[] = { [0] = ACCEPT_INPUT() };",
            ".abi_version = 15, .parse_actions = a");
        Assert.Equal(ParseActionType.Accept, model.Tables.ParseActions![0].Type);
    }

    [Fact]
    public void ParseActionTypeMustBeAcceptOrRecover()
    {
        Assert.Throws<CSourceException>(() => BuildSnippet(
            "static const TSParseActionEntry a[] = { [0] = {{ .type = 7 }} };",
            ".abi_version = 15, .parse_actions = a"));
        Assert.Throws<CSourceException>(() => BuildSnippet(
            "static const TSParseActionEntry a[] = { [0] = {{ .type = 1 }} };",
            ".abi_version = 15, .parse_actions = a"));
    }

    [Fact]
    public void BuildRejectsNonZeroReferenceIndices()
    {
        Assert.Throws<CSourceException>(() => BuildSnippet(
            "static const uint16_t t[2][2] = {{ 1, 2 }, { 3, 4 }};",
            ".abi_version = 15, .parse_table = &t[1][0]"));
    }

    [Fact]
    public void BuildRejectsFunctionUsedAsArray()
    {
        Assert.Throws<CSourceException>(() => BuildSnippet(
            "static bool ts_lex(TSLexer *lexer, TSStateId state) { return false; }",
            ".abi_version = 15, .parse_table = ts_lex"));
    }

    [Fact]
    public void BuildRejectsVariableUsedAsLexFunction()
    {
        Assert.Throws<CSourceException>(() => BuildSnippet(
            "static const uint16_t t[1] = { 5 };",
            ".abi_version = 15, .lex_fn = t"));
    }

    [Fact]
    public void BuildRejectsScannerFunctionThatIsNoPrototype()
    {
        Assert.Throws<CSourceException>(() => BuildSnippet(
            "static bool ts_lex(TSLexer *lexer, TSStateId state) { return false; }",
            ".abi_version = 15, .external_scanner = { NULL, NULL, ts_lex, NULL, NULL }"));
    }

    [Fact]
    public void ResolveArrayRejectsValuesThatAreNotArrayReferences()
    {
        CTranslationUnit unit = Unit("");
        var flattener = FlattenerOf(unit);
        Assert.Null(GrammarModelBuilder.ResolveArray(unit, flattener, CNullValue.Instance, "f"));
        Assert.Throws<CSourceException>(() => GrammarModelBuilder.ResolveArray(unit, flattener, new CIntValue(5), "f"));
        Assert.Throws<CSourceException>(() => GrammarModelBuilder.ResolveArray(unit, flattener, new CReferenceValue("no_such", Array.Empty<long>()), "f"));
    }

    [Fact]
    public void BindLexFunctionRejectsValuesThatAreNotFunctions()
    {
        CTranslationUnit unit = Unit("");
        Assert.Null(GrammarModelBuilder.BindLexFunction(unit, CNullValue.Instance));
        Assert.Throws<CSourceException>(() => GrammarModelBuilder.BindLexFunction(unit, new CIntValue(5)));
    }

    [Fact]
    public void BindExternalFunctionRejectsIndexedAndUnknownFunctions()
    {
        CTranslationUnit unit = Unit("void f(void *);\n");
        var functions = new Dictionary<string, string?>(StringComparer.Ordinal);
        Assert.Throws<CSourceException>(() =>
            GrammarModelBuilder.BindExternalFunction(unit, new CReferenceValue("f", new long[] { 1 }), "scan", functions));
        Assert.Throws<CSourceException>(() =>
            GrammarModelBuilder.BindExternalFunction(unit, new CReferenceValue("no_such", Array.Empty<long>()), "scan", functions));
    }

    [Fact]
    public void ConvertActionRejectsUnknownTypeValues()
    {
        CTranslationUnit unit = Unit("");
        Assert.Throws<CSourceException>(() => GrammarModelBuilder.ConvertAction(unit, ActionWithType(7)));
        Assert.Throws<CSourceException>(() => GrammarModelBuilder.ConvertAction(unit, ActionWithType(1)));
    }

    private static CAggregateValue ActionWithType(long typeValue)
    {
        var action = new CAggregateValue(
            ParserHTypes.TSParseAction,
            new Dictionary<string, CValue>(StringComparer.Ordinal) { ["type"] = new CIntValue(typeValue) },
            "type");
        return new CAggregateValue(
            ParserHTypes.TSParseActionEntry,
            new Dictionary<string, CValue>(StringComparer.Ordinal) { ["action"] = action },
            "action");
    }
}
