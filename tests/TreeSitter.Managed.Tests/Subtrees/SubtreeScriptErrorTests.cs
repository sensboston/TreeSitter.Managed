using System.Globalization;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// The error messages of the subtree script. The managed interpreter and the
/// native one must print the same text, and they must stop at the first error.
/// The scripts of the Scripts directory must not fail, so the errors are
/// checked here with scripts that are written in the test itself.
/// </summary>
public class SubtreeScriptErrorTests
{
    private const string TreeScript = "lang json\nleaf r9 1 0:0:0 1:0:1 1 1 0 0 0\ntree r9\n";

    public static TheoryData<string> ErrorScripts()
    {
        TheoryData<string> data = new TheoryData<string>
        {
            // No language was selected.
            "leaf r0 1 0:0:0 1:0:1 1 1 0 0 0",
            // The language does not exist.
            "lang no-such-grammar",
            // The number of arguments is wrong.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0",
            "lang json\nerror r0 64 0:0:0 1:0:1 1",
            "lang json\nmissing r0 6 9 2:0:2",
            "lang json\nextstate r0",
            "lang json\nnode r0 19",
            "lang json\nerrnode r0",
            "lang json\ndup r0",
            "lang json\nrelease r0 r1",
            "lang json\nmut r0 r1",
            "lang json\nclone r0",
            "lang json\nsetextra r0",
            "lang json\nsetsym r0",
            "lang json\nsummarize r0 r1",
            "lang json\nstring r0 0 0",
            "lang json\ndump r0 r1",
            // The register is not a register.
            "lang json\ndump x0",
            "lang json\nleaf x0 1 0:0:0 1:0:1 1 1 0 0 0",
            // The register is empty.
            "lang json\ndump r0",
            "lang json\nrelease r0",
            "lang json\nmut r0",
            "lang json\nclone r0 r1",
            "lang json\nsetextra r0 1",
            "lang json\nsetsym r0 10",
            "lang json\nsummarize r0",
            "lang json\nstring r0 0 0 0",
            "lang json\ndup r0 r1",
            // The numbers are not numbers.
            "lang json\nleaf r0 x 0:0:0 1:0:1 1 1 0 0 0",
            "lang json\nleaf r0 1 x 1:0:1 1 1 0 0 0",
            "lang json\nleaf r0 1 0:0:0 x 1 1 0 0 0",
            "lang json\nleaf r0 1 0:0:0 1:0:1 x 1 0 0 0",
            "lang json\nerror r0 x 0:0:0 1:0:1 1 1",
            "lang json\nmissing r0 6 x 2:0:2 1",
            // The symbol does not exist.
            "lang json\nleaf r0 300 0:0:0 1:0:1 1 1 0 0 0",
            "lang json\nmissing r0 300 9 2:0:2 1",
            "lang json\nnode r0 300 0 r1",
            "lang json\nsetsym r0 300",
            // The production does not exist.
            "lang json\nnode r0 19 5 r1",
            // The symbol does not fit into an inline subtree. The C grammar has more
            // than 255 symbols.
            "lang c\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\nsetsym r0 255",
            // The register is not a heap subtree.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\nclone r1 r0",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\nsummarize r0",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\nextstate r0 01",
            // The register has no external tokens.
            "lang json\nleaf r0 1 255:0:255 1:0:1 1 1 0 0 0\nextstate r0 01",
            // The register is a node with children.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 1 0 0\nleaf r1 1 0:0:0 1:0:1 1 1 0 0 0\n" +
                "node r2 19 0 r0 r1\nextstate r2 01",
            // The data is not hexadecimal.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 1 0 0\nextstate r0 0g",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 1 0 0\nextstate r0 010",
            // The command does not exist.
            "lang json\nbogus r0",
            // The commands compress, edit, compare and lastext: the number of arguments.
            "lang json\ncompress r0",
            "lang json\nedit r0",
            "lang json\nedit r0 0:0:0 1:0:1",
            "lang json\ncompare r0",
            "lang json\nlastext",
            // compress needs a language; the others do not.
            "compress r0 1",
            // The register is not a register or is empty.
            "lang json\ncompress x0 1",
            "lang json\ncompress r0 1",
            "lang json\nedit r0 0:0:0 1:0:1 1:0:1",
            "lang json\ncompare r0 r1",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ncompare r0 r1",
            "lang json\nlastext r0",
            "lastext r0",
            // The numbers and the lengths are not numbers and lengths.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ncompress r0 x",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\nedit r0 x 1:0:1 1:0:1",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\nedit r0 0:0:0 1:0:1 y",
            // Tree commands: no language selected.
            "tree r0",
            // Tree commands: no tree created.
            "lang json\nroot r0",
            "lang json\nparent r0",
            "lang json\nchild r0 0",
            "lang json\nstart_byte r0",
            // Tree commands: invalid register.
            "lang json\ntree x0",
            // Tree commands: null register.
            "lang json\ntree r0",
            // Tree commands: wrong number of arguments (after tree is created).
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\nchild r0",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\ndescendant_for_byte_range r0 0",
            // Tree commands: invalid number.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\nchild r0 x",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\ndescendant_for_byte_range r0 x 5",
            // Tree commands: invalid length.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\ndescendant_for_point_range r0 x 0:0:1",
            // Tree commands: next_parse_state wrong number of arguments.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\nnext_parse_state r0 r1",
            // Tree commands: field_name_for_child wrong number of arguments.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\nfield_name_for_child r0",
            // Tree commands: field_name_for_named_child wrong number of arguments.
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\nroot r0\nfield_name_for_named_child r0",
            // The tree and cursor commands without a tree.
            "lang json\ntdump",
            "lang json\ncwalk",
            "lang json\ncinternal",
            "lang json\ncdesc 0",
            "lang json\ncpoint 0:0:0",
            "lang json\ntincluded",
            "lang json\ntcopy",
            "lang json\ntdelete",
            "lang json\ntoffset r0 0:0:0",
            "lang json\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ntree r0\ntdelete\nroot r1",
            // The tree and cursor commands: the arguments.
            TreeScript + "tdump r0",
            TreeScript + "cwalk 1",
            TreeScript + "cinternal 1",
            TreeScript + "cdesc",
            TreeScript + "cdesc x",
            TreeScript + "cbyte 1 2",
            TreeScript + "ccopy x",
            TreeScript + "cpoint x",
            TreeScript + "tincluded 1",
            TreeScript + "tcopy 1",
            TreeScript + "tdelete 1",
            TreeScript + "toffset r0",
            TreeScript + "toffset x0 0:0:0",
            TreeScript + "toffset r0 x",
            "lang json\ntranges 0:0:0",
            "lang json\ntranges -1:0:1",
            "lang json\ntranges 0:0:0-x",
            "lang json\ntranges 0:0:0-1:0:1 0:0:0-1:0:1 0:0:0-1:0:1 0:0:0-1:0:1 0:0:0-1:0:1 " +
                "0:0:0-1:0:1 0:0:0-1:0:1 0:0:0-1:0:1 0:0:0-1:0:1",
        };

        // A line with more than 256 arguments.
        data.Add("lang json\nnode r0 19 0 " +
            string.Join(' ', Enumerable.Range(0, 300).Select(index => "r" + index.ToString(CultureInfo.InvariantCulture))));
        return data;
    }

