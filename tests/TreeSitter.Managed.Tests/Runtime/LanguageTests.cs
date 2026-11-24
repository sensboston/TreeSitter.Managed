using TreeSitter.Abi;
using TreeSitter.Generator.Dump;
using TreeSitter.Generator.Model;
using TreeSitter.Tests.Generator;
using TreeSitter.Tests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace TreeSitter.Tests.Runtime;

// Contract tests for Language (task M1a-T5).
public class LanguageTests
{
    private readonly ITestOutputHelper _output;

    public LanguageTests(ITestOutputHelper output) => _output = output;

    private static Language Load(string grammar) =>
        Language.FromTables(GrammarModelBuilder.Build(CParserTests.GrammarParserC(grammar)).Tables);

    [OracleFact]
    public void JsonApiMatchesOracle() => AssertMatchesOracle("Json", "json");

    [OracleFact]
    public void CApiMatchesOracle() => AssertMatchesOracle("C", "c");

    [OracleFact]
    public void PythonApiMatchesOracle() => AssertMatchesOracle("Python", "python");

    [Theory]
    [InlineData(13u)]
    [InlineData(14u)]
    [InlineData(16u)]
    public void RejectsOtherAbiVersions(uint version)
    {
        LanguageTables tables = GrammarModelBuilder.Build(CParserTests.GrammarParserC("Json")).Tables;
        tables.AbiVersion = version;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));
    }

    [Fact]
    public void NamesRoundTrip()
    {
        Language json = Load("Json");
        Assert.Equal("json", json.Name);
        ushort document = json.SymbolForName("document", isNamed: true);
        Assert.NotEqual(0, document);
        Assert.Equal("document", json.SymbolName(document));
        Assert.Equal(SymbolType.Regular, json.SymbolType(document));
        Assert.Equal(0, json.SymbolForName("no_such_symbol", isNamed: true));
        ushort key = json.FieldIdForName("key");
        Assert.NotEqual(0, key);
        Assert.Equal("key", json.FieldNameForId(key));
        Assert.Equal(0, json.FieldIdForName("no_such_field"));
    }

    private void AssertMatchesOracle(string grammar, string oracleName)
    {
        NativeOracle.EnsureAvailable();
        string expected = NativeOracle.OracleDumpLanguageApi(oracleName)!;
        string actual = LanguageApiDumper.Dump(Load(grammar));
        if (expected == actual)
        {
            return;
        }

        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
        {
            i++;
        }
        for (int k = Math.Max(0, i - 3); k <= i + 3; k++)
        {
            _output.WriteLine($"{k + 1,7} oracle : {(k < e.Length ? e[k] : "<end>")}");
            _output.WriteLine($"{k + 1,7} managed: {(k < a.Length ? a[k] : "<end>")}");
        }
        Assert.Fail($"{grammar}: first difference at line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', managed '{(i < a.Length ? a[i] : "<end>")}'");
    }
}
