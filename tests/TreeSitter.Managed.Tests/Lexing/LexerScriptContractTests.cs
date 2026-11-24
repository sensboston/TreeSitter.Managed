using TreeSitter.Tests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace TreeSitter.Tests.Lexing;

// Contract tests for the interpreter of the lexer scripts and for the generator of the
// random scripts (tests/Oracle/FORMATS.md, section "Lexer script"). Both must behave exactly
// like the oracle, so every case is compared with native output.
public class LexerScriptContractTests
{
    private static readonly string[] Commands =
    [
        "input", "ranges", "reset", "start", "advance", "mark_end", "column", "eof",
        "at_range_start", "finish", "dump",
    ];

    private readonly ITestOutputHelper _output;

    public LexerScriptContractTests(ITestOutputHelper output) => _output = output;

    // Compares one script with the oracle. A mismatch fails with the script and the first
    // different output line.
    private void Compare(string script, string name)
    {
        NativeOracle.EnsureAvailable();
        string expected = NativeOracle.OracleLexerScript(script)!;
        string actual = LexerScriptRunner.Run(script);
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

        _output.WriteLine(script);
        Assert.Fail($"{name}: first difference at output line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', managed '{(i < a.Length ? a[i] : "<end>")}'");
    }

    [OracleFact]
    public void EmptyScriptPrintsNothing()
    {
        Compare(string.Empty, "empty");
        Compare("# only a comment\n\n\t\n", "comments");
    }

    [OracleFact]
    public void EveryCommandWithTheWrongNumberOfArgumentsIsAnError()
    {
        foreach (string command in Commands)
        {
            Compare(command + " 1 2 3 4 5 6\n", command + " with too many arguments");
            Compare(command + " 0 0 0 0\n", command + " with extra arguments");
        }

        Compare("start extra\n", "start extra");
        Compare("dump extra\n", "dump extra");
        Compare("ranges\n", "ranges without arguments");
        Compare("ranges 1\n", "ranges without a range");
        Compare("ranges 0 1:0:0-2:0:2\n", "ranges 0 with a range");
    }

    [OracleFact]
    public void UnknownCommandsAndTooManyArgumentsAreErrors()
    {
        Compare("unknown\n", "unknown command");
        Compare("\n\nunknown\n", "unknown command on line 3");
        Compare("scan\n", "scan");
        Compare("input utf8 0 - " + string.Join(" ", Enumerable.Repeat("0", 300)) + "\n", "too many arguments");
    }

    [OracleFact]
    public void InvalidArgumentsAreErrors()
    {
        string[] scripts =
        [
            "input latin1 0 41\n",
            "input utf8 x 41\n",
            "input utf8 0 4\n",
            "input utf8 0 zz\n",
            "input utf8 0 -\n",
            "input utf8 0\n",
            "reset\n",
            "reset 1:2\n",
            "reset 1:2:3:4\n",
            "reset a:0:0\n",
            "advance\n",
            "advance 2\n",
            "advance x\n",
            "finish\n",
            "finish x\n",
            "finish 4294967296\n",
            "ranges x\n",
            "ranges 1 nonsense\n",
            "ranges 2 0:0:0-1:0:1\n",
        ];
        foreach (string script in scripts)
        {
            Compare(script, script.TrimEnd('\n'));
        }
    }

    [OracleFact]
    public void CommandsOnTheInitialLexerHaveDefinedResults()
    {
        string script = string.Concat(Commands.Select(command => command switch
        {
            "input" => "input utf8 0 -\n",
            "ranges" => "ranges 0\n",
            "reset" => "reset 0:0:0\n",
            "advance" => "advance 0\n",
            "finish" => "finish 0\n",
            _ => command + "\n",
        }));
        Compare(script, "every command on the initial lexer");
        Compare("dump\nstart\ndump\neof\nfinish 0\ndump\n", "start on the empty input");
    }

    [OracleFact]
    public void EveryChunkSizeAndEncodingIsAccepted()
    {
        foreach (string encoding in new[] { "utf8", "utf16le", "utf16be" })
        {
            for (int chunk = 0; chunk <= 6; chunk++)
            {
                Compare($"input {encoding} {chunk} 410a42c3a90a\nreset 0:0:0\nstart\ndump\nadvance 0\ndump\neof\n", $"{encoding} chunk {chunk}");
            }
        }
    }

