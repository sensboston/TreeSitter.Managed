// Prints a Language in the "oracle_dump_language_api" format of tests/Oracle/FORMATS.md.
// Line-by-line port of dump_language_api() in tests/Oracle/shim/oracle.c: same sections,
// same loops and bounds, same formatting. Every value comes from the Language methods (the
// port of language.c), never directly from LanguageTables, so this checks the port.
// Strings use TableDumper.FormatString. Lines end with '\n' only.

using System.Globalization;
using System.Text;
using TreeSitter.Abi;

namespace TreeSitter.Generator.Dump;

public static class LanguageApiDumper
{
    /// <summary>Prints the dump of dump_language_api() in oracle.c.</summary>
    /// <param name="language">The language to print.</param>
    /// <returns>The dump: one line per value, lines end with '\n'.</returns>
    public static string Dump(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);

        var builder = new StringBuilder();
        uint stateCount = language.StateCount;
        uint symbolCount = language.SymbolCount;

        builder.Append("abi_version = ").Append(Number(language.AbiVersion)).Append('\n');
        builder.Append("name = ").Append(TableDumper.FormatString(language.Name)).Append('\n');
        LanguageMetadata metadata = language.Metadata;
        builder.Append("metadata = major=").Append(Number(metadata.MajorVersion))
            .Append(" minor=").Append(Number(metadata.MinorVersion))
            .Append(" patch=").Append(Number(metadata.PatchVersion)).Append('\n');
        builder.Append("state_count = ").Append(Number(stateCount)).Append('\n');
        builder.Append("symbol_count = ").Append(Number(symbolCount)).Append('\n');
        builder.Append("field_count = ").Append(Number(language.FieldCount)).Append('\n');

        // The parse table columns are the terminals.
        uint tokenCount = language.TokenCount;

        for (uint s = 0; s < stateCount; s++)
        {
            AppendState(builder, language, s, tokenCount);
        }

        for (uint sym = 0; sym < symbolCount; sym++)
        {
            AppendSymbol(builder, language, sym);
        }

        for (uint p = 0; p < language.ProductionIdCount; p++)
        {
            AppendProduction(builder, language, p);
        }

        for (uint f = 0; f <= language.FieldCount; f++)
        {
            AppendField(builder, language, f);
        }

        for (uint p = 0; p < language.ProductionIdCount; p++)
        {
            AppendFieldMap(builder, language, p);
        }

