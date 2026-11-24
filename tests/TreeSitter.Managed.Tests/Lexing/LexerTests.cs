using TreeSitter.Tests.Generator;
using TreeSitter.Tests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace TreeSitter.Tests.Lexing;

// Contract tests for the Lexer port (task M1b-B2). The script format is in
// tests/Oracle/FORMATS.md, section "Lexer script".
public class LexerTests
{
    private readonly ITestOutputHelper _output;

    public LexerTests(ITestOutputHelper output) => _output = output;

    private static string ScriptDir() => Path.Combine(Path.GetDirectoryName(CParserTests.GrammarParserC("Json"))!,
        "..", "..", "..", "..", "tests", "TreeSitter.Managed.Tests", "Lexing", "Scripts");

    public static TheoryData<string> ScriptFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(ScriptDir(), "*.txt").OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [OracleTheory]
    [MemberData(nameof(ScriptFiles))]
    public void ScriptMatchesOracle(string fileName)
    {
        NativeOracle.EnsureAvailable();
        string script = File.ReadAllText(Path.Combine(ScriptDir(), fileName)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Compare(script, fileName);
    }

    [OracleFact]
    public void RandomScriptsMatchOracle()
    {
        NativeOracle.EnsureAvailable();
        for (int seed = 0; seed < 5_000; seed++)
        {
            Compare(LexerScriptGenerator.Generate(seed, 20 + (seed % 60)), $"seed {seed}");
        }
    }

    [Fact]
    public void GeneratorIsDeterministicAndUsesAllCommands()
    {
        Assert.Equal(LexerScriptGenerator.Generate(3, 50), LexerScriptGenerator.Generate(3, 50));
        string all = string.Concat(Enumerable.Range(0, 100).Select(seed => LexerScriptGenerator.Generate(seed, 60)));
        foreach (string command in new[] { "input utf8 ", "input utf16le ", "input utf16be ", "ranges ", "reset ", "start", "advance 0", "advance 1",
            "mark_end", "column", "eof", "at_range_start", "finish ", "dump" })
        {
            Assert.Contains("\n" + command, "\n" + all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BytesInputHonorsChunkSize()
    {
        IParseInput input = ParseInputs.FromBytes(new byte[] { 1, 2, 3, 4, 5 }, InputEncoding.Utf8, chunkSize: 2);
        Assert.Equal(new byte[] { 1, 2 }, input.Read(0, default).ToArray());
        Assert.Equal(new byte[] { 4, 5 }, input.Read(3, default).ToArray());
        Assert.Equal(new byte[] { 5 }, input.Read(4, default).ToArray());
        Assert.True(input.Read(5, default).IsEmpty);
        Assert.True(input.Read(9, default).IsEmpty);
    }

    [Fact]
    public void StringInputIsUtf16LittleEndian()
    {
        IParseInput input = ParseInputs.FromString("Aé", chunkSize: 4);
        Assert.Equal(InputEncoding.Utf16LE, input.Encoding);
        Assert.Equal(new byte[] { 0x41, 0, 0xe9, 0 }, input.Read(0, default).ToArray());
        Assert.Equal(new byte[] { 0xe9, 0 }, input.Read(2, default).ToArray());
        Assert.True(input.Read(4, default).IsEmpty);
    }

    private void Compare(string script, string name)
    {
        string expected = NativeOracle.OracleLexerScript(script)!;
        string actual = LexerScriptRunner.Run(script);
        if (expected == actual && !actual.Contains("error line", StringComparison.Ordinal))
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
        _output.WriteLine(script);
        Assert.Fail($"{name}: first difference at output line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', managed '{(i < a.Length ? a[i] : "<end>")}'");
    }
}
