// Prints LanguageTables in the "oracle_dump_tables" format of tests/Oracle/FORMATS.md.
//
// This is a line-by-line port of dump_tables() in tests/Oracle/shim/oracle.c: the same
// sections in the same order, the same number formatting ("%u", "%d", "%zu" are plain
// invariant decimal), the same string escaping (sb_put_string_or_null: \" \\ \n \t, and
// \xhh with lower-case hex for other bytes below 0x20 or from 0x7f), and the same derived
// array lengths (small_parse_table walk, parse_actions walk, reserved_words and
// external_scanner rows from lex_modes). Where the C code reads through ts_language_*
// functions, use the same logic on LanguageTables (port ts_language_lookup and friends
// locally as private helpers; do not depend on the runtime Language class).
// A null array in LanguageTables is the C NULL.
// Strings in LanguageTables are .NET strings; escape their UTF-8 bytes.
// Lines end with '\n' only.

using System.Globalization;
using System.Text;
using TreeSitter.Abi;

namespace TreeSitter.Generator.Dump;

public static class TableDumper
{
    /// <summary>ts_builtin_sym_error: ((TSSymbol)-1).</summary>
    private const uint BuiltinSymbolError = 0xFFFF;

    /// <summary>ts_builtin_sym_error_repeat: ts_builtin_sym_error - 1.</summary>
    private const uint BuiltinSymbolErrorRepeat = 0xFFFE;

    /// <summary>LANGUAGE_VERSION_WITH_RESERVED_WORDS: below it, lex_modes are TSLexMode and metadata is NULL.</summary>
    private const uint AbiVersionWithReservedWords = 15;

    /// <summary>Prints the dump of dump_tables() in oracle.c.</summary>
    /// <param name="tables">The tables of one language.</param>
    /// <returns>The dump: one "name = value" line per value, lines end with '\n'.</returns>
    public static string Dump(LanguageTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var builder = new StringBuilder();
        AppendHeader(builder, tables);
        AppendExternalScanner(builder, tables);
        AppendPrimaryStateIds(builder, tables);
        AppendNameAndMetadata(builder, tables);
        AppendReservedWords(builder, tables);
        AppendSymbols(builder, tables);
        AppendAliasMap(builder, tables);
        AppendProductions(builder, tables);
        AppendFieldMaps(builder, tables);
        AppendFieldNames(builder, tables);
        AppendLexModes(builder, tables);
        AppendParseTable(builder, tables);
        AppendSmallParseTable(builder, tables);
        AppendParseActions(builder, tables);
        return builder.ToString();
    }

    /// <summary>The string escaping of sb_put_string_or_null; null prints null.</summary>
    /// <param name="value">The string to escape, or null.</param>
    /// <returns>The escaped string in double quotes, or "null".</returns>
    public static string FormatString(string? value)
    {
        var builder = new StringBuilder();
        AppendStringLiteral(builder, value);
        return builder.ToString();
    }

    // ---------------------------------------------------------------- scalars

    /// <summary>"abi_version = 15\n" and the other TSLanguage scalars, in dump order.</summary>
    private static void AppendHeader(StringBuilder builder, LanguageTables tables)
    {
        AppendValue(builder, "abi_version", tables.AbiVersion);
        AppendValue(builder, "symbol_count", tables.SymbolCount);
        AppendValue(builder, "alias_count", tables.AliasCount);
        AppendValue(builder, "token_count", tables.TokenCount);
        AppendValue(builder, "external_token_count", tables.ExternalTokenCount);
        AppendValue(builder, "state_count", tables.StateCount);
        AppendValue(builder, "large_state_count", tables.LargeStateCount);
        AppendValue(builder, "production_id_count", tables.ProductionIdCount);
        AppendValue(builder, "field_count", tables.FieldCount);
        AppendValue(builder, "max_alias_sequence_length", tables.MaxAliasSequenceLength);
        AppendValue(builder, "keyword_capture_token", tables.KeywordCaptureToken);
        AppendValue(builder, "max_reserved_word_set_size", tables.MaxReservedWordSetSize);
        AppendFunction(builder, "lex_fn", tables.LexFn is not null);
        AppendFunction(builder, "keyword_lex_fn", tables.KeywordLexFn is not null);
        AppendFunction(builder, "external_scanner.create", tables.ExternalScannerCreateIsSet);
        AppendFunction(builder, "external_scanner.destroy", tables.ExternalScannerDestroyIsSet);
        AppendFunction(builder, "external_scanner.scan", tables.ExternalScannerScanIsSet);
        AppendFunction(builder, "external_scanner.serialize", tables.ExternalScannerSerializeIsSet);
        AppendFunction(builder, "external_scanner.deserialize", tables.ExternalScannerDeserializeIsSet);
    }

