using System.Text.RegularExpressions;
using TreeSitter.Generator.Model;
using Xunit;

namespace TreeSitter.Tests.Generator;

// Checks the hand-written copies of parser.h (MacroExpander, ParserHTypes) against the
// vendored tests/Oracle/tree-sitter/lib/src/parser.h of the pinned tag.
public partial class ParserHContractTests
{
    private static readonly string ParserH = File.ReadAllText(Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(CParserTests.GrammarParserC("Json")))))!,
        "..", "tests", "Oracle", "tree-sitter", "lib", "src", "parser.h"));

    public static TheoryData<string> AllMacroNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in MacroExpander.ExpressionMacros.Keys.Concat(MacroExpander.InitializerMacros.Keys))
        {
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllMacroNames))]
    public void MacroDefinitionMatchesParserH(string name)
    {
        MacroDefinition definition = MacroExpander.ExpressionMacros.TryGetValue(name, out MacroDefinition? d)
            ? d
            : MacroExpander.InitializerMacros[name];

        // #define NAME(params) \ ... body ... (lines joined, backslashes removed)
        Match match = Regex.Match(ParserH, @"#define " + Regex.Escape(name) + @"\(([^)]*)\)((?:[^\n]*\\\n)*[^\n]*)");
        Assert.True(match.Success, $"#define {name} not found in parser.h");
        string[] parameters = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(parameters, definition.Parameters);
        Assert.Equal(Normalize(match.Groups[2].Value.Replace("\\\n", " ", StringComparison.Ordinal)), Normalize(definition.Body));
    }

    [Theory]
    [InlineData("ts_builtin_sym_error")]
    [InlineData("ts_builtin_sym_end")]
    public void ObjectMacroMatchesParserH(string name)
    {
        Match match = Regex.Match(ParserH, @"#define " + Regex.Escape(name) + @" ([^\n]*)");
        Assert.True(match.Success);
        Assert.Equal(Normalize(match.Groups[1].Value), Normalize(MacroExpander.ObjectMacros[name]));
    }

    [Fact]
    public void ParseActionTypeEnumMatchesParserH()
    {
        Match match = Regex.Match(ParserH, @"typedef enum \{([^}]*)\} TSParseActionType;");
        Assert.True(match.Success);
        string[] members = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < members.Length; i++)
        {
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), MacroExpander.ObjectMacros[members[i]]);
        }
    }

    public static TheoryData<string> AllStructs() => new()
    {
        "TSFieldMapEntry", "TSMapSlice", "TSSymbolMetadata", "TSLexerMode", "TSCharacterRange", "TSLanguage",
    };

    [Theory]
    [MemberData(nameof(AllStructs))]
    public void StructMembersMatchParserH(string name)
    {
        var type = (CStructType)ParserHTypes.Resolve(name);
        string body = name == "TSLanguage"
            ? Regex.Match(ParserH, @"struct TSLanguage \{(.*?)\n\};", RegexOptions.Singleline).Groups[1].Value
            : Regex.Match(ParserH, @"typedef struct \{([^}]*)\} " + name + ";").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(body), name);
        AssertMembersInOrder(body, type.Members.Select(m => m.Name));
    }

    [Fact]
    public void ParseActionUnionsMatchParserH()
    {
        string action = Regex.Match(ParserH, @"typedef union \{(.*?)\} TSParseAction;", RegexOptions.Singleline).Groups[1].Value;
        AssertMembersInOrder(action, ParserHTypes.ShiftAction.Members.Select(m => m.Name).Prepend("struct"));
        AssertMembersInOrder(action, ParserHTypes.ReduceAction.Members.Select(m => m.Name));
        AssertMembersInOrder(action, ParserHTypes.TSParseAction.Members.Select(m => m.Name));
        string entry = Regex.Match(ParserH, @"typedef union \{(.*?)\} TSParseActionEntry;", RegexOptions.Singleline).Groups[1].Value;
        AssertMembersInOrder(entry, ParserHTypes.TSParseActionEntry.Members.Select(m => m.Name));
        AssertMembersInOrder(entry, ParserHTypes.ActionEntryHeader.Members.Select(m => m.Name));
    }

    // Each name must occur as a whole word after the previous one.
    private static void AssertMembersInOrder(string body, IEnumerable<string> names)
    {
        int position = 0;
        foreach (string name in names)
        {
            Match match = new Regex(@"\b" + Regex.Escape(name) + @"\b").Match(body, position);
            Assert.True(match.Success, $"member '{name}' not found in order");
            position = match.Index + match.Length;
        }
    }

    private static string Normalize(string text) => Spaces().Replace(text, " ").Trim().Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
