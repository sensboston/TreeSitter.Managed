using TreeSitter.Abi;
using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Emit;
using TreeSitter.Generator.Lex;
using TreeSitter.Generator.Model;
using Xunit;

namespace TreeSitter.Tests.Generator;

public class LexEmitterTests
{
    private static readonly SourcePos P = new(1, 1);

    private static readonly Dictionary<string, long> Symbols = new(StringComparer.Ordinal)
    {
        ["sym_a"] = 3,
        ["big"] = 70000,
    };

    private static readonly Dictionary<string, CharacterRange[]> Sets = new(StringComparer.Ordinal)
    {
        ["set_1"] = [new('a', 'z'), new(0x100, 0x200)],
        ["negative"] = [new(-1, 5)],
    };

    private static LexProgram Program(params LexStatement[] statements) =>
        new("ts_lex", [new LexState(P, 0, statements)]);

    private static string Body(params LexStatement[] statements) =>
        string.Join("\n", LexEmitter.Emit(Program(statements), "Lex", Symbols, Sets).MethodLines);

    private static LexIf If(LexCondition condition, int target = 1, bool skip = false) => new(P, condition, target, skip);

    private static LexLookahead L => new(P);

    private static LexConstant C(int value) => new(P, value);

    [Theory]
    [InlineData("Json")]
    [InlineData("C")]
    [InlineData("Python")]
    public void EmitsEveryLexFunctionOfTheGrammars(string grammar)
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC(grammar));
        foreach ((LexProgram? program, string name) in new[] { (model.Lex, "Lex"), (model.KeywordLex, "LexKeywords") })
        {
            if (program is null)
            {
                continue;
            }
            LexEmitResult result = LexEmitter.Emit(program, name, model.SymbolValues, model.CharacterSets);
            int cases = result.MethodLines.Count(l => l.TrimStart().StartsWith("case ", StringComparison.Ordinal));
            Assert.Equal(program.States.Count, cases);
            int maps = program.States.SelectMany(s => s.Statements).OfType<LexAdvanceMap>().Count();
            Assert.Equal(maps, result.Data.Count);
            int accepts = program.States.SelectMany(s => s.Statements).OfType<LexAccept>().Count();
            Assert.Equal(accepts, result.MethodLines.Count(l => l.Contains("lexer.MarkEnd();", StringComparison.Ordinal)));
        }

        string code = new LiteralEmitter().Emit(model, new EmitOptions(grammar, "X", null));
        Assert.DoesNotContain("NotSupportedException", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterSetsAreEmittedOncePerGrammar()
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("C"));
        IReadOnlyList<LexData> sets = LexEmitter.EmitCharacterSets(
            new[] { model.Lex!, model.KeywordLex! }, model.CharacterSets);
        Assert.NotEmpty(sets);
        Assert.Equal(sets.Count, sets.Select(s => s.Name).Distinct().Count());
        foreach (LexData set in sets)
        {
            string name = set.Name["CharacterSet_".Length..];
            Assert.Equal(model.CharacterSets[name].Length * 2, set.Values.Count);
        }
    }

    [Fact]
    public void BuiltinEndSymbolResolvesToZero()
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("Json"));
        Assert.Equal(0, model.SymbolValues["ts_builtin_sym_end"]);
    }

    [Fact]
    public void StatementsFollowTheParserHMacros()
    {
        string body = Body(
            If(new LexEof(P), 5),
            If(new LexCompare(P, "==", L, C(' ')), 6, skip: true),
            new LexAdvanceMap(P, [97, 98], [7, 8]),
            new LexAccept(P, "sym_a"));
        Assert.Contains("if (eof)\n", body, StringComparison.Ordinal);
        Assert.Contains("    state = 5;\n", body, StringComparison.Ordinal);
        Assert.Contains("if (lookahead == ' ')\n", body, StringComparison.Ordinal);
        Assert.Contains("skip = true;\n", body, StringComparison.Ordinal);
        Assert.Contains("LexHelpers.AdvanceMap(LexMap0, lookahead, ref state)", body, StringComparison.Ordinal);
        Assert.Contains("lexer.ResultSymbol = 3; // sym_a\n", body, StringComparison.Ordinal);
        Assert.Contains("default:\n            return false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionsKeepCTypes()
    {
        Assert.Contains("if (lookahead != 0)", Body(If(L)), StringComparison.Ordinal);
        Assert.Contains("if (!(lookahead != 0))", Body(If(new LexNot(P, L))), StringComparison.Ordinal);
        Assert.Contains("if ((eof ? 1 : 0) == 0)", Body(If(new LexCompare(P, "==", new LexEof(P), C(0)))), StringComparison.Ordinal);
        Assert.Contains(
            "if ((lookahead != 0) || eof)",
            Body(If(new LexLogical(P, false, L, new LexEof(P)))),
            StringComparison.Ordinal);
        Assert.Contains(
            "if (LexHelpers.SetContains(CharacterSet_set_1, 2, lookahead))",
            Body(If(new LexSetContains(P, "set_1", 2))),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('a', "'a'")]
    [InlineData('\'', "39")]
    [InlineData('\\', "92")]
    [InlineData('\n', "10")]
    [InlineData(0x7F, "127")]
    [InlineData(0x2060, "8288")]
    public void ConstantsAreCharsOnlyForPlainAscii(int value, string expected)
    {
        Assert.Contains($"if (lookahead == {expected})", Body(If(new LexCompare(P, "==", L, C(value)))), StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesOutsideTheCTypesAreErrors()
    {
        Assert.Throws<InvalidOperationException>(() => Body(If(L, 70000)));
        Assert.Throws<InvalidOperationException>(() => Body(If(L, -1)));
        Assert.Throws<InvalidOperationException>(() => Body(new LexAdvanceMap(P, [70000], [1])));
        Assert.Throws<InvalidOperationException>(() => Body(new LexAdvanceMap(P, [1], [70000])));
        Assert.Throws<InvalidOperationException>(() => Body(new LexAccept(P, "big")));
        Assert.Throws<InvalidOperationException>(() => Body(new LexAccept(P, "unknown")));
        Assert.Throws<InvalidOperationException>(() => Body(If(new LexSetContains(P, "missing", 2))));
        Assert.Throws<InvalidOperationException>(() => Body(If(new LexSetContains(P, "set_1", 3))));
        Assert.Throws<InvalidOperationException>(() => LexEmitter.EmitCharacterSets(
            [Program(If(new LexSetContains(P, "negative", 1)))], Sets));
    }

    [Fact]
    public void SetContainsMatchesLinearSearch()
    {
        GrammarModel model = GrammarModelBuilder.Build(CParserTests.GrammarParserC("C"));
        var random = new Random(1234);
        foreach (CharacterRange[] ranges in model.CharacterSets.Values)
        {
            int[] pairs = ranges.SelectMany(r => new[] { r.Start, r.End }).ToArray();
            var probes = new List<int> { -1, 0, 0x10FFFF, 0x110000 };
            foreach (CharacterRange range in ranges)
            {
                probes.AddRange([range.Start - 1, range.Start, range.End, range.End + 1]);
            }
            for (int i = 0; i < 2000; i++)
            {
                probes.Add(random.Next(-1, 0x110000));
            }
            foreach (int probe in probes)
            {
                bool expected = ranges.Any(r => probe >= r.Start && probe <= r.End);
                Assert.Equal(expected, LexHelpers.SetContains(pairs, (uint)ranges.Length, probe));
            }
        }
    }

    [Fact]
    public void AdvanceMapTakesTheFirstMatch()
    {
        ushort[] map = [97, 1, 98, 2, 97, 3];
        ushort state = 9;
        Assert.True(LexHelpers.AdvanceMap(map, 97, ref state));
        Assert.Equal(1, state);
        Assert.True(LexHelpers.AdvanceMap(map, 98, ref state));
        Assert.Equal(2, state);
        Assert.False(LexHelpers.AdvanceMap(map, 99, ref state));
        Assert.Equal(2, state);
        Assert.False(LexHelpers.AdvanceMap(map, -1, ref state));
    }
}
