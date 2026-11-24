// Builds a GrammarModel from a CTranslationUnit.
//
// The TSLanguage initializer is the only source of truth for which array goes into which
// field (never the array names). Steps:
// 1. Flatten the TSLanguage object (InitializerFlattener) to a CAggregateValue.
// 2. For each pointer member: CNullValue -> null array in LanguageTables (the C NULL; the
//    dumper prints "null"); CReferenceValue -> the referenced file-level variable, flattened; the
//    indices of the reference must be all zero (&x[0][0] is the start of x). The element
//    values are converted to the LanguageTables element type (ParseActionEntry, MapSlice,
//    SymbolMetadata, ...). Multi-dimensional arrays are stored row-major.
// 3. Scalars go to the matching properties; name -> string (UTF-8 bytes decoded);
//    metadata -> LanguageMetadata.
// 4. lex_fn / keyword_lex_fn: CNullValue -> null; a function reference -> the CFunction with
//    that name (must exist) and LanguageTables.LexFn / KeywordLexFn = PlaceholderLexFunction.
// 5. external_scanner: states and symbol_map like other pointers; the function members set
//    the ExternalScanner*IsSet flags and ExternalScannerFunctions (names must be prototypes).
// 6. CharacterSets: every file-level variable of type TSCharacterRange.
// 7. Lex / KeywordLex: the bodies of the lex functions parsed by LexBodyParser.
// 8. SymbolValues: every ACCEPT_TOKEN symbol of Lex / KeywordLex evaluated by ConstEvaluator
//    (enum members of the file, ts_builtin_sym_end and other parser.h names).
// A union element of ts_parse_actions with ActiveMember "entry" becomes
// ParseActionEntry.Entry(count, reusable); with "action" it becomes Shift / Reduce / Accept /
// Recover by the "type" value of the active TSParseAction member.

using System.Text;
using TreeSitter.Abi;
using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Lex;

namespace TreeSitter.Generator.Model;

public static class GrammarModelBuilder
{
    /// <summary>Lex function used in tables built at generation time: never called.</summary>
    public static readonly LexFunction PlaceholderLexFunction = static (_, _) =>
        throw new InvalidOperationException("tables built by ts2cs have no lexer");

