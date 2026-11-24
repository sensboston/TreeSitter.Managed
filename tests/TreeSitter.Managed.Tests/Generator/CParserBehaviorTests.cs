using TreeSitter.Generator.CSource;
using Xunit;

namespace TreeSitter.Tests.Generator;

public class CParserBehaviorTests
{
    [Fact]
    public void EvaluatesSupportedConditionalsAndDropsInactiveDeclarations()
    {
        const string source = """
            #define FIRST
            #define CONTINUED \
              1
            #ifdef FIRST
            static const bool first_flag[] = {true,};
            #endif
            #if defined(FIRST) || defined(SECOND)
            enum selected { VALUE = -1, };
            #else
            int ignored_one;
            #endif
            #define THIRD
            #if defined(UNKNOWN)
            int ignored_two;
            #elif defined(THIRD)
            static const bool flag[] = {true,};
            #else
            int ignored_three;
            #endif
            #ifdef MISSING
            int ignored_four;
            #else
            static const char * const names[] = {"x",};
            #endif
            #define TS_PUBLIC
            TS_PUBLIC const TSLanguage *tree_sitter_test(void) {
              static const TSLanguage language = {};
              return &language;
            }
            """;

        CTranslationUnit unit = Parse(source);

        Assert.Equal("test.c", unit.FileName);
        string[] expectedDefines = ["FIRST", "CONTINUED", "THIRD", "TS_PUBLIC"];
        Assert.Equal(expectedDefines, unit.Defines.Select(static define => define.Name));
        CDefine continued = Assert.Single(unit.Defines, static define => string.Equals(define.Name, "CONTINUED", StringComparison.Ordinal));
        Assert.Equal("1", Assert.Single(continued.Body).Text);
        Assert.Equal("selected", Assert.Single(unit.Enums).Name);
        Assert.Equal(3, unit.Variables.Count);
        Assert.Equal("first_flag", unit.Variables[0].Name);
        Assert.Equal("flag", unit.Variables[1].Name);
        Assert.Equal("names", unit.Variables[2].Name);
        Assert.Equal("char * const", unit.Variables[2].ElementType);
        Assert.Equal("tree_sitter_test", unit.Language.FunctionName);
    }

    [Fact]
    public void ReadsExternCPrototypesAndNestedFunctionBodies()
    {
        const string source = """
            #define TS_PUBLIC
            extern "C" {
              void *scanner_create(void);
              static bool ts_lex(int value) {
                if (value) { return true; }
                return false;
              }
              TS_PUBLIC const TSLanguage *tree_sitter_test(void) {
                static const TSLanguage language = {};
                return &language;
              }
            }
            """;

        CTranslationUnit unit = Parse(source);

        CPrototype prototype = Assert.Single(unit.Prototypes);
        Assert.Equal("void *", prototype.ReturnType);
        Assert.Equal("scanner_create", prototype.Name);
        Assert.Equal("void", prototype.Parameters);
        CFunction function = Assert.Single(unit.Functions);
        Assert.Equal("bool", function.ReturnType);
        Assert.Equal("int value", function.Parameters);
        Assert.Equal("if", function.Body[0].Text);
        Assert.Contains(function.Body, static token => string.Equals(token.Text, "{", StringComparison.Ordinal));
        Assert.Equal("tree_sitter_test", unit.Language.FunctionName);
    }

    [Theory]
    [InlineData("#if NAME\n#endif\n", 1, 1)]
    [InlineData("#if defined(A) && defined(B)\n#endif\n", 1, 1)]
    [InlineData("#ifndef A\n#endif\n", 1, 1)]
    [InlineData("#elif defined(A)\n", 1, 1)]
    [InlineData("#else\n", 1, 1)]
    [InlineData("#endif\n", 1, 1)]
    [InlineData("#if defined(A)\n", 1, 1)]
    public void RejectsUnsupportedOrUnbalancedPreprocessorDirectives(string source, int line, int column)
    {
        CSourceException error = Assert.Throws<CSourceException>(() => Parse(source));

        Assert.Equal(new SourcePos(line, column), error.Pos);
        Assert.StartsWith($"test.c:{line}:{column}: ", error.Message, StringComparison.Ordinal);
    }

    private static CTranslationUnit Parse(string source)
    {
        List<CToken> tokens = new CTokenizer(source, "test.c").Tokenize();
        return new CParser(tokens, "test.c").ParseTranslationUnit();
    }
}