    /// <summary>The external_scanner.states and external_scanner.symbol_map sections.</summary>
    private static void AppendExternalScanner(StringBuilder builder, LanguageTables tables)
    {
        if (tables.ExternalScannerStates is null)
        {
            builder.Append("external_scanner.states = null\n");
        }
        else
        {
            // external_scanner.states[external_lex_state * external_token_count + token]
            uint maxExternalLexState = 0;
            for (uint state = 0; state < tables.StateCount; state++)
            {
                LexerMode mode = LexModeForState(tables, state);
                if (mode.ExternalLexState > maxExternalLexState)
                {
                    maxExternalLexState = mode.ExternalLexState;
                }
            }

            for (long row = 0; row <= maxExternalLexState; row++)
            {
                for (long token = 0; token < tables.ExternalTokenCount; token++)
                {
                    long index = (row * tables.ExternalTokenCount) + token;
                    AppendRowValue(builder, "external_scanner.states", row, token,
                        tables.ExternalScannerStates[index] ? 1u : 0u);
                }
            }
        }

        if (tables.ExternalScannerSymbolMap is null)
        {
            builder.Append("external_scanner.symbol_map = null\n");
        }
        else
        {
            for (uint i = 0; i < tables.ExternalTokenCount; i++)
            {
                AppendIndexedValue(builder, "external_scanner.symbol_map", i, tables.ExternalScannerSymbolMap[i]);
            }
        }
    }

    /// <summary>The primary_state_ids section.</summary>
    private static void AppendPrimaryStateIds(StringBuilder builder, LanguageTables tables)
    {
        if (tables.PrimaryStateIds is null)
        {
            builder.Append("primary_state_ids = null\n");
        }
        else
        {
            for (uint i = 0; i < tables.StateCount; i++)
            {
                AppendIndexedValue(builder, "primary_state_ids", i, tables.PrimaryStateIds[i]);
            }
        }
    }

    /// <summary>The name and metadata sections; metadata is NULL below ABI 15.</summary>
    private static void AppendNameAndMetadata(StringBuilder builder, LanguageTables tables)
    {
        AppendStringValue(builder, "name", tables.Name);

        if (tables.AbiVersion < AbiVersionWithReservedWords)
        {
            builder.Append("metadata = null\n");
        }
        else
        {
            LanguageMetadata metadata = tables.Metadata;
            builder.Append("metadata = major=").Append(Number(metadata.MajorVersion))
                .Append(" minor=").Append(Number(metadata.MinorVersion))
                .Append(" patch=").Append(Number(metadata.PatchVersion)).Append('\n');
        }
    }

    /// <summary>The reserved_words section; its length comes from the lex_modes set ids.</summary>
    private static void AppendReservedWords(StringBuilder builder, LanguageTables tables)
    {
        if (tables.ReservedWords is null)
        {
            builder.Append("reserved_words = null\n");
            return;
        }

        // reserved_words[reserved_word_set_id * max_reserved_word_set_size + i]
        uint maxSetId = 0;
        for (uint state = 0; state < tables.StateCount; state++)
        {
            LexerMode mode = LexModeForState(tables, state);
            if (mode.ReservedWordSetId > maxSetId)
            {
                maxSetId = mode.ReservedWordSetId;
            }
        }

        long length = ((long)maxSetId + 1) * tables.MaxReservedWordSetSize;
        for (long i = 0; i < length; i++)
        {
            AppendIndexedValue(builder, "reserved_words", i, tables.ReservedWords[i]);
        }
    }

    // ---------------------------------------------------------------- symbols

    /// <summary>The symbol_names, symbol_metadata and public_symbol_map sections.</summary>
    private static void AppendSymbols(StringBuilder builder, LanguageTables tables)
    {
        uint symbolCount = tables.SymbolCount + tables.AliasCount; // ts_language_symbol_count

        for (uint i = 0; i < symbolCount; i++)
        {
            AppendIndexedStringValue(builder, "symbol_names", i, tables.SymbolNames![i]);
        }

        for (uint i = 0; i < symbolCount; i++)
        {
            SymbolMetadata metadata = SymbolMetadataFor(tables, i);
            builder.Append("symbol_metadata[").Append(Number(i))
                .Append("] = visible=").Append(Flag(metadata.Visible))
                .Append(" named=").Append(Flag(metadata.Named))
                .Append(" supertype=").Append(Flag(metadata.Supertype)).Append('\n');
        }

        for (uint i = 0; i < symbolCount; i++)
        {
            AppendIndexedValue(builder, "public_symbol_map", i, tables.PublicSymbolMap![i]);
        }
    }

