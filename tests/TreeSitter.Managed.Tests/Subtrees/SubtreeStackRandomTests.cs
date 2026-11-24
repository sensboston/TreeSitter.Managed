using System.Linq;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// The random stack scripts (StackScriptGenerator) must run on the native oracle and on the managed
/// port with the same output. The test also checks that every script runs to its end: the generator
/// only calls the commands that the C code accepts.
/// </summary>
public class SubtreeStackRandomTests
{
    private const int ScriptCount = 5000;

    private static readonly string[] Commands =
    {
        "leaf", "error", "missing", "spush", "spopcount", "spopall", "spoperror", "smerge", "scanmerge",
        "scopy", "spause", "sresume", "ssummary", "shalt", "sext", "sswap", "srenumber", "sremove", "sgraph",
        "sclear", "sdump",
    };

    [Fact]
    public void GeneratorIsDeterministic()
    {
        Assert.Equal(StackScriptGenerator.Generate(7), StackScriptGenerator.Generate(7));
        Assert.NotEqual(StackScriptGenerator.Generate(7), StackScriptGenerator.Generate(8));
    }

    [Fact]
    public void GeneratedScriptsUseTheStackCommands()
    {
        string all = "\n" + string.Concat(Enumerable.Range(0, 300).Select(StackScriptGenerator.Generate));
        foreach (string command in Commands)
        {
            Assert.Contains("\n" + command, all, StringComparison.Ordinal);
        }
    }

    [OracleFact]
    public void RandomStackScriptsMatchOracle()
    {
        NativeOracle.EnsureAvailable();
        int executed = 0;
        for (int seed = 0; seed < ScriptCount; seed++)
        {
            string script = StackScriptGenerator.Generate(seed);
            string expected = NativeOracle.OracleSubtreeScript(script)!;
            string actual = SubtreeScriptRunner.Run(script);
            if (expected.Contains("error line", StringComparison.Ordinal)
                || !expected.Contains("versions = ", StringComparison.Ordinal))
            {
                Assert.Fail($"seed {seed}: the script did not run to its end:\n{expected}\n{script}");
            }

            if (expected != actual)
            {
                Assert.Fail($"seed {seed}: the managed output differs: {FirstDifference(expected, actual)}\n{script}");
            }

            executed++;
        }

        Assert.Equal(ScriptCount, executed);
    }

    private static string FirstDifference(string expected, string actual)
    {
        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
        {
            i++;
        }

        return $"line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', managed '{(i < a.Length ? a[i] : "<end>")}'";
    }
}
