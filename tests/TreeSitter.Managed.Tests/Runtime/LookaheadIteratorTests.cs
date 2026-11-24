using System.Globalization;
using TreeSitter.Tests.Subtrees;
using Xunit;

namespace TreeSitter.Tests.Runtime;

/// <summary>
/// The public API of the port of the TSLookaheadIterator section of language.c
/// (Language.CreateLookaheadIterator and LookaheadIterator) without the native oracle: the
/// state checks of the creation and of the two resets, the phases of the iterator and the
/// symbols of a state against the parse tables. The enumeration itself is checked against
/// the native runtime by <see cref="LookaheadOracleTests"/>.
/// </summary>
public class LookaheadIteratorTests
{
    [Fact]
    public void AFreshIteratorIsNotPositioned()
    {
        Language language = ScriptLanguages.Get("json")!;
        LookaheadIterator iterator = language.CreateLookaheadIterator(0)!;
        Assert.Same(language, iterator.Language);

        // C: .symbol = UINT16_MAX and the LookaheadFresh phase.
        Assert.Equal(ushort.MaxValue, iterator.CurrentSymbol);
        Assert.Null(iterator.CurrentSymbolName);

        Assert.True(iterator.Next());
        Assert.NotEqual(ushort.MaxValue, iterator.CurrentSymbol);
        Assert.NotNull(iterator.CurrentSymbolName);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("c")]
    [InlineData("python")]
    public void StatesBeyondTheTableCountHaveNoIterator(string grammar)
    {
        Language language = ScriptLanguages.Get(grammar)!;
        ushort stateCount = checked((ushort)language.StateCount);
        Assert.NotNull(language.CreateLookaheadIterator((ushort)(stateCount - 1)));

        // ts_lookahead_iterator_new: if (state >= self->state_count) return NULL.
        Assert.Null(language.CreateLookaheadIterator(stateCount));
        Assert.Null(language.CreateLookaheadIterator(ushort.MaxValue));
    }

    [Fact]
    public void AFailedResetKeepsTheIteratorAsItWas()
    {
        Language language = ScriptLanguages.Get("json")!;
        LookaheadIterator iterator = language.CreateLookaheadIterator(1)!;
        Assert.True(iterator.Next());
        ushort symbol = iterator.CurrentSymbol;
        string? name = iterator.CurrentSymbolName;
        Assert.NotNull(name);

        // Both resets check the state count first and return false without touching the
        // iterator, so the position and the phase survive.
        Assert.False(iterator.ResetState(checked((ushort)(language.StateCount + 40))));
        Assert.Equal(symbol, iterator.CurrentSymbol);
        Assert.Equal(name, iterator.CurrentSymbolName);

        Assert.False(iterator.Reset(language, ushort.MaxValue));
        Assert.Equal(symbol, iterator.CurrentSymbol);
        Assert.Equal(name, iterator.CurrentSymbolName);

        Assert.True(iterator.ResetState(0));
        Assert.Null(iterator.CurrentSymbolName);
        Assert.Equal(ushort.MaxValue, iterator.CurrentSymbol);
    }

    [Fact]
    public void ResetSwitchesTheLanguageAndRestartsTheEnumeration()
    {
        Language json = ScriptLanguages.Get("json")!;
        Language python = ScriptLanguages.Get("python")!;
        LookaheadIterator iterator = python.CreateLookaheadIterator(100)!;
        Assert.True(iterator.Next());

        // A state python has and json does not: the iterator keeps the old language.
        Assert.False(iterator.Reset(json, 100));
        Assert.Same(python, iterator.Language);

        Assert.True(iterator.Reset(json, 3));
        Assert.Same(json, iterator.Language);

        // The reset iterator is not positioned on a symbol.
        Assert.Null(iterator.CurrentSymbolName);

        List<string> afterReset = Names(iterator);
        List<string> fresh = Names(json.CreateLookaheadIterator(3)!);
        Assert.Equal(fresh, afterReset);
        Assert.NotEmpty(fresh);
    }

