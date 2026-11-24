using TreeSitter.Abi;
using TreeSitter.Generator.Dump;
using Xunit;

namespace TreeSitter.Tests.Generator;

// Extra tests for the M1a-T4c dumper: the branches that the oracle grammars (all ABI 15,
// with a non-NULL alias map) cannot reach. The main contract - byte equality with
// oracle_dump_tables - is in TableDumperTests.
public class TableDumperCoverageTests
{
    // The smallest table with one large state, one lex mode and no productions.
    private static LanguageTables MinimalTables(uint abiVersion) => new()
    {
        AbiVersion = abiVersion,
        SymbolCount = 2,
        StateCount = 1,
        LargeStateCount = 1,
        LexModes = new[] { new LexerMode(7, 0, 3) },
        ParseTable = new ushort[] { 0, 0 },
        ParseActions = new[] { ParseActionEntry.Entry(0, false) },
        SymbolNames = new[] { "end", (string?)null },
        SymbolMetadata = new[] { new SymbolMetadata(false, true, false), new SymbolMetadata(true, false, false) },
        PublicSymbolMap = new ushort[] { 0, 1 },
        FieldNames = new string?[] { null },
    };

    [Fact]
    public void DumpRejectsNullTables() =>
        Assert.Throws<ArgumentNullException>("tables", () => TableDumper.Dump(null!));

    [Fact]
    public void DumpOfAbiBelow15PrintsNullMetadataAndZeroReservedWordSetId()
    {
        // ts_language_lex_mode_for_state reads TSLexMode (no reserved_word_set_id) below
        // ABI 15 and ts_language_metadata returns NULL.
        string expected = string.Join(
            "\n",
            "abi_version = 14",
            "symbol_count = 2",
            "alias_count = 0",
            "token_count = 0",
            "external_token_count = 0",
            "state_count = 1",
            "large_state_count = 1",
            "production_id_count = 0",
            "field_count = 0",
            "max_alias_sequence_length = 0",
            "keyword_capture_token = 0",
            "max_reserved_word_set_size = 0",
            "lex_fn = null",
            "keyword_lex_fn = null",
            "external_scanner.create = null",
            "external_scanner.destroy = null",
            "external_scanner.scan = null",
            "external_scanner.serialize = null",
            "external_scanner.deserialize = null",
            "external_scanner.states = null",
            "external_scanner.symbol_map = null",
            "primary_state_ids = null",
            "name = null",
            "metadata = null",
            "reserved_words = null",
            "symbol_names[0] = \"end\"",
            "symbol_names[1] = null",
            "symbol_metadata[0] = visible=0 named=1 supertype=0",
            "symbol_metadata[1] = visible=1 named=0 supertype=0",
            "public_symbol_map[0] = 0",
            "public_symbol_map[1] = 1",
            "field_map_slices = null",
            "field_names[0] = null",
            "lex_modes[0] = lex_state=7 external_lex_state=0 reserved_word_set_id=0",
            "parse_table[0] = 0",
            "parse_table[1] = 0") + "\n";

        Assert.Equal(expected, TableDumper.Dump(MinimalTables(14)));
    }

    [Fact]
    public void DumpOfAbi15PrintsMetadataAndReservedWordSetId()
    {
        string expected = string.Join(
            "\n",
            "abi_version = 15",
            "symbol_count = 2",
            "alias_count = 0",
            "token_count = 0",
            "external_token_count = 0",
            "state_count = 1",
            "large_state_count = 1",
            "production_id_count = 0",
            "field_count = 0",
            "max_alias_sequence_length = 0",
            "keyword_capture_token = 0",
            "max_reserved_word_set_size = 0",
            "lex_fn = null",
            "keyword_lex_fn = null",
            "external_scanner.create = null",
            "external_scanner.destroy = null",
            "external_scanner.scan = null",
            "external_scanner.serialize = null",
            "external_scanner.deserialize = null",
            "external_scanner.states = null",
            "external_scanner.symbol_map = null",
            "primary_state_ids = null",
            "name = null",
            "metadata = major=0 minor=0 patch=0",
            "reserved_words = null",
            "symbol_names[0] = \"end\"",
            "symbol_names[1] = null",
            "symbol_metadata[0] = visible=0 named=1 supertype=0",
            "symbol_metadata[1] = visible=1 named=0 supertype=0",
            "public_symbol_map[0] = 0",
            "public_symbol_map[1] = 1",
            "field_map_slices = null",
            "field_names[0] = null",
            "lex_modes[0] = lex_state=7 external_lex_state=0 reserved_word_set_id=3",
            "parse_table[0] = 0",
            "parse_table[1] = 0") + "\n";

        Assert.Equal(expected, TableDumper.Dump(MinimalTables(15)));
    }

    [Fact]
    public void DumpReportsAParseActionWalkThatOvershootsTheDerivedLength()
    {
        // The walk of parse_actions must end exactly at the derived length; a header inside
        // the walk whose count is longer than any lookup accounts for is a layout error
        // (dump_tables prints the same line).
        LanguageTables tables = MinimalTables(15);
        tables.TokenCount = 2;
        tables.ParseTable = new ushort[] { 0, 1 }; // lookups give 0 (count 2) and 1 (count 2)
        tables.ParseActions = new[]
        {
            ParseActionEntry.Entry(2, false),
            ParseActionEntry.Accept(),
            ParseActionEntry.Accept(),
            ParseActionEntry.Entry(5, false),
            ParseActionEntry.Accept(),
            ParseActionEntry.Accept(),
            ParseActionEntry.Accept(),
            ParseActionEntry.Accept(),
            ParseActionEntry.Accept(),
        };

        string text = TableDumper.Dump(tables);

        Assert.Contains(
            "parse_actions[0] = entry count=2 reusable=0\n" +
            "parse_actions[1] = accept\n" +
            "parse_actions[2] = accept\n" +
            "parse_actions[3] = entry count=5 reusable=0\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "parse_actions = <layout error: walk ended at 9, expected 4>\n",
            text,
            StringComparison.Ordinal);
    }
}