    public static GrammarModel Build(CTranslationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var evaluator = new ConstEvaluator(unit);
        var flattener = new InitializerFlattener(evaluator);
        CArrayValue languageArray = flattener.FlattenVariable(unit.Language.Language);
        var language = (CAggregateValue)languageArray.Elements[0];

        var tables = new LanguageTables
        {
            AbiVersion = (uint)MemberInt(language, "abi_version"),
            SymbolCount = (uint)MemberInt(language, "symbol_count"),
            AliasCount = (uint)MemberInt(language, "alias_count"),
            TokenCount = (uint)MemberInt(language, "token_count"),
            ExternalTokenCount = (uint)MemberInt(language, "external_token_count"),
            StateCount = (uint)MemberInt(language, "state_count"),
            LargeStateCount = (uint)MemberInt(language, "large_state_count"),
            ProductionIdCount = (uint)MemberInt(language, "production_id_count"),
            FieldCount = (uint)MemberInt(language, "field_count"),
            MaxAliasSequenceLength = (ushort)MemberInt(language, "max_alias_sequence_length"),
            KeywordCaptureToken = (ushort)MemberInt(language, "keyword_capture_token"),
            MaxReservedWordSetSize = (ushort)MemberInt(language, "max_reserved_word_set_size"),
            SupertypeCount = (uint)MemberInt(language, "supertype_count"),
            Name = ToName(language.Members["name"]),
            Metadata = ToMetadata(language.Members["metadata"]),
            ParseTable = ToUInt16Array(ResolveArray(unit, flattener, language.Members["parse_table"], "parse_table")),
            SmallParseTable = ToUInt16Array(ResolveArray(unit, flattener, language.Members["small_parse_table"], "small_parse_table")),
            SmallParseTableMap = ToUInt32Array(ResolveArray(unit, flattener, language.Members["small_parse_table_map"], "small_parse_table_map")),
            ParseActions = ToParseActions(unit, ResolveArray(unit, flattener, language.Members["parse_actions"], "parse_actions")),
            SymbolNames = ToStringArray(ResolveArray(unit, flattener, language.Members["symbol_names"], "symbol_names")),
            FieldNames = ToStringArray(ResolveArray(unit, flattener, language.Members["field_names"], "field_names")),
            FieldMapSlices = ToMapSlices(ResolveArray(unit, flattener, language.Members["field_map_slices"], "field_map_slices")),
            FieldMapEntries = ToFieldMapEntries(ResolveArray(unit, flattener, language.Members["field_map_entries"], "field_map_entries")),
            SymbolMetadata = ToSymbolMetadata(ResolveArray(unit, flattener, language.Members["symbol_metadata"], "symbol_metadata")),
            PublicSymbolMap = ToUInt16Array(ResolveArray(unit, flattener, language.Members["public_symbol_map"], "public_symbol_map")),
            AliasMap = ToUInt16Array(ResolveArray(unit, flattener, language.Members["alias_map"], "alias_map")),
            AliasSequences = ToUInt16Array(ResolveArray(unit, flattener, language.Members["alias_sequences"], "alias_sequences")),
            LexModes = ToLexerModes(ResolveArray(unit, flattener, language.Members["lex_modes"], "lex_modes")),
            PrimaryStateIds = ToUInt16Array(ResolveArray(unit, flattener, language.Members["primary_state_ids"], "primary_state_ids")),
            ReservedWords = ToUInt16Array(ResolveArray(unit, flattener, language.Members["reserved_words"], "reserved_words")),
            SupertypeSymbols = ToUInt16Array(ResolveArray(unit, flattener, language.Members["supertype_symbols"], "supertype_symbols")),
            SupertypeMapSlices = ToMapSlices(ResolveArray(unit, flattener, language.Members["supertype_map_slices"], "supertype_map_slices")),
            SupertypeMapEntries = ToUInt16Array(ResolveArray(unit, flattener, language.Members["supertype_map_entries"], "supertype_map_entries")),
        };

        CFunction? lexFunction = BindLexFunction(unit, language.Members["lex_fn"]);
        CFunction? keywordLexFunction = BindLexFunction(unit, language.Members["keyword_lex_fn"]);
        tables.LexFn = lexFunction is null ? null : PlaceholderLexFunction;
        tables.KeywordLexFn = keywordLexFunction is null ? null : PlaceholderLexFunction;

        var scanner = (CAggregateValue)language.Members["external_scanner"];
        var externalFunctions = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["create"] = null,
            ["destroy"] = null,
            ["scan"] = null,
            ["serialize"] = null,
            ["deserialize"] = null,
        };
        tables.ExternalScannerStates = ToBoolArray(ResolveArray(unit, flattener, scanner.Members["states"], "external_scanner.states"));
        tables.ExternalScannerSymbolMap = ToUInt16Array(ResolveArray(unit, flattener, scanner.Members["symbol_map"], "external_scanner.symbol_map"));
        tables.ExternalScannerCreateIsSet = BindExternalFunction(unit, scanner.Members["create"], "create", externalFunctions);
        tables.ExternalScannerDestroyIsSet = BindExternalFunction(unit, scanner.Members["destroy"], "destroy", externalFunctions);
        tables.ExternalScannerScanIsSet = BindExternalFunction(unit, scanner.Members["scan"], "scan", externalFunctions);
        tables.ExternalScannerSerializeIsSet = BindExternalFunction(unit, scanner.Members["serialize"], "serialize", externalFunctions);
        tables.ExternalScannerDeserializeIsSet = BindExternalFunction(unit, scanner.Members["deserialize"], "deserialize", externalFunctions);

        var characterSets = new Dictionary<string, CharacterRange[]>(StringComparer.Ordinal);
        foreach (CVariable variable in unit.Variables)
        {
            if (string.Equals(variable.ElementType, "TSCharacterRange", StringComparison.Ordinal))
            {
                characterSets[variable.Name] = ToCharacterRanges(flattener.FlattenVariable(variable));
            }
        }

        LexProgram? lex = lexFunction is null ? null : LexBodyParser.Parse(lexFunction, unit.FileName);
        LexProgram? keywordLex = keywordLexFunction is null ? null : LexBodyParser.Parse(keywordLexFunction, unit.FileName);
        var symbolValues = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (LexProgram program in new[] { lex, keywordLex }.OfType<LexProgram>())
        {
            foreach (LexAccept accept in program.States.SelectMany(s => s.Statements).OfType<LexAccept>())
            {
                if (!symbolValues.ContainsKey(accept.Symbol))
                {
                    symbolValues[accept.Symbol] = evaluator.EvaluateInt(new CIdentifier(accept.Pos, accept.Symbol));
                }
            }
        }