    [Fact]
    public void ResetRejectsANullLanguage()
    {
        LookaheadIterator iterator = ScriptLanguages.Get("json")!.CreateLookaheadIterator(0)!;
        Assert.Throws<ArgumentNullException>(() => iterator.Reset(null!, 0));
    }

    [Fact]
    public void TheEndOfAStateIsFinal()
    {
        LookaheadIterator iterator = ScriptLanguages.Get("json")!.CreateLookaheadIterator(0)!;
        ushort last = iterator.Next() ? iterator.CurrentSymbol : ushort.MaxValue;
        while (iterator.Next())
        {
            last = iterator.CurrentSymbol;
        }

        Assert.NotEqual(ushort.MaxValue, last);

        // C: the LookaheadDone phase makes next() return false without touching anything;
        // the name is NULL because the phase is not LookaheadPositioned.
        for (int i = 0; i < 3; i++)
        {
            Assert.False(iterator.Next());
        }

        Assert.Equal(last, iterator.CurrentSymbol);
        Assert.Null(iterator.CurrentSymbolName);
    }

    /// <summary>
    /// For a large state the symbols of the iterator are exactly the symbols with a non-zero
    /// entry of the row of the parse table, in increasing order.
    /// </summary>
    [Fact]
    public void ALargeStateEnumeratesTheRowOfTheParseTable()
    {
        Language language = ScriptLanguages.Get("json")!;
        Assert.True(language.Tables.LargeStateCount > 1);
        for (ushort state = 0; state < language.Tables.LargeStateCount; state++)
        {
            List<ushort> expected = new List<ushort>();
            for (ushort symbol = 0; symbol < language.Tables.SymbolCount; symbol++)
            {
                if (language.Lookup(state, symbol) != 0)
                {
                    expected.Add(symbol);
                }
            }

            Assert.Equal(expected, EnumerateSymbols(language, state));
        }
    }

    /// <summary>
    /// For a small state every symbol with an entry of the table is enumerated (the groups of
    /// the small parse table list the valid symbols, and a group whose value is 0 lists a
    /// symbol that the state has no action for), with no duplicates.
    /// </summary>
    [Fact]
    public void ASmallStateEnumeratesTheGroupsOfTheSmallParseTable()
    {
        foreach (string grammar in new[] { "json", "c" })
        {
            Language language = ScriptLanguages.Get(grammar)!;
            ushort first = checked((ushort)language.Tables.LargeStateCount);
            Assert.True(first < language.StateCount);
            for (ushort state = first; state < first + 20; state++)
            {
                List<ushort> symbols = EnumerateSymbols(language, state);
                Assert.Equal(symbols.Distinct().Count(), symbols.Count);
                for (ushort symbol = 0; symbol < language.Tables.SymbolCount; symbol++)
                {
                    if (language.Lookup(state, symbol) != 0)
                    {
                        Assert.Contains(symbol, symbols);
                    }
                }
            }
        }
    }

    /// <summary>Every symbol the iterator stops on has the name the language gives it.</summary>
    [Fact]
    public void EverySymbolOfEveryStateHasAName()
    {
        foreach (string grammar in new[] { "json", "python" })
        {
            Language language = ScriptLanguages.Get(grammar)!;
            for (ushort state = 0; state < language.StateCount; state++)
            {
                LookaheadIterator iterator = language.CreateLookaheadIterator(state)!;
                while (iterator.Next())
                {
                    string? name = iterator.CurrentSymbolName;
                    Assert.NotNull(name);
                    Assert.Equal(name, language.SymbolName(iterator.CurrentSymbol));
                }
            }
        }
    }

    private static List<ushort> EnumerateSymbols(Language language, ushort state)
    {
        List<ushort> symbols = new List<ushort>();
        LookaheadIterator iterator = language.CreateLookaheadIterator(state)!;
        while (iterator.Next())
        {
            symbols.Add(iterator.CurrentSymbol);
        }

        return symbols;
    }

    private static List<string> Names(LookaheadIterator iterator)
    {
        List<string> names = new List<string>();
        while (iterator.Next())
        {
            names.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}", iterator.CurrentSymbol, iterator.CurrentSymbolName));
        }

        return names;
    }
}
