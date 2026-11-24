using TreeSitter.Grammars.C;
using TreeSitter.Grammars.Json;
using TreeSitter.Grammars.Python;
using Xunit;

namespace TreeSitter.Tests.Grammars;

/// <summary>The query files embedded in the grammar assemblies compile with their language.</summary>
public class GrammarQueryTests
{
    [Fact]
    public void EmbeddedQueriesCompile()
    {
        AssertQuery(JsonGrammar.Language, JsonGrammar.Query("highlights"));
        AssertQuery(CGrammar.Language, CGrammar.Query("highlights"));
        AssertQuery(CGrammar.Language, CGrammar.Query("tags"));
        AssertQuery(PythonGrammar.Language, PythonGrammar.Query("highlights"));
        AssertQuery(PythonGrammar.Language, PythonGrammar.Query("tags"));
    }

    [Fact]
    public void MissingQueryIsNull()
    {
        Assert.Null(JsonGrammar.Query("tags"));
        Assert.Null(CGrammar.Query("no-such-file"));
    }

    private static void AssertQuery(Language language, string? source)
    {
        Assert.NotNull(source);
        Query query = new Query(language, source);
        Assert.True(query.PatternCount > 0);
    }
}
