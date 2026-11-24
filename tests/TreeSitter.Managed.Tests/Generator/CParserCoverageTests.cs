using TreeSitter.Generator.CSource;
using Xunit;

namespace TreeSitter.Tests.Generator;

public class CParserCoverageTests
{
    private const string LanguageFunction = """
        TS_PUBLIC const TSLanguage *tree_sitter_test(void) {
          static const TSLanguage language = {};
          return &language;
        }
        """;

    [Fact]
    public void ConstructorsRejectNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new CParser(null!, "test.c"));
        Assert.Throws<ArgumentNullException>(() => new CParser(Array.Empty<CToken>(), null!));
    }

    [Fact]
    public void AddsEofForTokenListsWithoutOne()
    {
        CToken[] tokens = [new CToken(CTokenKind.Identifier, "value", 1, 1)];
        var parser = new CParser(tokens, "test.c");
        Assert.Same(tokens, parser.Tokens);
        Assert.Equal("test.c", parser.FileName);
        Assert.Equal("value", Assert.IsType<CIdentifier>(parser.ParseExpression()).Name);

        var emptyParser = new CParser(Array.Empty<CToken>(), "test.c");
        Assert.Throws<CSourceException>(() => emptyParser.ParseExpression());

        CToken[] duplicateEofTokens =
        [
            new CToken(CTokenKind.Identifier, "value", 1, 1),
            new CToken(CTokenKind.EOF, string.Empty, 1, 6),
            new CToken(CTokenKind.EOF, string.Empty, 1, 6),
        ];
        var duplicateEofParser = new CParser(duplicateEofTokens, "test.c");
        Assert.Equal("value", Assert.IsType<CIdentifier>(duplicateEofParser.ParseExpression()).Name);
    }

    [Fact]
    public void RejectsMalformedDirectiveTokens()
    {
        AssertMalformedDirective(string.Empty);
        AssertMalformedDirective("if");
    }

    [Theory]
    [InlineData("#\n")]
    [InlineData("#define\n")]
    [InlineData("#if\n")]
    [InlineData("#if defined\n#endif\n")]
    [InlineData("#if defined(\n#endif\n")]
    [InlineData("#if defined(NAME\n#endif\n")]
    [InlineData("#if defined(NAME) ||\n#endif\n")]
    [InlineData("#ifdef\n")]
    [InlineData("#ifdef ONE TWO\n")]
    [InlineData("#else extra\n")]
    [InlineData("#endif extra\n")]
    [InlineData("#elif defined(NAME)\n")]
    [InlineData("#else\n")]
    [InlineData("#endif\n")]
    [InlineData("#if defined(NAME)\n#else\n#elif defined(OTHER)\n#endif\n")]
    [InlineData("#if defined(NAME)\n#else\n#else\n#endif\n")]
    [InlineData("#error unsupported\n")]
    [InlineData("#define NAME @\n")]
    public void RejectsMalformedPreprocessorDirectives(string source)
    {
        Assert.Throws<CSourceException>(() => Parse(source));
    }

    [Fact]
    public void DiscardsUnknownDirectivesInsideInactiveBranch()
    {
        CSourceException error = ParseError("#if defined(NEVER)\n#error ignored\n#endif\n");

        Assert.True(error.Text.Contains("Missing TS_PUBLIC language function", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsMalformedFileScopeAndLanguageDeclarations()
    {
        Assert.Throws<CSourceException>(() => Parse(""));
        Assert.Throws<CSourceException>(() => Parse("}"));
        Assert.Throws<CSourceException>(() => Parse("extern \"C\" {"));
        Assert.Throws<CSourceException>(() => Parse(LanguageFunction + LanguageFunction));

        Assert.Throws<CSourceException>(() => Parse(LanguageDeclaration(returnType: "int *")));
        Assert.Throws<CSourceException>(() => Parse(LanguageDeclaration(parameters: "int value")));
        Assert.Throws<CSourceException>(() => Parse(LanguageDeclaration(variableType: "int")));
        Assert.Throws<CSourceException>(() => Parse(LanguageDeclaration(variableName: "other")));
        Assert.Throws<CSourceException>(() => Parse(LanguageDeclaration(variableDimensions: "[]")));
        Assert.Throws<CSourceException>(() => Parse(LanguageDeclaration(returnExpression: "&other")));
        Assert.Throws<CSourceException>(() => Parse(LanguageDeclaration(returnExpression: "language")));
    }

    [Theory]
    [InlineData("enum e {")]
    [InlineData("enum e { A B };")]
    [InlineData("enum e { = 1 };")]
    [InlineData("static const int value;")]
    [InlineData("static const int = 1;")]
    [InlineData("static const int value[] = {1, 2")]
    [InlineData("static const int value[] = {[0] 1};")]
    [InlineData("static const int value[] = {. = 1};")]
    [InlineData("static const int value[] = {1 2};")]
    [InlineData("static bool f(void);")]
    [InlineData("static bool f(void) {")]
    [InlineData("static bool f(void {")]
    [InlineData("void scanner(void)")]
    [InlineData("int f(void) {")]
    public void RejectsMalformedDeclarationsAndInitializers(string source)
    {
        Assert.Throws<CSourceException>(() => Parse(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("&")]
    [InlineData("!")]
    [InlineData("()")]
    [InlineData("(1")]
    [InlineData("NAME(1,)")]
    [InlineData("0x")]
    [InlineData("9223372036854775808")]
    public void RejectsUnsupportedOrIncompleteExpressions(string source)
    {
        Assert.Throws<CSourceException>(() => ParseExpression(source));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{1,")]
    [InlineData("{[0] =}")]
    [InlineData("{[0] 1}")]
    [InlineData("{. = 1}")]
    [InlineData("{1 2}")]
    public void RejectsMalformedInitializers(string source)
    {
        Assert.Throws<CSourceException>(() => ParseInitializer(source));
    }

    [Fact]
    public void AcceptsParenthesizedExpressionsAndEmptyInitializerLists()
    {
        var expression = Assert.IsType<CIntLiteral>(ParseExpression("(15)"));
        Assert.Equal(15, expression.Value);
        Assert.Empty(Assert.IsType<CInitList>(ParseInitializer("{}")).Items);
    }

    private static void AssertMalformedDirective(string text)
    {
        var tokens = new[] { new CToken(CTokenKind.PreprocessorDirective, text, 1, 1) };
        Assert.Throws<CSourceException>(() => new CParser(tokens, "test.c").ParseTranslationUnit());
    }

    private static CTranslationUnit Parse(string source)
    {
        List<CToken> tokens = new CTokenizer(source, "test.c").Tokenize();
        return new CParser(tokens, "test.c").ParseTranslationUnit();
    }

    private static CSourceException ParseError(string source)
    {
        List<CToken> tokens = new CTokenizer(source, "test.c").Tokenize();
        return Assert.Throws<CSourceException>(() => new CParser(tokens, "test.c").ParseTranslationUnit());
    }

    private static CExpr ParseExpression(string source)
    {
        List<CToken> tokens = new CTokenizer(source, "test.c").Tokenize();
        return new CParser(tokens, "test.c").ParseExpression();
    }

    private static CInitializer ParseInitializer(string source)
    {
        List<CToken> tokens = new CTokenizer(source, "test.c").Tokenize();
        return new CParser(tokens, "test.c").ParseInitializer();
    }

    private static string LanguageDeclaration(
        string returnType = "const TSLanguage *",
        string parameters = "void",
        string variableType = "TSLanguage",
        string variableName = "language",
        string variableDimensions = "",
        string returnExpression = "&language") =>
        $"TS_PUBLIC {returnType} tree_sitter_test({parameters}) {{ static const {variableType} {variableName}{variableDimensions} = {{}}; return {returnExpression}; }}";
}
