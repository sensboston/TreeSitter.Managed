using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Subtrees;

// Contract tests for tree commands (tests/Oracle/FORMATS.md, section "Tree commands").
// SubtreeTreeScriptRunner runs the same script over the managed port and the native
// oracle; the outputs must be equal. Generates 3000 scripts per grammar.
public class SubtreeTreeScriptTests
{
    private static readonly string[] Languages = ["json", "c", "python"];

    [Fact]
    public void GeneratorIsDeterministic()
    {
        Assert.Equal(
            SubtreeTreeScriptGenerator.Generate(7, "c", 100),
            SubtreeTreeScriptGenerator.Generate(7, "c", 100));
        Assert.NotEqual(
            SubtreeTreeScriptGenerator.Generate(7, "c", 100),
            SubtreeTreeScriptGenerator.Generate(8, "c", 100));
    }

    [Fact]
    public void GeneratedScriptsUseTreeCommands()
    {
        string all = string.Concat(
            Enumerable.Range(0, 50).Select(seed => SubtreeTreeScriptGenerator.Generate(seed, Languages[seed % 3], 200)));

        foreach (string command in new[]
        {
            "tree ", "root ", "parent ", "child ", "named_child ", "next_sibling ", "prev_sibling ",
            "next_named_sibling ", "prev_named_sibling ", "first_child_for_byte ",
            "first_named_child_for_byte ", "descendant_for_byte_range ",
            "named_descendant_for_byte_range ", "descendant_for_point_range ",
            "named_descendant_for_point_range ", "start_byte ", "start_point ", "end_byte ",
            "end_point ", "type ", "symbol ", "grammar_type ", "grammar_symbol ",
            "child_count ", "named_child_count ", "descendant_count ", "is_null ", "is_named ",
            "is_missing ", "is_extra ", "has_error ", "is_error ", "parse_state ",
            "next_parse_state ", "field_name_for_child ", "field_name_for_named_child ",
            "child_by_field_name ", "child_by_field_id ",
            "tdump", "cwalk", "cinternal", "cdesc ", "ccopy ", "cbyte ", "cpoint ", "setextra ", "missing ",
            "error ", "errnode ", "tranges ", "tincluded", "toffset ", "tcopy", "tdelete"
        })
        {
            Assert.Contains("\n" + command, "\n" + all, StringComparison.Ordinal);
        }
    }

    [OracleFact]
    public void RandomTreeScriptsMatchOracle()
    {
        for (int seed = 0; seed < SubtreeTreeScriptGenerator.CountPerGrammar * Languages.Length; seed++)
        {
            string language = Languages[seed % Languages.Length];
            int commands = 10 + (seed * 7919 % 91);
            string script = SubtreeTreeScriptGenerator.Generate(seed, language, commands);
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

            Assert.Fail(
                $"seed {seed}, language {language}, {commands} commands: first difference at output line {i + 1}: " +
                $"oracle '{(i < e.Length ? e[i] : "<end>")}', managed '{(i < a.Length ? a[i] : "<end>")}'\n" +
                $"Script:\n{script}");
        }
    }
}