    [OracleTheory]
    [MemberData(nameof(ErrorScripts))]
    public void ErrorScriptMatchesOracle(string script)
    {
        NativeOracle.EnsureAvailable();
        string expected = NativeOracle.OracleSubtreeScript(script)!;
        string actual = SubtreeScriptRunner.Run(script);
        Assert.Equal(expected, actual);
        Assert.True(actual.Contains("error line", StringComparison.Ordinal));
    }

    [OracleFact]
    public void TheScriptStopsAtTheFirstError()
    {
        NativeOracle.EnsureAvailable();
        const string Script = "lang json\nbogus r0\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ndump r0";
        string expected = NativeOracle.OracleSubtreeScript(Script)!;
        string actual = SubtreeScriptRunner.Run(Script);
        Assert.Equal(expected, actual);
        Assert.Equal("error line 2: unknown command 'bogus'\n", actual);
    }

    [OracleFact]
    public void TheCommentsAreIgnored()
    {
        NativeOracle.EnsureAvailable();
        const string Script = "# a comment\nlang json # the language\nleaf r0 1 0:0:0 1:0:1 1 1 0 0 0\ndump r0";
        string expected = NativeOracle.OracleSubtreeScript(Script)!;
        string actual = SubtreeScriptRunner.Run(Script);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("error line", actual, StringComparison.Ordinal);
    }
}
