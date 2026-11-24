using System.Text;
using TreeSitter.Abi;
using TreeSitter.Grammars.C;
using TreeSitter.Grammars.Json;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Parsing;

/// <summary>The public API of Parser (api.h: ts_parser_*), beside the oracle comparisons.</summary>
public class ParserApiTests
{
    [Fact]
    public void ParseWithoutLanguageReturnsNull()
    {
        Parser parser = new Parser();
        Assert.Null(parser.Language);
        Assert.Null(parser.ParseString("[]"u8.ToArray()));
    }

    [Fact]
    public void SetLanguageKeepsTheLanguage()
    {
        Parser parser = new Parser();
        Assert.True(parser.SetLanguage(JsonGrammar.Language));
        Assert.Same(JsonGrammar.Language, parser.Language);
        Assert.True(parser.SetLanguage(null));
        Assert.Null(parser.Language);
    }

    [Fact]
    public void SetLanguageRejectsALanguageWithoutLexFunction()
    {
        LanguageTables tables = JsonGrammar.CreateTables();
        tables.LexFn = null;
        Parser parser = new Parser();
        Assert.False(parser.SetLanguage(Language.FromTables(tables)));
        Assert.Null(parser.Language);
    }

    [Fact]
    public void OldTreeOfAnotherLanguageIsRejected()
    {
        Parser parser = new Parser();
        parser.SetLanguage(JsonGrammar.Language);
        Tree json = parser.ParseString("[1]"u8.ToArray())!;
        parser.SetLanguage(CGrammar.Language);
        Assert.Null(parser.ParseString("int x;"u8.ToArray(), json));
    }

    [Fact]
    public void LoggerAndIncludedRangesAreKept()
    {
        Parser parser = new Parser();
        Logger logger = (_, _) => { };
        parser.Logger = logger;
        Assert.Same(logger, parser.Logger);

        Range[] ranges = [new Range(new Point(0, 1), new Point(0, 3), 1, 3)];
        Assert.True(parser.SetIncludedRanges(ranges));
        Assert.Equal(ranges, parser.IncludedRanges());
    }

    [OracleFact]
    public void Utf16StringGivesTheSameTreeAsUtf8()
    {
        Parser parser = new Parser();
        parser.SetLanguage(JsonGrammar.Language);
        const string Text = "{\"é\": [1, true, null]}";
        Tree utf8 = parser.ParseString(Encoding.UTF8.GetBytes(Text))!;
        Tree utf16 = parser.ParseString(Text)!;
        Assert.Equal(utf8.RootNode.ToString(), utf16.RootNode.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(Text), (int)utf8.RootNode.EndByte);
        Assert.Equal(Text.Length * 2, (int)utf16.RootNode.EndByte);
    }

    [Fact]
    public void ResetDropsAParseThatWasStopped()
    {
        Parser parser = new Parser();
        parser.SetLanguage(CGrammar.Language);
        byte[] source = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("int f(void) { return 1 + 2; }\n", 20)));
        ParseOptions stop = new ParseOptions { ProgressCallback = _ => true };
        Assert.Null(parser.Parse(ParseInputs.FromBytes(source, InputEncoding.Utf8), null, stop));

        // Without Reset the next parse resumes; after Reset it starts again and finishes.
        parser.Reset();
        List<string> log = new List<string>();
        parser.Logger = (_, message) => log.Add(message);
        Tree tree = parser.ParseString(source)!;
        Assert.Contains("new_parse", log);
        Assert.DoesNotContain("resume_parsing", log);
        Assert.False(tree.RootNode.HasError);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Parser parser = new Parser();
        Assert.Throws<ArgumentNullException>(() => parser.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => parser.ParseString((string)null!));
        Assert.Throws<ArgumentNullException>(() => parser.Parse(ParseInputs.FromString("x"), null, null!));
    }
}
