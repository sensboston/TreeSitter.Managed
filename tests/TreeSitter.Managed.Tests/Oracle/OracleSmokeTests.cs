using Xunit;

namespace TreeSitter.Tests.Oracle;

public class OracleSmokeTests
{
    // One line = one value: `name = value`, `name[i] = value`,
    // or `name count=N reusable=R` (table_entry / parse_actions header).
    // See tests/Oracle/FORMATS.md.
    private const string LineRegex = @"^[A-Za-z_][A-Za-z0-9_.\[\]]*( = .+| count=\d+ reusable=[01])$";

    private static readonly string[] OracleLanguages = { "json", "c", "python" };

    [OracleFact]
    public void EachOracleLanguageIsNotNullAndAbiVersionIs15()
    {
        NativeOracle.EnsureAvailable();
        foreach (string name in OracleLanguages)
        {
            IntPtr language = NativeOracle.OracleLanguage(name);
            Assert.NotEqual(IntPtr.Zero, language);
            Assert.Equal(15u, NativeOracle.OracleAbiVersion(language));
        }
    }

    [OracleFact]
    public void UnknownLanguageReturnsNull()
    {
        NativeOracle.EnsureAvailable();
        Assert.Equal(IntPtr.Zero, NativeOracle.OracleLanguage("no-such"));
    }

    [OracleFact]
    public void DumpsAreNonEmptyDeterministicAndFormatted()
    {
        NativeOracle.EnsureAvailable();
        foreach (string name in OracleLanguages)
        {
            IntPtr language = NativeOracle.OracleLanguage(name);
            Assert.NotEqual(IntPtr.Zero, language);

            string tables1 = NativeOracle.OracleDumpTables(name)!;
            string tables2 = NativeOracle.OracleDumpTables(name)!;
            string api1 = NativeOracle.OracleDumpLanguageApi(name)!;
            string api2 = NativeOracle.OracleDumpLanguageApi(name)!;

            Assert.False(string.IsNullOrEmpty(tables1), $"tables empty for {name}");
            Assert.False(string.IsNullOrEmpty(api1), $"api empty for {name}");
            Assert.True(tables1 == tables2, $"tables not deterministic for {name}");
            Assert.True(api1 == api2, $"api not deterministic for {name}");

            AssertLinesMatchFormat(tables1, name);
            AssertLinesMatchFormat(api1, name);
            Assert.DoesNotContain("<layout error", tables1, StringComparison.Ordinal);
            Assert.DoesNotContain("<unknown type", tables1, StringComparison.Ordinal);
        }
    }

    [OracleFact]
    public void CAndPythonDumpRealSections()
    {
        NativeOracle.EnsureAvailable();

        // C has a keyword lexer.
        string cTables = NativeOracle.OracleDumpTables("c")!;
        Assert.Contains("keyword_lex_fn = set\n", cTables);

        // Python has reserved words or external scanner states (or both).
        string pythonTables = NativeOracle.OracleDumpTables("python")!;
        Assert.True(
            pythonTables.Contains("reserved_words[") ||
            pythonTables.Contains("external_scanner.states["),
            "Python dump should print reserved_words or external_scanner.states");
    }

    private static void AssertLinesMatchFormat(string text, string languageName)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            Assert.False(line.EndsWith(' '), $"line ends with a space in {languageName}: {line}");
            Assert.Matches(LineRegex, line);
        }
    }
}
