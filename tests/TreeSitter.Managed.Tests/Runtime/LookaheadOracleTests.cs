using System.Globalization;
using System.Text;
using TreeSitter.Tests.Oracle;
using TreeSitter.Tests.Subtrees;
using Xunit;

namespace TreeSitter.Tests.Runtime;

/// <summary>
/// The lookahead iterator port (LookaheadIterator.cs and LanguageLookaheadIterator of
/// Language.cs) against the native ts_lookahead_iterator_* functions of language.c: the same
/// commands must print the same text (tests/Oracle/FORMATS.md, section "oracle_lookahead").
/// </summary>
public class LookaheadOracleTests
{
    private static readonly string[] GrammarNames = { "json", "c", "python" };

    /// <summary>
    /// Every state of every grammar is created and enumerated symbol by symbol with "all",
    /// and the states at or above the state count produce no iterator.
    /// </summary>
    /// <param name="grammar">The grammar name the oracle and the port both know.</param>
    [OracleTheory]
    [InlineData("json")]
    [InlineData("c")]
    [InlineData("python")]
    public void EveryStateMatchesOracle(string grammar)
    {
        NativeOracle.EnsureAvailable();
        Language language = ScriptLanguages.Get(grammar)!;
        StringBuilder commands = new StringBuilder();
        for (uint state = 0; state < language.StateCount; state++)
        {
            commands.Append(CultureInfo.InvariantCulture, $"new {state}\nall\n");
        }

        // ts_lookahead_iterator_new returns NULL for a state that is not below the state
        // count; the state of a command is a uint32 truncated to TSStateId, so 65536 is 0.
        for (uint state = language.StateCount; state < language.StateCount + 8; state++)
        {
            commands.Append(CultureInfo.InvariantCulture, $"new {state}\nnew 0\ncount\n");
        }

        commands.Append("new 65535\nnew 65536\ncount\nnew 65600\nnew 0\ncount\nnew 70000\nnew 4\ncount\n");
        AssertSame(grammar, commands.ToString(), out long symbols);
        Assert.True(symbols > 100, $"only {symbols} symbol lines for {grammar}");
    }

    /// <summary>
    /// Random sequences of new / reset / reset_state / next / all / peek / delete commands,
    /// each sequence starting from a valid state, so that resets land on a live iterator and
    /// the failing ones (a state of the other grammar that is too large, a delete before a
    /// new) are exercised too.
    /// </summary>
    /// <param name="grammar">The grammar name the oracle and the port both know.</param>
    /// <param name="count">The number of random sequences.</param>
    [OracleTheory]
    [InlineData("json", 600)]
    [InlineData("c", 200)]
    [InlineData("python", 200)]
    public void RandomCommandSequencesMatchOracle(string grammar, int count)
    {
        NativeOracle.EnsureAvailable();
        Language language = ScriptLanguages.Get(grammar)!;
        long symbols = 0;
        int noIterator = 0;
        int failedResets = 0;
        int unknownCommands = 0;

        for (int seed = 0; seed < count; seed++)
        {
            Random random = new Random(seed);
            StringBuilder commands = new StringBuilder();
            commands.Append(CultureInfo.InvariantCulture, $"new {random.Next((int)language.StateCount)}\n");
            int lines = random.Next(6, 18);
            for (int line = 0; line < lines; line++)
            {
                switch (random.Next(11))
                {
                    case 0:
                        commands.Append(CultureInfo.InvariantCulture, $"new {random.Next((int)language.StateCount + 40)}\n");
                        break;
                    case 1:
                        commands.Append(CultureInfo.InvariantCulture, $"reset_state {random.Next(70000)}\n");
                        break;
                    case 2:
                        commands.Append(CultureInfo.InvariantCulture, $"reset_state {random.Next((int)language.StateCount)}\n");
                        break;
                    case 3:
                        commands.Append(CultureInfo.InvariantCulture, $"reset {GrammarNames[random.Next(GrammarNames.Length)]} {random.Next(60)}\n");
                        break;
                    case 4:
                        commands.Append(CultureInfo.InvariantCulture, $"reset {GrammarNames[random.Next(GrammarNames.Length)]} {random.Next((int)language.StateCount)}\n");
                        break;
                    case 5:
                        commands.Append("next\n");
                        break;
                    case 6:
                        commands.Append("peek\n");
                        break;
                    case 7:
                        commands.Append("all\n");
                        break;
                    case 8:
                        commands.Append("count\n");
                        break;
                    case 9:
                        commands.Append("language\n");
                        break;
                    default:
                        commands.Append("delete\n");
                        break;
                }
            }

            // An unknown command ends the script, so it only goes at the end of a sequence.
            if (random.Next(3) == 0)
            {
                commands.Append(CultureInfo.InvariantCulture, $"unknown {random.Next(4)}\n");
                unknownCommands++;
            }

            string output = AssertSame(grammar, commands.ToString(), out long seedSymbols);
            symbols += seedSymbols;
            noIterator += CountLines(output, "error = no iterator");
            failedResets +=
                CountLines(output, "new = 0") +
                CountLines(output, "reset = 0") +
                CountLines(output, "reset_state = 0");
        }

        Assert.True(symbols > count * 2, $"only {symbols} symbol lines");
        Assert.True(noIterator > 0, "no sequence lost its iterator");
        Assert.True(failedResets > 0, "no reset or new ever failed");
        Assert.True(unknownCommands > 0, "no unknown command was generated");
    }

