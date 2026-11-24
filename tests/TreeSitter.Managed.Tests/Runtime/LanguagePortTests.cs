using TreeSitter.Abi;
using TreeSitter.Generator.Model;
using TreeSitter.Tests.Generator;
using Xunit;

namespace TreeSitter.Tests.Runtime;

// Tests for the branches of the Language port that the oracle dump does not reach:
// the built-in error symbols, the boundaries of the symbol and field tables, missing
// tables and the lookahead iterator of small and large parse states.
public class LanguagePortTests
{
    private static Language Load(string grammar) =>
        Language.FromTables(GrammarModelBuilder.Build(CParserTests.GrammarParserC(grammar)).Tables);

    private static Language Synthetic() => Language.FromTables(SyntheticTables());

    // ---------------------------------------------------------------- built-in symbols

    [Fact]
    public void BuiltinErrorSymbolsHaveTheirOwnNames()
    {
        Language json = Load("Json");

        Assert.Equal("ERROR", json.SymbolName(0xFFFF));
        Assert.Equal("_ERROR", json.SymbolName(0xFFFE));
        Assert.Equal(SymbolType.Regular, json.SymbolType(0xFFFF));
        Assert.Equal(SymbolType.Auxiliary, json.SymbolType(0xFFFE));
        Assert.Equal(0xFFFF, json.PublicSymbol(0xFFFF));
        Assert.Equal(0xFFFF, json.SymbolForName("ERROR", isNamed: true));
        Assert.Equal(0, json.SymbolForName("ERROR", isNamed: false));
    }

    [Fact]
    public void BuiltinErrorSymbolsHaveNoActions()
    {
        Language json = Load("Json");

        json.GetTableEntry(0, 0xFFFF, out TableEntry error);
        Assert.Equal(0u, error.ActionCount);
        Assert.False(error.IsReusable);
        Assert.Empty(json.Actions(0, 0xFFFF).ToArray());
        Assert.False(json.HasReduceAction(0, 0xFFFF));

        json.GetTableEntry(0, 0xFFFE, out TableEntry repeat);
        Assert.Equal(0u, repeat.ActionCount);
        Assert.False(repeat.IsReusable);
        Assert.Empty(json.Actions(0, 0xFFFE).ToArray());
        Assert.False(json.HasReduceAction(0, 0xFFFE));

        Assert.Equal(0, json.NextState(0, 0xFFFF));
        Assert.Equal(0, json.NextState(0, 0xFFFE));
    }

    [Fact]
    public void BuiltinErrorMetadataIsFixed()
    {
        Language json = Load("Json");

        SymbolMetadata error = json.GetSymbolMetadata(0xFFFF);
        Assert.True(error.Visible);
        Assert.True(error.Named);
        Assert.False(error.Supertype);

        SymbolMetadata repeat = json.GetSymbolMetadata(0xFFFE);
        Assert.False(repeat.Visible);
        Assert.False(repeat.Named);
        Assert.False(repeat.Supertype);
    }

    // ---------------------------------------------------------------- boundaries

    [Fact]
    public void SymbolNamesOutsideTheTableAreNull()
    {
        Language json = Load("Json");

        Assert.Equal("json", json.Name);
        Assert.NotNull(json.SymbolName(0));
        Assert.Null(json.SymbolName((ushort)json.SymbolCount));
    }

    [Fact]
    public void FieldNamesOutsideTheTableAreNull()
    {
        Language json = Load("Json");

        // The first field name is always NULL in the C tables.
        Assert.Null(json.FieldNameForId(0));
        Assert.NotNull(json.FieldNameForId(1));
        Assert.Null(json.FieldNameForId((ushort)(json.FieldCount + 1)));
        Assert.Equal(0, json.FieldIdForName(string.Empty));
        Assert.Equal(0, json.FieldIdForName("no_such_field"));
    }

    [Fact]
    public void SymbolNamesRoundTripThroughTheTable()
    {
        Language json = Load("Json");

        for (ushort symbol = 0; symbol < json.SymbolCount; symbol++)
        {
            string? name = json.SymbolName(symbol);
            Assert.NotNull(name);
            SymbolMetadata metadata = json.GetSymbolMetadata(symbol);
            if (!metadata.Visible && !metadata.Supertype)
            {
                // Hidden symbols are never found by their name.
                Assert.Equal(0, json.SymbolForName(name!, metadata.Named));
                continue;
            }

            Assert.Equal(json.PublicSymbol(symbol), json.SymbolForName(name!, metadata.Named));
        }
    }

