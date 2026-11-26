// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/language.c and lib/src/language.h
// (without wasm support; the public TSLookaheadIterator is the LookaheadIterator class
// of LookaheadIterator.cs, which uses Lookaheads below).
//
// Rules for the port:
// - Every method has the C function name in a comment above it and the same logic.
// - Where C returns NULL, the C# method returns false from a TryGet method (a span cannot be
//   null), or null for strings.
// - Pointers into tables become indices into LanguageTables arrays.
// - No allocations in Lookup, GetTableEntry, NextState and the lookahead iterator.
// - String comparisons are ordinal (C uses strncmp on the UTF-8 bytes; tree-sitter symbol
//   and field names are compared byte by byte, so compare the UTF-8 encoding when a name is
//   not ASCII). The UTF-8 bytes of the names are cached once per language.
// - The C code branches on the ABI version in ts_language_name, ts_language_metadata,
//   ts_language_lex_mode_for_state and ts_language_state_is_primary. Only ABI 15 is
//   supported here (FromTables rejects every other version), so those branches are already
//   decided: the ABI 15 behavior is the only one that can happen.

using System.ComponentModel;
using System.Text;
using TreeSitter.Abi;

namespace TreeSitter;

/// <summary>The kind of a grammar symbol (api.h TSSymbolType).</summary>
public enum SymbolType
{
    /// <summary>A named, visible symbol.</summary>
    Regular,

    /// <summary>An anonymous, visible symbol (a literal token).</summary>
    Anonymous,

    /// <summary>A hidden supertype symbol.</summary>
    Supertype,

    /// <summary>A hidden symbol of the grammar.</summary>
    Auxiliary,
}

/// <summary>A grammar: parse tables, symbol and field names. Immutable and thread safe.</summary>
public sealed class Language
{
    /// <summary>The only language ABI version this runtime reads (api.h TREE_SITTER_LANGUAGE_VERSION).</summary>
    public const uint SupportedAbiVersion = 15;

    /// <summary>ts_builtin_sym_error: ((TSSymbol)-1).</summary>
    private const ushort BuiltinSymbolError = 0xFFFF;

    /// <summary>ts_builtin_sym_error_repeat: ts_builtin_sym_error - 1.</summary>
    private const ushort BuiltinSymbolErrorRepeat = 0xFFFE;

    /// <summary>The name of ts_builtin_sym_error.</summary>
    private static readonly byte[] ErrorName = { (byte)'E', (byte)'R', (byte)'R', (byte)'O', (byte)'R' };

    /// <summary>The UTF-8 bytes of the symbol names, for the byte-wise name comparisons.</summary>
    private readonly byte[]?[] _symbolNameBytes;

    /// <summary>The UTF-8 bytes of the field names, for the byte-wise name comparisons.</summary>
    private readonly byte[]?[] _fieldNameBytes;

    private Language(LanguageTables tables)
    {
        Tables = tables;
        _symbolNameBytes = BuildNameBytes(tables.SymbolNames);
        _fieldNameBytes = BuildNameBytes(tables.FieldNames);
    }

    internal LanguageTables Tables { get; }

    /// <summary>
    /// Creates a language from generated tables. Throws ArgumentException when the ABI version
    /// is not SupportedAbiVersion or a table needed by the runtime is missing.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Language FromTables(LanguageTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.AbiVersion != SupportedAbiVersion)
        {
            throw new ArgumentException(
                $"The language ABI version {tables.AbiVersion} is not supported, this runtime reads version {SupportedAbiVersion}.",
                nameof(tables));
        }

        // The arrays the runtime reads unconditionally.
        RequireArray(tables.ParseActions, "parse_actions");
        RequireArray(tables.SymbolNames, "symbol_names");
        RequireArray(tables.SymbolMetadata, "symbol_metadata");
        RequireArray(tables.PublicSymbolMap, "public_symbol_map");
        RequireArray(tables.AliasMap, "alias_map");
        RequireArray(tables.LexModes, "lex_modes");
        RequireArray(tables.PrimaryStateIds, "primary_state_ids");

        // The arrays the runtime reads only when the grammar uses them.
        if (tables.LargeStateCount > 0)
        {
            RequireArray(tables.ParseTable, "parse_table");
        }

        if (tables.StateCount > tables.LargeStateCount)
        {
            RequireArray(tables.SmallParseTable, "small_parse_table");
            RequireArray(tables.SmallParseTableMap, "small_parse_table_map");
        }