    /// <summary>The arguments the shim reads with strtoul, the words it drops and the lines it stops on.</summary>
    [OracleTheory]
    [InlineData("json")]
    [InlineData("python")]
    public void MalformedCommandsMatchOracle(string grammar)
    {
        NativeOracle.EnsureAvailable();
        string[] scripts =
        {
            "new abc\ncount\n",
            "new 3xyz\ncount\n",
            "new 0 99 100\npeek\n",
            "new\npeek\n",
            "   \nnew 0\ncount\n",
            "\t\nnew 0\ncount\n",
            "new 0\n\nnew 3\ncount\n",
            "new 0\nreset nosuch 1\nnew 1\ncount\n",
            "reset json 1\n",
            "reset json 65535\ncount\n",
            "peek\nnext\nall\ncount\nlanguage\ndelete\n",
            "delete\ndelete\nnew 0\ndelete\ncount\n",
            "new 1\nreset_state 99999\npeek\nreset_state 0\npeek\nall\n",
            "new 0\nnext\nnext\nall\ncount\nnext\npeek\nnew 0\nlanguage\n",
            "garbage\n",
            "new 0\nnew 0\nnew 0\ncount\n",
        };

        foreach (string script in scripts)
        {
            AssertSame(grammar, script, out _);
        }
    }

    /// <summary>An unknown grammar name: both sides print the single error line.</summary>
    [OracleFact]
    public void UnknownGrammarMatchesOracle()
    {
        NativeOracle.EnsureAvailable();
        foreach (string commands in new[] { "new 0\ncount\n", "peek\n", "" })
        {
            string expected = NativeOracle.OracleLookahead("nosuchgrammar", commands);
            Assert.Equal(expected, LookaheadRunner.Run("nosuchgrammar", commands));
        }
    }

    private static int CountLines(string text, string prefix) =>
        text.Split('\n').Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Runs the commands natively and with the port and compares the two texts line by line,
    /// reporting the first difference. Returns the oracle text and the number of printed
    /// symbol lines.
    /// </summary>
    private static string AssertSame(string grammar, string commands, out long symbols)
    {
        string expected = NativeOracle.OracleLookahead(grammar, commands);
        string actual = LookaheadRunner.Run(grammar, commands);
        symbols = CountLines(expected, "symbol ");

        if (expected == actual)
        {
            return expected;
        }

        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
        {
            i++;
        }

        Assert.Fail(
            $"{grammar}: first difference at line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', " +
            $"managed '{(i < a.Length ? a[i] : "<end>")}'\ncommands:\n{commands}");
        return expected;
    }
}