    /// <summary>alias_map: groups of {symbol, count, symbols...} terminated by symbol 0.</summary>
    private static void AppendAliasMap(StringBuilder builder, LanguageTables tables)
    {
        if (tables.AliasMap is null)
        {
            return;
        }

        ushort[] aliasMap = tables.AliasMap;
        uint group = 0;
        long position = 0;
        while (aliasMap[position] != 0)
        {
            uint symbol = aliasMap[position];
            uint count = aliasMap[position + 1];
            builder.Append("alias_group[").Append(Number(group)).Append("] = symbol=")
                .Append(Number(symbol)).Append(" count=").Append(Number(count)).Append('\n');
            position += 2;
            for (uint j = 0; j < count; j++)
            {
                AppendIndexedValue(builder, "alias_group[" + Number(group) + "].symbols", j, aliasMap[position]);
                position++;
            }

            group++;
        }
    }

    /// <summary>Alias sequences per production id; production 0 has no sequence in C.</summary>
    private static void AppendProductions(StringBuilder builder, LanguageTables tables)
    {
        for (uint production = 0; production < tables.ProductionIdCount; production++)
        {
            // ts_language_alias_sequence is NULL for production id 0 and without the array.
            if (production == 0 || tables.AliasSequences is null || tables.MaxAliasSequenceLength == 0)
            {
                builder.Append("production[").Append(Number(production)).Append("].alias_sequence = null\n");
            }
            else
            {
                for (uint child = 0; child < tables.MaxAliasSequenceLength; child++)
                {
                    AppendIndexedValue(builder, "production[" + Number(production) + "].alias_at", child,
                        AliasAt(tables, production, child));
                }
            }
        }
    }

    /// <summary>The field_map_slices and field_map_entries sections.</summary>
    private static void AppendFieldMaps(StringBuilder builder, LanguageTables tables)
    {
        // A grammar without fields has no field map (NULL in C).
        if (tables.FieldMapSlices is null)
        {
            builder.Append("field_map_slices = null\n");
            return;
        }

        for (uint production = 0; production < tables.ProductionIdCount; production++)
        {
            MapSlice slice = tables.FieldMapSlices![production];
            builder.Append("field_map_slices[").Append(Number(production))
                .Append("] = index=").Append(Number(slice.Index))
                .Append(" length=").Append(Number(slice.Length)).Append('\n');
            for (uint j = 0; j < slice.Length; j++)
            {
                long index = slice.Index + j;
                FieldMapEntry entry = tables.FieldMapEntries![index];
                builder.Append("field_map_entries[").Append(Number(index))
                    .Append("] = field_id=").Append(Number(entry.FieldId))
                    .Append(" child_index=").Append(Number(entry.ChildIndex))
                    .Append(" inherited=").Append(Flag(entry.Inherited)).Append('\n');
            }
        }
    }

    /// <summary>The field_names section: field_count + 1 entries, the first is always NULL.</summary>
    private static void AppendFieldNames(StringBuilder builder, LanguageTables tables)
    {
        if (tables.FieldNames is null)
        {
            builder.Append("field_names = null\n");
            return;
        }

        for (uint i = 0; i <= tables.FieldCount; i++)
        {
            AppendIndexedStringValue(builder, "field_names", i, tables.FieldNames![i]);
        }
    }

    /// <summary>The lex_modes section.</summary>
    private static void AppendLexModes(StringBuilder builder, LanguageTables tables)
    {
        for (uint i = 0; i < tables.StateCount; i++)
        {
            LexerMode mode = LexModeForState(tables, i);
            builder.Append("lex_modes[").Append(Number(i))
                .Append("] = lex_state=").Append(Number(mode.LexState))
                .Append(" external_lex_state=").Append(Number(mode.ExternalLexState))
                .Append(" reserved_word_set_id=").Append(Number(mode.ReservedWordSetId)).Append('\n');
        }
    }

    // ---------------------------------------------------------------- tables

