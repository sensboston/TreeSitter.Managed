using TreeSitter.Tests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace TreeSitter.Tests.Subtrees;

// Contract tests for M1a-T6b: random valid scripts give the same output in native and managed code.
public class SubtreeRandomTests
{
    private static readonly string[] Languages = ["json", "c", "python"];

    private readonly ITestOutputHelper _output;

    public SubtreeRandomTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void GeneratorIsDeterministic()
    {
        Assert.Equal(SubtreeScriptGenerator.Generate(7, "c", 100), SubtreeScriptGenerator.Generate(7, "c", 100));
        Assert.NotEqual(SubtreeScriptGenerator.Generate(7, "c", 100), SubtreeScriptGenerator.Generate(8, "c", 100));
    }

    [Fact]
    public void GeneratedScriptsUseAllCommands()
    {
        string all = string.Concat(Enumerable.Range(0, 50).Select(seed => SubtreeScriptGenerator.Generate(seed, Languages[seed % 3], 200)));
        foreach (string command in new[] { "leaf ", "error ", "missing ", "extstate ", "node ", "errnode ", "dup ", "release ", "mut ", "clone ",
            "setextra ", "setsym ", "compress ", "summarize ", "edit ", "string ", "compare ", "lastext ", "dump " })
        {
            Assert.Contains("\n" + command, "\n" + all, StringComparison.Ordinal);
        }
    }

    [OracleFact]
    public void RandomScriptsMatchOracle()
    {
        NativeOracle.EnsureAvailable();
        for (int seed = 0; seed < 10_000; seed++)
        {
            string language = Languages[seed % Languages.Length];
            int commands = 20 + (seed * 7919 % 181);
            string script = SubtreeScriptGenerator.Generate(seed, language, commands);
            string expected = NativeOracle.OracleSubtreeScript(script)!;
            string actual = SubtreeScriptRunner.Run(script);
            if (expected == actual && !actual.Contains("error line", StringComparison.Ordinal))
            {
                continue;
            }

            string[] e = expected.Split('\n');
            string[] a = actual.Split('\n');
            int i = 0;
            while (i < e.Length && i < a.Length && e[i] == a[i])
            {
                i++;
            }
            _output.WriteLine($"seed {seed}, language {language}, {commands} commands");
            _output.WriteLine(script);
            Assert.Fail($"seed {seed}: first difference at output line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', managed '{(i < a.Length ? a[i] : "<end>")}'");
        }
    }
}