    [Fact]
    public void SupertypesOfAGrammarAreKnown()
    {
        Language json = Load("Json");

        Assert.NotEmpty(json.Supertypes.ToArray());
        ushort supertype = json.Supertypes[0];
        Assert.NotEmpty(json.Subtypes(supertype).ToArray());
        // A symbol that is not a supertype has no subtypes.
        Assert.Empty(json.Subtypes(0).ToArray());
        // A symbol outside of the table has no subtypes either.
        Assert.Empty(json.Subtypes((ushort)json.SymbolCount).ToArray());
    }

    // ---------------------------------------------------------------- missing tables

    [Fact]
    public void NullTablesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Language.FromTables(null!));
    }

    [Fact]
    public void MissingTablesAreRejected()
    {
        LanguageTables tables = SyntheticTables();
        tables.ParseActions = null;
        ArgumentException error = Assert.Throws<ArgumentException>(() => Language.FromTables(tables));
        Assert.Contains("parse_actions", error.Message, StringComparison.Ordinal);

        tables = SyntheticTables();
        tables.LexModes = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.AliasMap = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.FieldMapEntries = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.SupertypeMapSlices = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.ExternalScannerStates = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.ReservedWords = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.AliasSequences = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.SmallParseTableMap = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.ParseTable = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.SymbolNames = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.PublicSymbolMap = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.SymbolMetadata = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));

        tables = SyntheticTables();
        tables.PrimaryStateIds = null;
        Assert.Throws<ArgumentException>(() => Language.FromTables(tables));
    }

    // ---------------------------------------------------------------- empty language

    [Fact]
    public void AnEmptyLanguageHasNoTables()
    {
        Language language = Language.FromTables(EmptyTables());

        Assert.Equal(0u, language.SymbolCount);
        Assert.Equal(0u, language.StateCount);
        Assert.Equal(0u, language.FieldCount);
        Assert.Null(language.Name);
        Assert.Null(language.SymbolName(0));
        Assert.Equal(0, language.SymbolForName("a", isNamed: true));
        Assert.Null(language.FieldNameForId(0));
        Assert.Empty(language.FieldMap(0).ToArray());
        Assert.Empty(language.Supertypes.ToArray());
        Assert.Empty(language.Subtypes(0).ToArray());

        // Without any external scanner state there are no external tokens.
        Assert.False(language.TryGetEnabledExternalTokens(1, out ReadOnlySpan<bool> tokens));
        Assert.Empty(tokens.ToArray());

        // Without any alias sequence there is none.
        Assert.False(language.TryGetAliasSequence(1, out ReadOnlySpan<ushort> sequence));
        Assert.Empty(sequence.ToArray());
        Assert.Equal(0, language.AliasAt(1, 0));
    }

    // ---------------------------------------------------------------- null names

    [Fact]
    public void NullNamesAreNotFound()
    {
        Language language = Language.FromTables(NullNameTables());

        Assert.Null(language.SymbolName(0));
        Assert.Equal(0, language.SymbolForName("a", isNamed: true));
        Assert.Null(language.FieldNameForId(1));
        Assert.Equal(0, language.FieldIdForName("a"));
    }

    [Fact]
    public void ANameWithATerminatorIsNotFound()
    {
        Language language = Synthetic();

        // The name of the symbol 1 is "a": the query "a\0" is longer.
        Assert.Equal(0, language.SymbolForName("a\0", isNamed: true));
    }


    // ---------------------------------------------------------------- synthetic language

    [Fact]
    public void SyntheticSymbolsAreClassified()
    {
        Language language = Synthetic();

        Assert.Equal(15u, language.AbiVersion);
        Assert.Equal("synthetic", language.Name);
        Assert.Equal(new LanguageMetadata(1, 2, 3), language.Metadata);
        Assert.Equal(5u, language.SymbolCount);
        Assert.Equal(4u, language.StateCount);
        Assert.Equal(3u, language.FieldCount);

        Assert.Equal("end", language.SymbolName(0));
        Assert.Equal("a", language.SymbolName(1));
        Assert.Equal("e", language.SymbolName(4));
        Assert.Null(language.SymbolName(5));

        Assert.Equal(SymbolType.Auxiliary, language.SymbolType(0));
        Assert.Equal(SymbolType.Regular, language.SymbolType(1));
        Assert.Equal(SymbolType.Anonymous, language.SymbolType(2));
        Assert.Equal(SymbolType.Regular, language.SymbolType(3));
        Assert.Equal(SymbolType.Supertype, language.SymbolType(4));
    }

    [Fact]
    public void SyntheticSymbolsAreFoundByName()
    {
        Language language = Synthetic();

        Assert.Equal(1, language.SymbolForName("a", isNamed: true));
        Assert.Equal(2, language.SymbolForName("b", isNamed: false));
        Assert.Equal(3, language.SymbolForName("c", isNamed: true));
        Assert.Equal(0, language.SymbolForName("a", isNamed: false));
        // "end" is invisible and "e" is a hidden supertype: neither is found.
        Assert.Equal(0, language.SymbolForName("end", isNamed: true));
        Assert.Equal(0, language.SymbolForName("e", isNamed: true));
        Assert.Equal(0, language.SymbolForName("no_such_symbol", isNamed: true));
    }

    [Fact]
    public void SyntheticFieldsAreFoundByName()
    {
        Language language = Synthetic();

        Assert.Equal(1, language.FieldIdForName("k"));
        // "j" is smaller than "k" by one byte: the C code gives up at that point.
        Assert.Equal(0, language.FieldIdForName("j"));
        // "key" is the name of the field 3.
        Assert.Equal(3, language.FieldIdForName("key"));
        // "ke" starts with "k" but is shorter than the name of the field 3.
        Assert.Equal(0, language.FieldIdForName("ke"));
        Assert.Equal(0, language.FieldIdForName("k\0"));
        Assert.Equal(0, language.FieldIdForName("zzz"));
    }

    [Fact]
    public void SyntheticAliasesAreResolved()
    {
        Language language = Synthetic();

        // Symbol 1 has an alias group, every other symbol has only its public symbol.
        Assert.Equal(new ushort[] { 10, 11 }, language.AliasesForSymbol(1).ToArray());
        Assert.Equal(new ushort[] { 0 }, language.AliasesForSymbol(0).ToArray());
        Assert.Equal(new ushort[] { 2 }, language.AliasesForSymbol(2).ToArray());
        Assert.Equal(new ushort[] { 4 }, language.AliasesForSymbol(4).ToArray());
    }

    [Fact]
    public void SyntheticAliasSequencesAreResolved()
    {
        Language language = Synthetic();

        Assert.False(language.TryGetAliasSequence(0, out ReadOnlySpan<ushort> none));
        Assert.Empty(none.ToArray());
        Assert.True(language.TryGetAliasSequence(1, out ReadOnlySpan<ushort> sequence));
        Assert.Equal(new ushort[] { 5, 6 }, sequence.ToArray());
        Assert.Equal(5, language.AliasAt(1, 0));
        Assert.Equal(6, language.AliasAt(1, 1));
        Assert.Equal(0, language.AliasAt(0, 0));
    }

    [Fact]
    public void SyntheticFieldMapsAreResolved()
    {
        Language language = Synthetic();

        Assert.Equal(
            new FieldMapEntry[] { new FieldMapEntry(1, 0, false) },
            language.FieldMap(0).ToArray());
        Assert.Equal(
            new FieldMapEntry[] { new FieldMapEntry(2, 0, false) },
            language.FieldMap(1).ToArray());
    }

    [Fact]
    public void SyntheticExternalTokensAreResolved()
    {
        Language language = Synthetic();

        // The external scanner state 0 has no external tokens (NULL in C).
        Assert.False(language.TryGetEnabledExternalTokens(0, out ReadOnlySpan<bool> none));
        Assert.Empty(none.ToArray());
        Assert.True(language.TryGetEnabledExternalTokens(1, out ReadOnlySpan<bool> tokens));
        Assert.Equal(new[] { false, true }, tokens.ToArray());
    }

    [Fact]
    public void SyntheticReservedWordsAreFound()
    {
        Language language = Synthetic();

        // State 0 uses the reserved word set 1, which holds the symbol 7.
        Assert.True(language.IsReservedWord(0, 7));
        Assert.False(language.IsReservedWord(0, 5));
        // The other states have no reserved word set at all.
        Assert.False(language.IsReservedWord(1, 7));
    }

    [Fact]
    public void SyntheticSupertypesAreResolved()
    {
        Language language = Synthetic();

        Assert.Equal(new ushort[] { 3, 4 }, language.Supertypes.ToArray());
        Assert.Equal(new ushort[] { 7, 8 }, language.Subtypes(3).ToArray());
        Assert.Empty(language.Subtypes(4).ToArray());
        Assert.Empty(language.Subtypes(1).ToArray());
    }

    // ---------------------------------------------------------------- parse tables

    [Fact]
    public void SyntheticLookupReadsBothTableKinds()
    {
        Language language = Synthetic();

        // The large states read the parse table.
        Assert.Equal(5, language.Lookup(0, 0));
        Assert.Equal(7, language.Lookup(0, 1));
        Assert.Equal(2, language.Lookup(0, 2));
        Assert.Equal(4, language.Lookup(0, 3));
        Assert.Equal(0, language.Lookup(1, 0));

        // The small states read the groups of the small parse table.
        Assert.Equal(5, language.Lookup(2, 0));
        Assert.Equal(5, language.Lookup(2, 1));
        Assert.Equal(0, language.Lookup(2, 2));
        Assert.Equal(0, language.Lookup(3, 0));

        Assert.True(language.HasActions(0, 0));
        Assert.False(language.HasActions(1, 0));
    }

    [Fact]
    public void SyntheticNextStateFollowsTheActions()
    {
        Language language = Synthetic();

        // The last action of the symbol 0 is a shift with the extra flag: the state
        // stays the same.
        Assert.Equal(0, language.NextState(0, 0));
        // The last action of the symbol 1 is a reduce: there is no next state.
        Assert.Equal(0, language.NextState(0, 1));
        // The last action of the symbol 2 is a plain shift.
        Assert.Equal(9, language.NextState(0, 2));
        // A non-terminal uses the goto table.
        Assert.Equal(4, language.NextState(0, 3));
        // A symbol outside of the table has no next state.
        Assert.Equal(0, language.NextState(0, 4));
        // A state outside of the table has no next state.
        Assert.Equal(0, language.NextState(4, 0));
    }

    [Fact]
    public void SyntheticTableEntriesAreRead()
    {
        Language language = Synthetic();

        language.GetTableEntry(0, 0, out TableEntry extra);
        Assert.Equal(1u, extra.ActionCount);
        Assert.False(extra.IsReusable);
        Assert.Equal(6, extra.ActionsStart);
        Assert.Equal(ParseActionType.Shift, language.Actions(0, 0)[0].Type);
        Assert.True(language.Actions(0, 0)[0].ShiftExtra);

        language.GetTableEntry(0, 1, out TableEntry reduce);
        Assert.Equal(1u, reduce.ActionCount);
        Assert.Equal(8, reduce.ActionsStart);
        Assert.Equal(ParseActionType.Reduce, language.Actions(0, 1)[0].Type);
        Assert.True(language.HasReduceAction(0, 1));
        Assert.False(language.HasReduceAction(0, 0));

        language.GetTableEntry(1, 0, out TableEntry empty);
        Assert.Equal(0u, empty.ActionCount);
        Assert.Empty(language.Actions(1, 0).ToArray());
        Assert.False(language.HasReduceAction(1, 0));
    }

    [Fact]
    public void SyntheticStatesArePrimary()
    {
        Language language = Synthetic();

        Assert.True(language.StateIsPrimary(0));
        Assert.False(language.StateIsPrimary(1));
        Assert.True(language.StateIsPrimary(2));
        Assert.True(language.StateIsPrimary(3));
    }

    // ---------------------------------------------------------------- lookahead iterator

    [Fact]
    public void LookaheadIteratorWalksALargeState()
    {
        Language language = Synthetic();

        LanguageLookaheadIterator iterator = language.Lookaheads(0);
        Assert.True(iterator.Next());
        Assert.Equal(0, iterator.Symbol);
        Assert.Equal(5, iterator.TableValue);
        Assert.Equal(0, iterator.NextState);
        Assert.Equal(ParseActionType.Shift, iterator.Actions[0].Type);

        Assert.True(iterator.Next());
        Assert.Equal(1, iterator.Symbol);
        Assert.Equal(7, iterator.TableValue);
        Assert.Equal(ParseActionType.Reduce, iterator.Actions[0].Type);

        Assert.True(iterator.Next());
        Assert.Equal(2, iterator.Symbol);
        Assert.Equal(2, iterator.TableValue);
        Assert.Equal(ParseActionType.Shift, iterator.Actions[0].Type);

        // The last symbol is a non-terminal: it has a successor state and no actions.
        Assert.True(iterator.Next());
        Assert.Equal(3, iterator.Symbol);
        Assert.Equal(4, iterator.TableValue);
        Assert.Equal(4, iterator.NextState);
        Assert.Empty(iterator.Actions.ToArray());

        Assert.False(iterator.Next());
        Assert.False(iterator.Next());
    }

    [Fact]
    public void LookaheadIteratorStopsAtAnEmptyState()
    {
        Language language = Synthetic();

        // The state 1 has no valid symbol at all.
        LanguageLookaheadIterator iterator = language.Lookaheads(1);
        Assert.False(iterator.Next());
        Assert.False(iterator.Next());
    }

    [Fact]
    public void LookaheadIteratorWalksASmallState()
    {
        Language language = Synthetic();

        // The state 2 is a small state with one group of two symbols.
        LanguageLookaheadIterator iterator = language.Lookaheads(2);
        Assert.True(iterator.Next());
        Assert.Equal(0, iterator.Symbol);
        Assert.Equal(5, iterator.TableValue);
        Assert.Equal(0, iterator.NextState);

        // The second symbol of the group keeps the values of the group.
        Assert.True(iterator.Next());
        Assert.Equal(1, iterator.Symbol);
        Assert.Equal(5, iterator.TableValue);
        Assert.Equal(0, iterator.NextState);

        Assert.False(iterator.Next());
        Assert.False(iterator.Next());
    }

    [Fact]
    public void LookaheadIteratorStopsAtAnEmptySmallState()
    {
        Language language = Synthetic();

        // The state 3 is a small state without any group.
        LanguageLookaheadIterator iterator = language.Lookaheads(3);
        Assert.False(iterator.Next());
        Assert.False(iterator.Next());
    }

    [Fact]
    public void LookaheadIteratorOfAGrammarWalksEveryState()
    {
        Language json = Load("Json");

        for (ushort state = 0; state < json.StateCount; state++)
        {
            LanguageLookaheadIterator iterator = json.Lookaheads(state);
            while (iterator.Next())
            {
                Assert.True(iterator.Symbol < json.SymbolCount);
            }
        }
    }

    [Fact]
    public void AnIteratorWithoutALanguageHasNoActions()
    {
        LanguageLookaheadIterator iterator = default;
        Assert.Empty(iterator.Actions.ToArray());
    }

    // ---------------------------------------------------------------- the synthetic tables

    /// <summary>A language without any symbol, state, field or production.</summary>
    private static LanguageTables EmptyTables() => new LanguageTables
    {
        AbiVersion = 15,
        SymbolCount = 0,
        AliasCount = 0,
        TokenCount = 0,
        ExternalTokenCount = 0,
        StateCount = 0,
        LargeStateCount = 0,
        ProductionIdCount = 1,
        FieldCount = 0,
        MaxAliasSequenceLength = 0,
        KeywordCaptureToken = 0,
        MaxReservedWordSetSize = 0,
        SupertypeCount = 0,
        Name = null,
        Metadata = new LanguageMetadata(0, 0, 0),

        ParseTable = Array.Empty<ushort>(),
        SmallParseTable = Array.Empty<ushort>(),
        SmallParseTableMap = Array.Empty<uint>(),
        ParseActions = Array.Empty<ParseActionEntry>(),
        SymbolNames = Array.Empty<string?>(),
        FieldNames = null,
        FieldMapSlices = Array.Empty<MapSlice>(),
        FieldMapEntries = Array.Empty<FieldMapEntry>(),
        SymbolMetadata = Array.Empty<SymbolMetadata>(),
        PublicSymbolMap = Array.Empty<ushort>(),
        AliasMap = new ushort[] { 0 },
        AliasSequences = null,
        LexModes = Array.Empty<LexerMode>(),
        PrimaryStateIds = Array.Empty<ushort>(),
        ReservedWords = Array.Empty<ushort>(),
        SupertypeSymbols = Array.Empty<ushort>(),
        SupertypeMapSlices = Array.Empty<MapSlice>(),
        SupertypeMapEntries = Array.Empty<ushort>(),
        ExternalScannerStates = null,
    };

    /// <summary>A language whose only symbol and field have no name.</summary>
    private static LanguageTables NullNameTables() => new LanguageTables
    {
        AbiVersion = 15,
        SymbolCount = 1,
        AliasCount = 0,
        TokenCount = 0,
        ExternalTokenCount = 0,
        StateCount = 0,
        LargeStateCount = 0,
        ProductionIdCount = 0,
        FieldCount = 1,
        MaxAliasSequenceLength = 0,
        KeywordCaptureToken = 0,
        MaxReservedWordSetSize = 0,
        SupertypeCount = 0,
        Name = null,
        Metadata = new LanguageMetadata(0, 0, 0),

        ParseTable = Array.Empty<ushort>(),
        SmallParseTable = Array.Empty<ushort>(),
        SmallParseTableMap = Array.Empty<uint>(),
        ParseActions = Array.Empty<ParseActionEntry>(),
        SymbolNames = new string?[] { null },
        FieldNames = new string?[] { null, null },
        FieldMapSlices = Array.Empty<MapSlice>(),
        FieldMapEntries = Array.Empty<FieldMapEntry>(),
        SymbolMetadata = new SymbolMetadata[] { new SymbolMetadata(true, true, false) },
        PublicSymbolMap = new ushort[] { 0 },
        AliasMap = new ushort[] { 0 },
        AliasSequences = Array.Empty<ushort>(),
        LexModes = Array.Empty<LexerMode>(),
        PrimaryStateIds = Array.Empty<ushort>(),
        ReservedWords = Array.Empty<ushort>(),
        SupertypeSymbols = Array.Empty<ushort>(),
        SupertypeMapSlices = Array.Empty<MapSlice>(),
        SupertypeMapEntries = Array.Empty<ushort>(),
        ExternalScannerStates = null,
    };

    private static LanguageTables SyntheticTables() => new LanguageTables
    {
        AbiVersion = 15,
        SymbolCount = 4,
        AliasCount = 1,
        TokenCount = 3,
        ExternalTokenCount = 2,
        StateCount = 4,
        LargeStateCount = 2,
        ProductionIdCount = 2,
        FieldCount = 3,
        MaxAliasSequenceLength = 2,
        KeywordCaptureToken = 0,
        MaxReservedWordSetSize = 3,
        SupertypeCount = 2,
        Name = "synthetic",
        Metadata = new LanguageMetadata(1, 2, 3),

        // Two large states with four symbols each.
        ParseTable = new ushort[] { 5, 7, 2, 4, 0, 0, 0, 0 },

        // The state 2 has one group with the symbols 0 and 1, the state 3 has none.
        SmallParseTable = new ushort[] { 1, 5, 2, 0, 1, 0 },
        SmallParseTableMap = new uint[] { 0, 5 },

        // The entry headers and the actions they belong to.
        ParseActions = new ParseActionEntry[]
        {
            ParseActionEntry.Entry(0, false),
            ParseActionEntry.Entry(0, false),
            ParseActionEntry.Entry(1, false),
            ParseActionEntry.Shift(9, false, true),
            ParseActionEntry.Entry(0, false),
            ParseActionEntry.Entry(1, false),
            ParseActionEntry.Shift(7, true, false),
            ParseActionEntry.Entry(1, false),
            ParseActionEntry.Reduce(1, 2, -2, 1),
        },

        SymbolNames = new string?[] { "end", "a", "b", "c", "e" },
        FieldNames = new string?[] { null, "k", "j", "key" },
        FieldMapSlices = new MapSlice[] { new MapSlice(0, 1), new MapSlice(1, 1) },
        FieldMapEntries = new FieldMapEntry[]
        {
            new FieldMapEntry(1, 0, false),
            new FieldMapEntry(2, 0, false),
        },
        SymbolMetadata = new SymbolMetadata[]
        {
            new SymbolMetadata(false, true, false),
            new SymbolMetadata(true, true, false),
            new SymbolMetadata(true, false, false),
            new SymbolMetadata(true, true, true),
            new SymbolMetadata(false, false, true),
        },
        PublicSymbolMap = new ushort[] { 0, 1, 2, 3, 4 },

        // One alias group for the symbol 1.
        AliasMap = new ushort[] { 1, 2, 10, 11, 0 },
        AliasSequences = new ushort[] { 0, 0, 5, 6 },

        LexModes = new LexerMode[]
        {
            new LexerMode(0, 0, 1),
            new LexerMode(1, 0, 0),
            new LexerMode(2, 0, 0),
            new LexerMode(3, 0, 0),
        },
        PrimaryStateIds = new ushort[] { 0, 0, 2, 3 },
        ReservedWords = new ushort[] { 0, 0, 0, 7, 0, 0 },

        SupertypeSymbols = new ushort[] { 3, 4 },
        SupertypeMapSlices = new MapSlice[]
        {
            new MapSlice(0, 0),
            new MapSlice(0, 0),
            new MapSlice(0, 0),
            new MapSlice(0, 2),
            new MapSlice(0, 0),
        },
        SupertypeMapEntries = new ushort[] { 7, 8 },

        ExternalScannerStates = new bool[] { true, false, false, true },
    };
}