    /// <summary>parse_table: large_state_count * symbol_count entries.</summary>
    private static void AppendParseTable(StringBuilder builder, LanguageTables tables)
    {
        uint length = tables.LargeStateCount * tables.SymbolCount;
        for (uint i = 0; i < length; i++)
        {
            AppendIndexedValue(builder, "parse_table", i, tables.ParseTable![i]);
        }
    }

    /// <summary>small_parse_table_map, then small_parse_table walked as ts_language_lookup walks it.</summary>
    private static void AppendSmallParseTable(StringBuilder builder, LanguageTables tables)
    {
        for (uint i = 0; i + tables.LargeStateCount < tables.StateCount; i++)
        {
            AppendIndexedValue(builder, "small_parse_table_map", i, tables.SmallParseTableMap![i]);
        }

        long maxOffset = 0;
        for (uint state = tables.LargeStateCount; state < tables.StateCount; state++)
        {
            uint index = tables.SmallParseTableMap![state - tables.LargeStateCount];
            ushort[] data = tables.SmallParseTable!;
            long position = index;
            ushort groupCount = data[position];
            position++;
            long end = index + 1; // group_count
            for (uint group = 0; group < groupCount; group++)
            {
                end += 2;
                long symbolCount = data[position + 1];
                end += symbolCount;
                position += 2 + symbolCount;
            }

            if (end > maxOffset)
            {
                maxOffset = end;
            }
        }

        for (long i = 0; i < maxOffset; i++)
        {
            AppendIndexedValue(builder, "small_parse_table", i, tables.SmallParseTable![i]);
        }
    }

    /// <summary>parse_actions: entry headers followed by their actions, up to the derived length.</summary>
    private static void AppendParseActions(StringBuilder builder, LanguageTables tables)
    {
        long maxOffset = 0;
        for (uint state = 0; state < tables.StateCount; state++)
        {
            for (uint symbol = 0; symbol < tables.TokenCount; symbol++)
            {
                uint tableValue = Lookup(tables, state, symbol);
                long end = tableValue + 1 + tables.ParseActions![tableValue].EntryCount;
                if (end > maxOffset)
                {
                    maxOffset = end;
                }
            }
        }

        long i = 0;
        while (i < maxOffset)
        {
            ParseActionEntry entry = tables.ParseActions![i];
            builder.Append("parse_actions[").Append(Number(i))
                .Append("] = entry count=").Append(Number(entry.EntryCount))
                .Append(" reusable=").Append(Flag(entry.EntryReusable)).Append('\n');
            i++;
            for (uint j = 0; j < entry.EntryCount; j++)
            {
                AppendParseAction(builder, i, tables.ParseActions[i]);
                i++;
            }

            if (i > maxOffset)
            {
                // The sequential walk must end exactly at the derived length.
                builder.Append("parse_actions = <layout error: walk ended at ").Append(Number(i))
                    .Append(", expected ").Append(Number(maxOffset)).Append(">\n");
            }
        }
    }

    /// <summary>One action line of parse_actions[i].</summary>
    private static void AppendParseAction(StringBuilder builder, long index, ParseActionEntry action)
    {
        builder.Append("parse_actions[").Append(Number(index)).Append("] = ");
        switch (action.Type)
        {
            case ParseActionType.Shift:
                builder.Append("shift state=").Append(Number(action.ShiftState))
                    .Append(" extra=").Append(Flag(action.ShiftExtra))
                    .Append(" repetition=").Append(Flag(action.ShiftRepetition)).Append('\n');
                break;
            case ParseActionType.Reduce:
                builder.Append("reduce symbol=").Append(Number(action.ReduceSymbol))
                    .Append(" child_count=").Append(Number(action.ReduceChildCount))
                    .Append(" dynamic_precedence=").Append(Number(action.ReduceDynamicPrecedence))
                    .Append(" production_id=").Append(Number(action.ReduceProductionId)).Append('\n');
                break;
            case ParseActionType.Accept:
                builder.Append("accept\n");
                break;
            case ParseActionType.Recover:
                builder.Append("recover\n");
                break;
            default:
                builder.Append("<unknown type=").Append(Number((byte)action.Type)).Append(">\n");
                break;
        }
    }

    // ---------------------------------------------------------------- text

    /// <summary>sb_fmt("%s = %u\n"): one scalar line.</summary>
    private static void AppendValue(StringBuilder builder, string name, uint value) =>
        builder.Append(name).Append(" = ").Append(Number(value)).Append('\n');

