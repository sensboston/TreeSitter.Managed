using TreeSitter.Subtrees;
using TreeSitter.Tests.Generator;
using TreeSitter.Tests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace TreeSitter.Tests.Subtrees;

// Contract tests for Subtree (task M1a-T6a). The script format is described in
// tests/Oracle/FORMATS.md, section "Subtree script". SubtreeScriptRunner (managed) and
// oracle_subtree_script (native shim) run the same script; the outputs must be equal.
public class SubtreeTests
{
    private readonly ITestOutputHelper _output;

    public SubtreeTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string> ScriptFiles()
    {
        var data = new TheoryData<string>();
        string dir = Path.Combine(Path.GetDirectoryName(CParserTests.GrammarParserC("Json"))!, "..", "..", "..", "..",
            "tests", "TreeSitter.Managed.Tests", "Subtrees", "Scripts");
        foreach (string file in Directory.GetFiles(dir, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
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
        string dir = Path.Combine(Path.GetDirectoryName(CParserTests.GrammarParserC("Json"))!, "..", "..", "..", "..",
            "tests", "TreeSitter.Managed.Tests", "Subtrees", "Scripts");
        string script = File.ReadAllText(Path.Combine(dir, fileName)).Replace("\r\n", "\n", StringComparison.Ordinal);
        string expected = NativeOracle.OracleSubtreeScript(script)!;
        string actual = SubtreeScriptRunner.Run(script);
        if (expected != actual)
        {
            string[] e = expected.Split('\n');
            string[] a = actual.Split('\n');
            int i = 0;
            while (i < e.Length && i < a.Length && e[i] == a[i])
            {
                i++;
            }
            for (int k = Math.Max(0, i - 3); k <= i + 3; k++)
            {
                _output.WriteLine($"{k + 1,5} oracle : {(k < e.Length ? e[k] : "<end>")}");
                _output.WriteLine($"{k + 1,5} managed: {(k < a.Length ? a[k] : "<end>")}");
            }
            Assert.Fail($"{fileName}: first difference at line {i + 1}");
        }
        Assert.DoesNotContain("error line", actual, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, 7, 1, 1, 0, 0, 1, 1, 0, 3, 2, 4, 9, 6)]
    [InlineData(255, 65535, 0, 0, 1, 1, 0, 0, 1, 255, 15, 15, 254, 254)]
    public void InlineLayoutRoundTrips(
        int symbol, int parseState, int visible, int named, int extra, int hasChanges, int isMissing, int isKeyword,
        int dependsOnColumn, int paddingColumns, int paddingRows, int lookaheadBytes, int paddingBytes, int sizeBytes)
    {
        Subtree s = Subtree.NewInline(
            (ushort)symbol, (ushort)parseState, visible != 0, named != 0, extra != 0, hasChanges != 0,
            isMissing != 0, isKeyword != 0, dependsOnColumn != 0, (byte)paddingColumns, (byte)paddingRows,
            (byte)lookaheadBytes, (byte)paddingBytes, (byte)sizeBytes);
        Assert.True(s.IsInline);
        Assert.False(s.IsNull);
        Assert.Equal(symbol, s.Symbol);
        Assert.Equal(parseState, s.ParseState);
        Assert.Equal(visible != 0, s.Visible);
        Assert.Equal(named != 0, s.Named);
        Assert.Equal(extra != 0, s.Extra);
        Assert.Equal(hasChanges != 0, s.HasChanges);
        Assert.Equal(isMissing != 0, s.IsMissing);
        Assert.Equal(isKeyword != 0, s.IsKeyword);
        Assert.Equal(dependsOnColumn != 0, s.DependsOnColumn);
        Assert.Equal((uint)lookaheadBytes, s.LookaheadBytes);
        Assert.Equal(new Length((uint)paddingBytes, new Point((uint)paddingRows, (uint)paddingColumns)), s.Padding);
        // ts_subtree_size of an inline subtree: {size_bytes, {0, size_bytes}}
        Assert.Equal(new Length((uint)sizeBytes, new Point(0, (uint)sizeBytes)), s.Size);
        Assert.Equal(0u, s.ChildCount);
        Assert.True(s.Children.IsEmpty);
        Subtree flipped = s.WithInlineExtra(extra == 0);
        Assert.Equal(extra == 0, flipped.Extra);
        Assert.Equal(symbol, flipped.Symbol);
    }

    [Fact]
    public void NullSubtree()
    {
        Assert.True(Subtree.Null.IsNull);
        Assert.False(Subtree.Null.IsInline);
    }
}