        if (tables.MaxReservedWordSetSize > 0)
        {
            RequireArray(tables.ReservedWords, "reserved_words");
        }

        if (tables.ProductionIdCount > 0 && tables.MaxAliasSequenceLength > 0)
        {
            RequireArray(tables.AliasSequences, "alias_sequences");
        }

        if (tables.FieldCount > 0)
        {
            RequireArray(tables.FieldNames, "field_names");
            RequireArray(tables.FieldMapSlices, "field_map_slices");
            RequireArray(tables.FieldMapEntries, "field_map_entries");
        }

        if (tables.SupertypeCount > 0)
        {
            RequireArray(tables.SupertypeSymbols, "supertype_symbols");
            RequireArray(tables.SupertypeMapSlices, "supertype_map_slices");
            RequireArray(tables.SupertypeMapEntries, "supertype_map_entries");
        }

        if (tables.ExternalTokenCount > 0)
        {
            RequireArray(tables.ExternalScannerStates, "external_scanner.states");
        }

        return new Language(tables);
    }

    /// <summary>Throws when a table the runtime needs is missing.</summary>
    /// <typeparam name="T">The type of the table.</typeparam>
    /// <param name="array">The table, or null when it is missing.</param>
    /// <param name="arrayName">The name of the table in the grammar ABI.</param>
    private static void RequireArray<T>(T? array, string arrayName)
        where T : class
    {
        if (array is null)
        {
            throw new ArgumentException(
                $"The language tables are missing the '{arrayName}' array, which the runtime needs.");
        }
    }

    // ---------------------------------------------------------------- public API (api.h)

    /// <summary>ts_language_abi_version</summary>
    public uint AbiVersion => Tables.AbiVersion;

    /// <summary>ts_language_name</summary>
    public string? Name => Tables.Name;

    /// <summary>ts_language_metadata</summary>
    public LanguageMetadata Metadata => Tables.Metadata;

    /// <summary>ts_language_symbol_count: symbols plus aliases.</summary>
    public uint SymbolCount => Tables.SymbolCount + Tables.AliasCount;

    /// <summary>ts_language_state_count</summary>
    public uint StateCount => Tables.StateCount;

    /// <summary>ts_language_field_count</summary>
    public uint FieldCount => Tables.FieldCount;

    /// <summary>ts_language_symbol_name</summary>
    public string? SymbolName(ushort symbol)
    {
        if (symbol == BuiltinSymbolError)
        {
            return "ERROR";
        }

        if (symbol == BuiltinSymbolErrorRepeat)
        {
            return "_ERROR";
        }

        if (symbol < SymbolCount)
        {
            return Tables.SymbolNames![symbol];
        }

        return null;
    }

    /// <summary>ts_language_symbol_for_name; 0 when not found.</summary>
    public ushort SymbolForName(string name, bool isNamed)
    {
        ArgumentNullException.ThrowIfNull(name);

        byte[] query = Encoding.UTF8.GetBytes(name);
        if (isNamed && IsErrorName(query))
        {
            return BuiltinSymbolError;
        }

        ushort count = (ushort)SymbolCount;
        for (ushort i = 0; i < count; i++)
        {
            SymbolMetadata metadata = GetSymbolMetadata(i);
            if ((!metadata.Visible && !metadata.Supertype) || metadata.Named != isNamed)
            {
                continue;
            }

            byte[]? symbolName = _symbolNameBytes[i];
            if (symbolName is null)
            {
                continue;
            }

            if (Strncmp(symbolName, query, query.Length) == 0 && symbolName.Length == query.Length)
            {
                return Tables.PublicSymbolMap![i];
            }
        }

        return 0;
    }

    /// <summary>ts_language_symbol_type</summary>
    public SymbolType SymbolType(ushort symbol)
    {
        // The method hides the enum of the same name, so the enum is qualified here.
        SymbolMetadata metadata = GetSymbolMetadata(symbol);
        if (metadata.Named && metadata.Visible)
        {
            return TreeSitter.SymbolType.Regular;
        }

        if (metadata.Visible)
        {
            return TreeSitter.SymbolType.Anonymous;
        }

        if (metadata.Supertype)
        {
            return TreeSitter.SymbolType.Supertype;
        }

        return TreeSitter.SymbolType.Auxiliary;
    }

    /// <summary>ts_language_field_name_for_id</summary>
    public string? FieldNameForId(ushort fieldId)
    {
        uint count = FieldCount;
        if (count != 0 && fieldId <= count)
        {
            return Tables.FieldNames![fieldId];
        }

        return null;
    }

    /// <summary>ts_language_field_id_for_name; 0 when not found.</summary>
    public ushort FieldIdForName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        byte[] query = Encoding.UTF8.GetBytes(name);
        ushort count = (ushort)FieldCount;
        for (ushort i = 1; i < count + 1; i++)
        {
            byte[]? fieldName = _fieldNameBytes[i];
            if (fieldName is null)
            {
                continue;
            }

            switch (Strncmp(query, fieldName, query.Length))
            {
                case 0:
                    if (fieldName.Length == query.Length)
                    {
                        return i;
                    }

                    break;
                case -1:
                    // The field names are sorted: the name is smaller than this field name
                    // and cannot be any of the following ones either.
                    return 0;
                default:
                    break;
            }
        }

        return 0;
    }

    /// <summary>ts_language_next_state</summary>
    public ushort NextState(ushort state, ushort symbol)
    {
        if (symbol == BuiltinSymbolError ||
            symbol == BuiltinSymbolErrorRepeat ||
            symbol >= Tables.SymbolCount ||
            state >= Tables.StateCount)
        {
            return 0;
        }

        if (symbol < Tables.TokenCount)
        {
            ReadOnlySpan<ParseActionEntry> actions = Actions(state, symbol);
            if (actions.Length > 0)
            {
                ParseActionEntry action = actions[actions.Length - 1];
                if (action.Type == ParseActionType.Shift)
                {
                    return action.ShiftExtra ? state : action.ShiftState;
                }
            }

            return 0;
        }

        return Lookup(state, symbol);
    }

    /// <summary>ts_language_supertypes</summary>
    public ReadOnlySpan<ushort> Supertypes => Tables.SupertypeSymbols ?? Array.Empty<ushort>();

    /// <summary>ts_language_subtypes; empty when C returns NULL.</summary>
    public ReadOnlySpan<ushort> Subtypes(ushort supertype)
    {
        if (supertype >= SymbolCount || !GetSymbolMetadata(supertype).Supertype)
        {
            return ReadOnlySpan<ushort>.Empty;
        }

        MapSlice slice = Tables.SupertypeMapSlices![supertype];
        return Tables.SupertypeMapEntries!.AsSpan(slice.Index, slice.Length);
    }

    /// <summary>
    /// ts_lookahead_iterator_new: null when the state is not below the state count of the
    /// language, like the C function that returns NULL.
    /// </summary>
    /// <param name="state">The parse state to enumerate the valid symbols of.</param>
    /// <returns>A new iterator over the symbols of the state, or null.</returns>
    public LookaheadIterator? CreateLookaheadIterator(ushort state) =>
        state >= Tables.StateCount ? null : new LookaheadIterator(this, state);

    // ---------------------------------------------------------------- internal (language.h)

    /// <summary>ts_language_table_entry</summary>
    internal void GetTableEntry(ushort state, ushort symbol, out TableEntry result)
    {
        if (symbol == BuiltinSymbolError || symbol == BuiltinSymbolErrorRepeat)
        {
            result = new TableEntry(0, 0, false);
            return;
        }

        uint actionIndex = Lookup(state, symbol);
        ParseActionEntry entry = Tables.ParseActions![actionIndex];
        result = new TableEntry((int)actionIndex + 1, entry.EntryCount, entry.EntryReusable);
    }

    /// <summary>ts_language_lex_mode_for_state</summary>
    internal LexerMode LexModeForState(ushort state) => Tables.LexModes![state];

    /// <summary>ts_language_is_reserved_word</summary>
    internal bool IsReservedWord(ushort state, ushort symbol)
    {
        LexerMode mode = LexModeForState(state);
        if (mode.ReservedWordSetId > 0)
        {
            ushort[] reservedWords = Tables.ReservedWords!;
            int start = mode.ReservedWordSetId * Tables.MaxReservedWordSetSize;
            int end = start + Tables.MaxReservedWordSetSize;
            for (int i = start; i < end; i++)
            {
                if (reservedWords[i] == symbol)
                {
                    return true;
                }

                if (reservedWords[i] == 0)
                {
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>ts_language_symbol_metadata</summary>
    internal SymbolMetadata GetSymbolMetadata(ushort symbol)
    {
        if (symbol == BuiltinSymbolError)
        {
            return new SymbolMetadata(true, true, false);
        }

        if (symbol == BuiltinSymbolErrorRepeat)
        {
            return default(SymbolMetadata);
        }

        return Tables.SymbolMetadata![symbol];
    }

    /// <summary>ts_language_public_symbol</summary>
    internal ushort PublicSymbol(ushort symbol)
    {
        if (symbol == BuiltinSymbolError)
        {
            return symbol;
        }

        return Tables.PublicSymbolMap![symbol];
    }

    /// <summary>ts_language_actions</summary>
    internal ReadOnlySpan<ParseActionEntry> Actions(ushort state, ushort symbol)
    {
        GetTableEntry(state, symbol, out TableEntry entry);
        return entry.ActionCount == 0
            ? ReadOnlySpan<ParseActionEntry>.Empty
            : Tables.ParseActions!.AsSpan(entry.ActionsStart, (int)entry.ActionCount);
    }

    /// <summary>ts_language_has_reduce_action</summary>
    internal bool HasReduceAction(ushort state, ushort symbol)
    {
        GetTableEntry(state, symbol, out TableEntry entry);
        if (entry.ActionCount == 0)
        {
            return false;
        }

        return Tables.ParseActions![entry.ActionsStart].Type == ParseActionType.Reduce;
    }

    /// <summary>ts_language_lookup</summary>
    internal ushort Lookup(ushort state, ushort symbol)
    {
        if (state >= Tables.LargeStateCount)
        {
            uint index = Tables.SmallParseTableMap![state - Tables.LargeStateCount];
            ushort[] data = Tables.SmallParseTable!;
            int position = (int)index;
            ushort groupCount = data[position];
            position++;
            for (int group = 0; group < groupCount; group++)
            {
                ushort sectionValue = data[position];
                position++;
                ushort symbolCount = data[position];
                position++;
                for (int j = 0; j < symbolCount; j++)
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

        return Tables.ParseTable![((int)state * (int)Tables.SymbolCount) + symbol];
    }

    /// <summary>ts_language_has_actions</summary>
    internal bool HasActions(ushort state, ushort symbol) => Lookup(state, symbol) != 0;

    /// <summary>ts_language_lookaheads</summary>
    internal LanguageLookaheadIterator Lookaheads(ushort state)
    {
        bool isSmallState = state >= Tables.LargeStateCount;
        int data;
        int groupEnd = 0;
        ushort groupCount = 0;
        if (isSmallState)
        {
            uint index = Tables.SmallParseTableMap![state - Tables.LargeStateCount];
            data = (int)index;
            groupEnd = data + 1;
            groupCount = Tables.SmallParseTable![data];
        }
        else
        {
            data = (int)((int)state * (int)Tables.SymbolCount);
        }

        return new LanguageLookaheadIterator(this, data, groupEnd, groupCount, isSmallState);
    }

    /// <summary>ts_language_state_is_primary</summary>
    internal bool StateIsPrimary(ushort state) => state == Tables.PrimaryStateIds![state];

    /// <summary>ts_language_enabled_external_tokens; false when C returns NULL.</summary>
    internal bool TryGetEnabledExternalTokens(uint externalScannerState, out ReadOnlySpan<bool> tokens)
    {
        bool[]? states = Tables.ExternalScannerStates;
        if (externalScannerState == 0 || states is null)
        {
            tokens = ReadOnlySpan<bool>.Empty;
            return false;
        }

        int offset = (int)(Tables.ExternalTokenCount * externalScannerState);
        tokens = states.AsSpan(offset, (int)Tables.ExternalTokenCount);
        return true;
    }

    /// <summary>ts_language_alias_sequence; false when C returns NULL.</summary>
    private ushort[]?[]? _aliasSequenceArrays;

    /// <summary>The alias sequence of a production as an array, created once per production
    /// and shared (the tree cursor and node iterators keep it in a struct field).</summary>
    internal ushort[]? AliasSequenceArray(uint productionId)
    {
        if (!TryGetAliasSequence(productionId, out ReadOnlySpan<ushort> sequence))
        {
            return null;
        }

        ushort[]?[] arrays = _aliasSequenceArrays ??= new ushort[]?[Tables.ProductionIdCount];
        return arrays[productionId] ??= sequence.ToArray();
    }

    internal bool TryGetAliasSequence(uint productionId, out ReadOnlySpan<ushort> sequence)
    {
        ushort[]? sequences = Tables.AliasSequences;
        if (productionId == 0 || sequences is null)
        {
            sequence = ReadOnlySpan<ushort>.Empty;
            return false;
        }

        int start = (int)(productionId * Tables.MaxAliasSequenceLength);
        sequence = sequences.AsSpan(start, Tables.MaxAliasSequenceLength);
        return true;
    }

    /// <summary>ts_language_alias_at</summary>
    internal ushort AliasAt(uint productionId, uint childIndex)
    {
        ushort[]? sequences = Tables.AliasSequences;
        if (productionId == 0 || sequences is null)
        {
            return 0;
        }

        return sequences[(int)((productionId * Tables.MaxAliasSequenceLength) + childIndex)];
    }

    /// <summary>ts_language_field_map</summary>
    internal ReadOnlySpan<FieldMapEntry> FieldMap(uint productionId)
    {
        if (Tables.FieldCount == 0)
        {
            return ReadOnlySpan<FieldMapEntry>.Empty;
        }

        MapSlice slice = Tables.FieldMapSlices![productionId];
        return Tables.FieldMapEntries!.AsSpan(slice.Index, slice.Length);
    }

    /// <summary>ts_language_aliases_for_symbol</summary>
    internal ReadOnlySpan<ushort> AliasesForSymbol(ushort originalSymbol)
    {
        ushort[] aliasMap = Tables.AliasMap!;
        int index = 0;
        while (true)
        {
            ushort symbol = aliasMap[index++];
            if (symbol == 0 || symbol > originalSymbol)
            {
                break;
            }

            ushort count = aliasMap[index++];
            if (symbol == originalSymbol)
            {
                return aliasMap.AsSpan(index, count);
            }

            index += count;
        }

        return Tables.PublicSymbolMap!.AsSpan(originalSymbol, 1);
    }

    // ---------------------------------------------------------------- raw table fields

    /// <summary>
    /// The raw symbol_count field: the terminals plus the non-terminals, without the alias
    /// symbols. The parse table is dimensioned by it (state * symbol_count + symbol).
    /// </summary>
    internal uint RawSymbolCount => Tables.SymbolCount;

    /// <summary>The raw token_count field: the number of terminal symbols.</summary>
    internal uint TokenCount => Tables.TokenCount;

    /// <summary>The raw production_id_count field.</summary>
    internal uint ProductionIdCount => Tables.ProductionIdCount;

    /// <summary>The raw max_alias_sequence_length field.</summary>
    internal ushort MaxAliasSequenceLength => Tables.MaxAliasSequenceLength;

    // ---------------------------------------------------------------- helpers

    /// <summary>The UTF-8 bytes of every name; a null name stays null.</summary>
    private static byte[]?[] BuildNameBytes(string?[]? names)
    {
        if (names is null)
        {
            return Array.Empty<byte[]?>();
        }

        var result = new byte[]?[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            string? name = names[i];
            result[i] = name is null ? null : Encoding.UTF8.GetBytes(name);
        }

        return result;
    }

    /// <summary>The "ERROR" name of ts_builtin_sym_error.</summary>
    private static bool IsErrorName(byte[] query) =>
        query.Length == ErrorName.Length && Strncmp(query, ErrorName, ErrorName.Length) == 0;

    /// <summary>
    /// strncmp on the UTF-8 bytes: the difference of the first differing bytes, 0 when the
    /// first bytes are equal. A byte behind the end of a name is the null terminator.
    /// </summary>
    private static int Strncmp(byte[] left, byte[] right, int length)
    {
        for (int i = 0; i < length; i++)
        {
            byte l = i < left.Length ? left[i] : (byte)0;
            byte r = i < right.Length ? right[i] : (byte)0;
            if (l != r)
            {
                return l - r;
            }

            if (l == 0)
            {
                return 0;
            }
        }

        return 0;
    }
}

/// <summary>TableEntry (language.h): the actions are ParseActions[ActionsStart .. ActionsStart + ActionCount).</summary>
internal readonly record struct TableEntry(int ActionsStart, uint ActionCount, bool IsReusable);

/// <summary>
/// LookaheadIterator of language.h (ts_language_lookaheads / ts_lookahead_iterator__next),
/// with the same fields and phases. Next() is ts_lookahead_iterator__next.
/// </summary>
internal struct LanguageLookaheadIterator
{
    /// <summary>LookaheadPhase of language.h.</summary>
    private enum Phase
    {
        /// <summary>No next() yet.</summary>
        Fresh,

        /// <summary>The last next() returned true.</summary>
        Positioned,

        /// <summary>The last next() returned false.</summary>
        Done,
    }

    // C: const TSLanguage *language.
    private readonly Language? _language;

    // C: const uint16_t *data; the index into the parse table or into the small parse table.
    private int _data;

    // C: const uint16_t *group_end; the index into the small parse table.
    private int _groupEnd;

    // C: uint16_t group_count.
    private ushort _groupCount;

    // C: bool is_small_state.
    private readonly bool _isSmallState;

    // C: LookaheadPhase phase.
    private Phase _phase;

    // C: const TSParseAction *actions; the index into the parse actions.
    private int _actionsStart;

    // C: uint16_t action_count.
    private int _actionCount;

    /// <summary>Creates the iterator of a state (ts_language_lookaheads).</summary>
    /// <param name="language">The language of the state.</param>
    /// <param name="data">The index of the first entry of the state.</param>
    /// <param name="groupEnd">The index behind the last symbol of the current group.</param>
    /// <param name="groupCount">The number of groups that are not visited yet.</param>
    /// <param name="isSmallState">True for a small parse state.</param>
    internal LanguageLookaheadIterator(
        Language language,
        int data,
        int groupEnd,
        ushort groupCount,
        bool isSmallState)
    {
        _language = language;
        _data = data;
        _groupEnd = groupEnd;
        _groupCount = groupCount;
        _isSmallState = isSmallState;
        _phase = Phase.Fresh;
        _actionsStart = 0;
        _actionCount = 0;
        Symbol = ushort.MaxValue;
        TableValue = 0;
        NextState = 0;
    }

    /// <summary>The symbol of the current position (C: symbol).</summary>
    public ushort Symbol { get; internal set; }

    /// <summary>The table value of the current position (C: table_value).</summary>
    public ushort TableValue { get; internal set; }

    /// <summary>The successor state of the current position (C: next_state).</summary>
    public ushort NextState { get; internal set; }

    /// <summary>True between the first Next() that returned true and the next reset
    /// (C: phase == LookaheadPositioned); ts_lookahead_iterator_current_symbol_name
    /// returns NULL in every other phase.</summary>
    internal bool IsPositioned => _phase == Phase.Positioned;

    /// <summary>The actions of the current symbol; empty for a non-terminal.</summary>
    public ReadOnlySpan<ParseActionEntry> Actions
    {
        get
        {
            ParseActionEntry[]? actions = _language?.Tables.ParseActions;
            return actions is null || _actionCount == 0
                ? ReadOnlySpan<ParseActionEntry>.Empty
                : actions.AsSpan(_actionsStart, _actionCount);
        }
    }

    /// <summary>ts_lookahead_iterator__next: moves to the next valid symbol of the state.</summary>
    /// <returns>True when the iterator is on a valid symbol.</returns>
    public bool Next()
    {
        if (_phase == Phase.Done)
        {
            return false;
        }

        LanguageTables tables = _language!.Tables;

        if (_isSmallState)
        {
            // For small parse states the valid symbols are listed explicitly, grouped by
            // their value. The actions do not have to be looked up again until the
            // iterator moves to the next group.
            ushort[] data = tables.SmallParseTable!;
            _data++;
            if (_data == _groupEnd)
            {
                if (_groupCount == 0)
                {
                    _phase = Phase.Done;
                    return false;
                }

                _groupCount--;
                TableValue = data[_data++];
                int symbolCount = data[_data++];
                _groupEnd = _data + symbolCount;
                Symbol = data[_data];
            }
            else
            {
                Symbol = data[_data];
                _phase = Phase.Positioned;
                return true;
            }
        }
        else
        {
            // For large parse states every symbol is visited until one with valid
            // actions is found.
            ushort[] row = tables.ParseTable!;
            int symbol = _phase == Phase.Fresh ? 0 : Symbol + 1;
            while (symbol < tables.SymbolCount && row[_data + symbol] == 0)
            {
                symbol++;
            }

            if (symbol >= tables.SymbolCount)
            {
                _phase = Phase.Done;
                return false;
            }

            Symbol = (ushort)symbol;
            TableValue = row[_data + symbol];
        }

        // The table value is a list of actions for a terminal and a successor state
        // for a non-terminal.
        if (Symbol < tables.TokenCount)
        {
            ParseActionEntry entry = tables.ParseActions![TableValue];
            _actionCount = entry.EntryCount;
            _actionsStart = TableValue + 1;
            NextState = 0;
        }
        else
        {
            _actionCount = 0;
            NextState = TableValue;
        }

        _phase = Phase.Positioned;
        return true;
    }
}