    /// <summary>sb_fmt("%s[%zu] = %u\n"): one indexed scalar line.</summary>
    private static void AppendIndexedValue(StringBuilder builder, string name, long index, uint value) =>
        builder.Append(name).Append('[').Append(Number(index)).Append("] = ").Append(Number(value)).Append('\n');

    /// <summary>sb_fmt("%s[%zu][%zu] = %u\n"): one external_scanner.states cell.</summary>
    private static void AppendRowValue(StringBuilder builder, string name, long row, long column, uint value) =>
        builder.Append(name).Append('[').Append(Number(row)).Append("][").Append(Number(column))
            .Append("] = ").Append(Number(value)).Append('\n');

    /// <summary>sb_fmt("%s = %s\n", ... "set" : "null"): one function pointer line.</summary>
    private static void AppendFunction(StringBuilder builder, string name, bool isSet) =>
        builder.Append(name).Append(" = ").Append(isSet ? "set" : "null").Append('\n');

    /// <summary>sb_fmt("%s = "), sb_put_string_or_null, then '\n': the name line.</summary>
    private static void AppendStringValue(StringBuilder builder, string name, string? value)
    {
        builder.Append(name).Append(" = ");
        AppendStringLiteral(builder, value);
        builder.Append('\n');
    }

    /// <summary>sb_fmt("%s[%u] = "), sb_put_string_or_null, then '\n': a symbol or field name line.</summary>
    private static void AppendIndexedStringValue(StringBuilder builder, string name, long index, string? value)
    {
        builder.Append(name).Append('[').Append(Number(index)).Append("] = ");
        AppendStringLiteral(builder, value);
        builder.Append('\n');
    }

    /// <summary>sb_put_string_or_null: "null", or the UTF-8 bytes of the string with C escaping.</summary>
    private static void AppendStringLiteral(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("null");
            return;
        }

        builder.Append('"');
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            switch (b)
            {
                case (byte)'"':
                    builder.Append("\\\"");
                    break;
                case (byte)'\\':
                    builder.Append("\\\\");
                    break;
                case (byte)'\n':
                    builder.Append("\\n");
                    break;
                case (byte)'\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (b < 0x20 || b >= 0x7f)
                    {
                        builder.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append((char)b);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>An invariant decimal of "%u" / "%d" / "%zu".</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A C bool printed by "%u".</summary>
    private static char Flag(bool value) => value ? '1' : '0';

    // ---------------------------------------------------------------- ts_language_* logic

    /// <summary>ts_language_lookup.</summary>
    private static ushort Lookup(LanguageTables tables, uint state, uint symbol)
    {
        if (state >= tables.LargeStateCount)
        {
            uint index = tables.SmallParseTableMap![state - tables.LargeStateCount];
            ushort[] data = tables.SmallParseTable!;
            long position = index;
            ushort groupCount = data[position];
            position++;
            for (uint group = 0; group < groupCount; group++)
            {
                ushort sectionValue = data[position];
                position++;
                ushort symbolCount = data[position];
                position++;
                for (uint j = 0; j < symbolCount; j++)
                {
                    if (data[position] == symbol)
                    {
                        return sectionValue;
                    }

                    position++;
                }
            }

            return 0;
        }

        return tables.ParseTable![(state * tables.SymbolCount) + symbol];
    }

    /// <summary>ts_language_lex_mode_for_state: below ABI 15 lex_modes are TSLexMode, the set id is 0.</summary>
    private static LexerMode LexModeForState(LanguageTables tables, uint state)
    {
        LexerMode mode = tables.LexModes![state];
        return tables.AbiVersion < AbiVersionWithReservedWords
            ? new LexerMode(mode.LexState, mode.ExternalLexState, 0)
            : mode;
    }

    /// <summary>ts_language_alias_at.</summary>
    private static ushort AliasAt(LanguageTables tables, uint productionId, uint childIndex) =>
        productionId != 0
            ? tables.AliasSequences![(productionId * tables.MaxAliasSequenceLength) + childIndex]
            : (ushort)0;

    /// <summary>ts_language_symbol_metadata.</summary>
    private static SymbolMetadata SymbolMetadataFor(LanguageTables tables, uint symbol) =>
        symbol == BuiltinSymbolError ? new SymbolMetadata(true, true, false)
        : symbol == BuiltinSymbolErrorRepeat ? default(SymbolMetadata)
        : tables.SymbolMetadata![symbol];
}