        return new GrammarModel
        {
            Tables = tables,
            LexFunction = lexFunction,
            KeywordLexFunction = keywordLexFunction,
            Lex = lex,
            KeywordLex = keywordLex,
            SymbolValues = symbolValues,
            CharacterSets = characterSets,
            ExternalScannerFunctions = externalFunctions,
        };
    }

    /// <summary>Reads, parses and builds a parser.c file.</summary>
    public static GrammarModel Build(string parserCPath) => Build(CSourceFile.Parse(parserCPath));

    internal static CArrayValue? ResolveArray(CTranslationUnit unit, InitializerFlattener flattener, CValue value, string fieldName)
    {
        switch (value)
        {
            case CNullValue:
                return null;
            case CReferenceValue reference:
            {
                foreach (long index in reference.Indices)
                {
                    if (index != 0)
                    {
                        throw new CSourceException(
                            unit.FileName,
                            unit.Language.Language.Pos,
                            $"'{reference.Name}' must point to the start of the array for '{fieldName}'.");
                    }
                }

                foreach (CVariable variable in unit.Variables)
                {
                    if (string.Equals(variable.Name, reference.Name, StringComparison.Ordinal))
                    {
                        return flattener.FlattenVariable(variable);
                    }
                }

                throw new CSourceException(
                    unit.FileName,
                    unit.Language.Language.Pos,
                    $"'{reference.Name}' is not a file-level array variable.");
            }
            default:
                throw new CSourceException(
                    unit.FileName,
                    unit.Language.Language.Pos,
                    $"'{fieldName}' must be an array reference or NULL.");
        }
    }

    internal static CFunction? BindLexFunction(CTranslationUnit unit, CValue value)
    {
        switch (value)
        {
            case CNullValue:
                return null;
            case CReferenceValue reference:
            {
                foreach (CFunction function in unit.Functions)
                {
                    if (string.Equals(function.Name, reference.Name, StringComparison.Ordinal))
                    {
                        return function;
                    }
                }

                throw new CSourceException(
                    unit.FileName,
                    unit.Language.Language.Pos,
                    $"'{reference.Name}' is not a function of {unit.FileName}.");
            }
            default:
                throw new CSourceException(
                    unit.FileName,
                    unit.Language.Language.Pos,
                    "A lex function must be a function name or NULL.");
        }
    }

    internal static bool BindExternalFunction(CTranslationUnit unit, CValue value, string key, IDictionary<string, string?> functions)
    {
        switch (value)
        {
            case CNullValue:
                functions[key] = null;
                return false;
            case CReferenceValue reference when reference.Indices.Count == 0:
            {
                foreach (CPrototype prototype in unit.Prototypes)
                {
                    if (string.Equals(prototype.Name, reference.Name, StringComparison.Ordinal))
                    {
                        functions[key] = reference.Name;
                        return true;
                    }
                }

                throw new CSourceException(
                    unit.FileName,
                    unit.Language.Language.Pos,
                    $"'{reference.Name}' is not a prototype of {unit.FileName}.");
            }
            default:
                throw new CSourceException(
                    unit.FileName,
                    unit.Language.Language.Pos,
                    $"The external scanner function '{key}' must be a function name or NULL.");
        }
    }

    internal static ParseActionEntry[]? ToParseActions(CTranslationUnit unit, CArrayValue? array) =>
        array is null ? null : ConvertAll(array, value => ConvertAction(unit, value));

    internal static ParseActionEntry ConvertAction(CTranslationUnit unit, CValue value)
    {
        var entry = (CAggregateValue)value;
        if (entry.ActiveMember == "entry")
        {
            var header = (CAggregateValue)entry.Members["entry"];
            return ParseActionEntry.Entry((byte)MemberInt(header, "count"), MemberInt(header, "reusable") != 0);
        }

        var action = (CAggregateValue)entry.Members["action"];
        switch (action.ActiveMember)
        {
            case "shift":
            {
                var shift = (CAggregateValue)action.Members["shift"];
                return ParseActionEntry.Shift(
                    (ushort)MemberInt(shift, "state"),
                    MemberInt(shift, "extra") != 0,
                    MemberInt(shift, "repetition") != 0);
            }
            case "reduce":
            {
                var reduce = (CAggregateValue)action.Members["reduce"];
                return ParseActionEntry.Reduce(
                    (byte)MemberInt(reduce, "child_count"),
                    (ushort)MemberInt(reduce, "symbol"),
                    (short)MemberInt(reduce, "dynamic_precedence"),
                    (ushort)MemberInt(reduce, "production_id"));
            }
            default:
                switch (MemberInt(action, "type"))
                {
                    case 2:
                        return ParseActionEntry.Accept();
                    case 3:
                        return ParseActionEntry.Recover();
                    default:
                        throw new CSourceException(
                            unit.FileName,
                            unit.Language.Language.Pos,
                            "A TSParseAction type value must be 2 (accept) or 3 (recover).");
                }
        }
    }

    private static long MemberInt(CAggregateValue aggregate, string name) =>
        ((CIntValue)aggregate.Members[name]).Value;

    private static T[] ConvertAll<T>(CArrayValue array, Func<CValue, T> converter)
    {
        var result = new T[array.Elements.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = converter(array.Elements[i]);
        }

        return result;
    }

    private static ushort[]? ToUInt16Array(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value => (ushort)((CIntValue)value).Value);

    private static uint[]? ToUInt32Array(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value => (uint)((CIntValue)value).Value);

    private static bool[]? ToBoolArray(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value => ((CIntValue)value).Value != 0);

    private static string?[]? ToStringArray(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value => ToName(value));

    private static MapSlice[]? ToMapSlices(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value =>
        {
            var slice = (CAggregateValue)value;
            return new MapSlice(
                (ushort)((CIntValue)slice.Members["index"]).Value,
                (ushort)((CIntValue)slice.Members["length"]).Value);
        });

    private static FieldMapEntry[]? ToFieldMapEntries(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value =>
        {
            var entry = (CAggregateValue)value;
            return new FieldMapEntry(
                (ushort)((CIntValue)entry.Members["field_id"]).Value,
                (byte)((CIntValue)entry.Members["child_index"]).Value,
                ((CIntValue)entry.Members["inherited"]).Value != 0);
        });

    private static SymbolMetadata[]? ToSymbolMetadata(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value =>
        {
            var metadata = (CAggregateValue)value;
            return new SymbolMetadata(
                ((CIntValue)metadata.Members["visible"]).Value != 0,
                ((CIntValue)metadata.Members["named"]).Value != 0,
                ((CIntValue)metadata.Members["supertype"]).Value != 0);
        });

    private static LexerMode[]? ToLexerModes(CArrayValue? array) =>
        array is null ? null : ConvertAll(array, static value =>
        {
            var mode = (CAggregateValue)value;
            return new LexerMode(
                (ushort)((CIntValue)mode.Members["lex_state"]).Value,
                (ushort)((CIntValue)mode.Members["external_lex_state"]).Value,
                (ushort)((CIntValue)mode.Members["reserved_word_set_id"]).Value);
        });

    private static CharacterRange[] ToCharacterRanges(CArrayValue array) =>
        ConvertAll(array, static value =>
        {
            var range = (CAggregateValue)value;
            return new CharacterRange(
                (int)((CIntValue)range.Members["start"]).Value,
                (int)((CIntValue)range.Members["end"]).Value);
        });

    // The tables hold `const char *`: the runtime sees the string up to the first NUL byte
    // (tree-sitter-go names a token " ", which is "" for the runtime).
    private static string? ToName(CValue value)
    {
        if (value is not CStringValue text)
        {
            return null;
        }

        int end = Array.IndexOf(text.Bytes, (byte)0);
        return Encoding.UTF8.GetString(text.Bytes, 0, end < 0 ? text.Bytes.Length : end);
    }

    private static LanguageMetadata ToMetadata(CValue value)
    {
        var metadata = (CAggregateValue)value;
        return new LanguageMetadata(
            (byte)MemberInt(metadata, "major_version"),
            (byte)MemberInt(metadata, "minor_version"),
            (byte)MemberInt(metadata, "patch_version"));
    }
}