    [OracleFact]
    public void PositionsBeyondTheInputAndTheDefaultRangeHaveDefinedResults()
    {
        Compare("input utf8 0 41\nreset 100:10:10\ndump\neof\nfinish 0\n", "reset beyond the input");
        Compare("input utf8 0 41\nstart\nadvance 0\nadvance 0\ndump\ncolumn\neof\n", "advance past the end");
        Compare("ranges 1 5:0:5-9:0:9\nreset 0:0:0\ndump\neof\n", "reset before the first range");
        Compare("ranges 1 5:0:5-9:0:9\nreset 7:0:7\ndump\ncolumn\n", "reset into a range");
        Compare("ranges 1 5:0:5-9:0:9\nreset 20:2:2\ndump\nfinish 0\n", "reset after the last range");
        Compare("ranges 1 5:0:5-5:0:5\nreset 0:0:0\ndump\neof\nadvance 0\ndump\n", "an empty range is skipped");
        Compare("ranges 0\nreset 0:0:0\nstart\ndump\n", "the default range");
        Compare("ranges 2 0:0:0-4294967295:0:0 1:1:1-2:2:2\n", "unsorted ranges");
    }

    [OracleFact]
    public void CommentsAndTabsAreIgnored()
    {
        Compare("\t input\tutf8\t0\t41 # the input 41\n\r\nreset\t0:0:0\nstart\ndump # trailing comment\n", "tabs and comments");
        Compare("input utf8 0 41 # 42\nreset 0:0:0\ndump\n", "a comment after hex data");
        Compare("  # indented comment\ninput utf8 0 41\n", "an indented comment");
    }

    [Fact]
    public void GeneratorIsDeterministicAndDiffersBetweenSeeds()
    {
        // The header line of a script holds its seed and the number of commands decides where
        // the script stops, so neither value is a constant here (xUnit cannot see that).
        for (int seed = 5; seed < 7; seed++)
        {
            Assert.Equal(LexerScriptGenerator.Generate(seed, 40), LexerScriptGenerator.Generate(seed, 40));
        }

        for (int count = 40; count < 42; count++)
        {
            Assert.NotEqual(LexerScriptGenerator.Generate(5, count), LexerScriptGenerator.Generate(6, count));
        }
    }

    [OracleFact]
    public void GeneratedScriptsMatchTheOracle()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            Compare(LexerScriptGenerator.Generate(seed, 20 + (seed % 40)), $"generated seed {seed}");
        }
    }

    [OracleFact]
    public void EveryRandomScriptRunsWithoutAnError()
    {
        // The generator only emits valid scripts: a script that stops with "error line" would
        // hide the rest of the commands from the comparison. Both interpreters must agree on it.
        for (int seed = 0; seed < 200; seed++)
        {
            string script = LexerScriptGenerator.Generate(seed, 60);
            string actual = LexerScriptRunner.Run(script);
            if (actual.Contains("error line", StringComparison.Ordinal))
            {
                _output.WriteLine(script);
                Assert.Fail($"seed {seed}: {actual.TrimEnd('\n')}");
            }

            Assert.Equal(NativeOracle.OracleLexerScript(script), actual);
        }
    }

    [OracleFact]
    public void GeneratedScriptsReachEveryCommandAndEveryEncoding()
    {
        string all = string.Concat(Enumerable.Range(0, 100).Select(seed => LexerScriptGenerator.Generate(seed, 60)));
        foreach (string command in Commands)
        {
            Assert.Contains("\n" + command, "\n" + all, StringComparison.Ordinal);
        }

        foreach (string encoding in new[] { "input utf8 ", "input utf16le ", "input utf16be " })
        {
            Assert.Contains("\n" + encoding, "\n" + all, StringComparison.Ordinal);
        }

        foreach (string part in new[] { "\nadvance 0", "\nadvance 1", "\nfinish " })
        {
            Assert.Contains(part, "\n" + all, StringComparison.Ordinal);
        }
    }
}