        ReadOnlySpan<ushort> supertypes = language.Supertypes;
        builder.Append("supertypes.count = ").Append(Number(supertypes.Length)).Append('\n');
        for (int j = 0; j < supertypes.Length; j++)
        {
            builder.Append("supertypes[").Append(Number(j)).Append("] = ")
                .Append(Number(supertypes[j])).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The lines of one parse state.</summary>
    private static void AppendState(StringBuilder builder, Language language, uint s, uint tokenCount)
    {
        builder.Append("state[").Append(Number(s)).Append("].is_primary = ")
            .Append(Flag(language.StateIsPrimary((ushort)s))).Append('\n');

        LexerMode mode = language.LexModeForState((ushort)s);
        builder.Append("state[").Append(Number(s)).Append("].lex_mode = lex_state=").Append(Number(mode.LexState))
            .Append(" external_lex_state=").Append(Number(mode.ExternalLexState))
            .Append(" reserved_word_set_id=").Append(Number(mode.ReservedWordSetId)).Append('\n');

        // The rows of the external scanner states are indexed by the external scanner
        // state of the lexer mode, the same value the runtime uses in lexer.c.
        if (language.TryGetEnabledExternalTokens(mode.ExternalLexState, out ReadOnlySpan<bool> externalTokens))
        {
            for (int e = 0; e < externalTokens.Length; e++)
            {
                builder.Append("state[").Append(Number(s)).Append("].external_tokens[").Append(Number(e)).Append("] = ")
                    .Append(Flag(externalTokens[e])).Append('\n');
            }
        }
        else
        {
            builder.Append("state[").Append(Number(s)).Append("].external_tokens = null\n");
        }

        // lookup and next_state are defined for every symbol in the raw
        // symbol_count (terminals + non-terminals): terminals use the parse
        // table, non-terminals use the goto table. Not ts_language_symbol_count():
        // alias symbols are never looked up by the runtime, and for the last
        // large state they would index past the end of parse_table.
        for (uint sym = 0; sym < language.RawSymbolCount; sym++)
        {
            builder.Append("state[").Append(Number(s)).Append("].symbol[").Append(Number(sym)).Append("].lookup = ")
                .Append(Number(language.Lookup((ushort)s, (ushort)sym))).Append('\n');
            builder.Append("state[").Append(Number(s)).Append("].symbol[").Append(Number(sym)).Append("].next_state = ")
                .Append(Number(language.NextState((ushort)s, (ushort)sym))).Append('\n');
        }

        // Table operations are defined for terminal symbols only
        // (0..token_count-1); the runtime asserts symbol < token_count.
        for (uint sym = 0; sym < tokenCount; sym++)
        {
            language.GetTableEntry((ushort)s, (ushort)sym, out TableEntry entry);
            builder.Append("state[").Append(Number(s)).Append("].symbol[").Append(Number(sym))
                .Append("].table_entry count=").Append(Number(entry.ActionCount))
                .Append(" reusable=").Append(Flag(entry.IsReusable)).Append('\n');
            AppendActions(builder, language.Actions((ushort)s, (ushort)sym));

            builder.Append("state[").Append(Number(s)).Append("].symbol[").Append(Number(sym)).Append("].has_reduce_action = ")
                .Append(Flag(language.HasReduceAction((ushort)s, (ushort)sym))).Append('\n');
            builder.Append("state[").Append(Number(s)).Append("].symbol[").Append(Number(sym)).Append("].is_reserved_word = ")
                .Append(Flag(language.IsReservedWord((ushort)s, (ushort)sym))).Append('\n');
            builder.Append("state[").Append(Number(s)).Append("].symbol[").Append(Number(sym)).Append("].has_actions = ")
                .Append(Flag(language.HasActions((ushort)s, (ushort)sym))).Append('\n');
        }

        LanguageLookaheadIterator iterator = language.Lookaheads((ushort)s);
        uint j = 0;
        while (iterator.Next())
        {
            builder.Append("state[").Append(Number(s)).Append("].lookahead[").Append(Number(j)).Append("] = symbol=")
                .Append(Number(iterator.Symbol)).Append(" table_value=").Append(Number(iterator.TableValue))
                .Append(" next_state=").Append(Number(iterator.NextState)).Append('\n');
            j++;
        }
    }

    /// <summary>The action lines of one table entry.</summary>
    private static void AppendActions(StringBuilder builder, ReadOnlySpan<ParseActionEntry> actions)
    {
        for (int j = 0; j < actions.Length; j++)
        {
            ParseActionEntry action = actions[j];
            builder.Append("action[").Append(Number(j)).Append("] = ");
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
    }

    /// <summary>The lines of one symbol.</summary>
    private static void AppendSymbol(StringBuilder builder, Language language, uint sym)
    {
        string? name = language.SymbolName((ushort)sym);
        builder.Append("symbol[").Append(Number(sym)).Append("].name = ")
            .Append(TableDumper.FormatString(name)).Append('\n');
        builder.Append("symbol[").Append(Number(sym)).Append("].type = ")
            .Append(Number((uint)language.SymbolType((ushort)sym))).Append('\n');

        SymbolMetadata metadata = language.GetSymbolMetadata((ushort)sym);
        builder.Append("symbol[").Append(Number(sym)).Append("].metadata = visible=").Append(Flag(metadata.Visible))
            .Append(" named=").Append(Flag(metadata.Named)).Append(" supertype=").Append(Flag(metadata.Supertype)).Append('\n');
        builder.Append("symbol[").Append(Number(sym)).Append("].public_symbol = ")
            .Append(Number(language.PublicSymbol((ushort)sym))).Append('\n');

        uint forName = name is null ? 0u : language.SymbolForName(name, metadata.Named);
        builder.Append("symbol[").Append(Number(sym)).Append("].for_name = ").Append(Number(forName)).Append('\n');

        ReadOnlySpan<ushort> aliases = language.AliasesForSymbol((ushort)sym);
        builder.Append("symbol[").Append(Number(sym)).Append("].alias_count = ").Append(Number(aliases.Length)).Append('\n');
        for (int j = 0; j < aliases.Length; j++)
        {
            builder.Append("symbol[").Append(Number(sym)).Append("].aliases[").Append(Number(j)).Append("] = ")
                .Append(Number(aliases[j])).Append('\n');
        }

        ReadOnlySpan<ushort> subtypes = language.Subtypes((ushort)sym);
        builder.Append("symbol[").Append(Number(sym)).Append("].subtype_count = ").Append(Number(subtypes.Length)).Append('\n');
        for (int j = 0; j < subtypes.Length; j++)
        {
            builder.Append("symbol[").Append(Number(sym)).Append("].subtypes[").Append(Number(j)).Append("] = ")
                .Append(Number(subtypes[j])).Append('\n');
        }
    }

    /// <summary>The lines of one production id.</summary>
    private static void AppendProduction(StringBuilder builder, Language language, uint p)
    {
        bool hasSequence = language.TryGetAliasSequence(p, out _) &&
            language.MaxAliasSequenceLength > 0;
        if (!hasSequence)
        {
            builder.Append("production[").Append(Number(p)).Append("].alias_sequence = null\n");
            return;
        }

        for (uint c = 0; c < language.MaxAliasSequenceLength; c++)
        {
            builder.Append("production[").Append(Number(p)).Append("].alias_at[").Append(Number(c)).Append("] = ")
                .Append(Number(language.AliasAt(p, c))).Append('\n');
        }
    }

    /// <summary>The lines of one field id.</summary>
    private static void AppendField(StringBuilder builder, Language language, uint f)
    {
        string? name = language.FieldNameForId((ushort)f);
        builder.Append("field[").Append(Number(f)).Append("].name_for_id = ")
            .Append(TableDumper.FormatString(name)).Append('\n');
        builder.Append("field[").Append(Number(f)).Append("].id_for_name = ")
            .Append(Number(language.FieldIdForName(name ?? string.Empty))).Append('\n');
    }

    /// <summary>The lines of the field map of one production id.</summary>
    private static void AppendFieldMap(StringBuilder builder, Language language, uint p)
    {
        ReadOnlySpan<FieldMapEntry> map = language.FieldMap(p);
        builder.Append("field_map[").Append(Number(p)).Append("].count = ").Append(Number(map.Length)).Append('\n');
        for (int j = 0; j < map.Length; j++)
        {
            FieldMapEntry entry = map[j];
            builder.Append("field_map[").Append(Number(p)).Append("].entries[").Append(Number(j)).Append("] = field_id=")
                .Append(Number(entry.FieldId)).Append(" child_index=").Append(Number(entry.ChildIndex))
                .Append(" inherited=").Append(Flag(entry.Inherited)).Append('\n');
        }
    }

    /// <summary>An invariant decimal of "%u" / "%d" / "%zu".</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A C bool printed by "%u".</summary>
    private static char Flag(bool value) => value ? '1' : '0';
}
