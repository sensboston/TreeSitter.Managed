using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// The error messages of the stack commands (FORMATS.md, "Stack commands"). The managed
/// interpreter and the native one must print the same text, and both stop at the first error.
/// The scripts of the Scripts directory must not fail, so the errors are checked here.
/// </summary>
public class SubtreeStackScriptTests
{
    public static TheoryData<string> ErrorScripts() => new TheoryData<string>
    {
        "snew x",
        "sdelete",
        "sdump",
        "lang json\nsdump",
        "snew\nspush 0 - 0",
        "snew\nspush 3 - 0 1",
        "snew\nspush x - 0 1",
        "snew\nspush 0 r0 0 1",
        "lang json\nsnew\nleaf r0 10 0:0:0 1:0:1 1 2 0 0 0\nspush 0 r0 2 1",
        "lang json\nsnew\nleaf r0 10 0:0:0 1:0:1 1 2 0 0 0\nspush 0 r0 0 x",
        "lang json\nsnew\nleaf r0 10 0:0:0 1:0:1 1 2 0 0 0\nspush 0 r0 0 70000",
        "snew\nspopcount 0 5000 r0",
        "snew\nspopcount 0 1 r99",
        "snew\nspopcount 0 1 x",
        "snew\nspoppending 0",
        "snew\nspopall 0 r64",
        "snew\nspoperror 0 r0 r1",
        "snew\nsmerge 0 0",
        "snew\nsmerge 0 1",
        "snew\nscanmerge 0 9",
        "snew\nscopy 0 0",
        "snew\nspause 0 -\nscopy 0",
        "snew\nspause 0 -\nspause 0 -",
        "snew\nsresume 0 r0",
        "snew\nsremove 1",
        "snew\nscopy 0\nsrenumber 0 1",
        "snew\nscopy 0\nsrenumber 1 x",
        "snew\nsswap 0",
        "snew\nshalt 5",
        "snew\nspause 0 -\nshalt 0",
        "snew\nspause 0 r0",
        "snew\nssummary 0 x",
        "snew\nsext 0 r0",
        "lang json\nsnew\nleaf r0 10 0:0:0 1:0:1 1 2 0 0 0\nsext 0 r0",
        "lang python\nsnew\nleaf r0 102 0:0:0 1:0:1 1 1 1 0 0\nnode r1 19 0 r0\nsext 0 r1",
        "snew\nsgraph",
        "snew\nsdump extra",
        "lang json\nsnew\nspush 0 - 0 1\nsbogus",
        "lang json\nsnew\nleaf r0 10 0:0:0 1:0:1 1 2 0 0 0\nleaf r1 10 0:0:0 1:0:1 1 2 0 0 0\nleaf r2 10 0:0:0 1:0:1 1 2 0 0 0\nleaf r3 10 0:0:0 1:0:1 1 2 0 0 0\nleaf r4 10 0:0:0 1:0:1 1 2 0 0 0\nspush 0 r0 0 2\nspush 0 r1 0 2\nspush 0 r2 0 2\nspush 0 r3 0 2\nspush 0 r4 0 2\nspopall 0 r60",
        "snew\nsremove 0 0",
        "snew\nscanmerge 0",
        "snew\nspause 0 -\nsresume 0 r0 r1",
        "snew\nspopcount 0 1",
    };

    [OracleTheory]
    [MemberData(nameof(ErrorScripts))]
    public void ErrorScriptMatchesOracle(string script)
    {
        NativeOracle.EnsureAvailable();
        string expected = NativeOracle.OracleSubtreeScript(script)!;
        string actual = SubtreeScriptRunner.Run(script);
        Assert.Equal(expected, actual);
        Assert.Contains("error line", actual, StringComparison.Ordinal);
    }

    [OracleFact]
    public void SnewReplacesTheStack()
    {
        NativeOracle.EnsureAvailable();
        const string Script = "lang json\nsnew\nleaf r0 10 0:0:0 1:0:1 1 2 0 0 0\nspush 0 r0 0 2\nsnew\nsdump\nsdelete";
        string expected = NativeOracle.OracleSubtreeScript(Script)!;
        string actual = SubtreeScriptRunner.Run(Script);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("error line", actual, StringComparison.Ordinal);
    }

    [OracleFact]
    public void TheScriptStopsAtTheFirstStackError()
    {
        NativeOracle.EnsureAvailable();
        const string Script = "lang json\nsnew\nsremove 1\nsdump";
        string expected = NativeOracle.OracleSubtreeScript(Script)!;
        string actual = SubtreeScriptRunner.Run(Script);
        Assert.Equal(expected, actual);
        Assert.Equal("error line 3: invalid version '1'\n", actual);
    }
}
