// The C types of parser.h (tree-sitter v0.27.2, lib/src/parser.h) that appear in a generated
// parser.c, described as data. The initializer flattener uses them to apply C99 rules
// (member order, zero fill, unions). Member order and names must match parser.h exactly;
// ParserHTypesTests checks this against the vendored header.

// The names mirror the C type names on purpose.
#pragma warning disable CA1720

namespace TreeSitter.Generator.Model;

/// <summary>A C type.</summary>
public abstract record CTypeInfo(string Name);

/// <summary>Kinds of scalar values.</summary>
public enum CScalarKind
{
    UInt8,
    UInt16,
    UInt32,
    Int16,
    Int32,
    Bool,

    /// <summary>const char * (a string literal or NULL).</summary>
    CString,

    /// <summary>Pointer to data: an array name, &amp;array[0][0], a cast of those, or NULL.</summary>
    Pointer,

    /// <summary>Pointer to a function: a function name or NULL.</summary>
    FunctionPointer,
}

/// <summary>Scalar type.</summary>
public sealed record CScalarType(string Name, CScalarKind Kind) : CTypeInfo(Name);

/// <summary>Struct type; members in declaration order.</summary>
public sealed record CStructType(string Name, IReadOnlyList<CMember> Members) : CTypeInfo(Name);

/// <summary>Union type; members in declaration order. Without a designator, the first member is initialized (C99 6.7.8).</summary>
public sealed record CUnionType(string Name, IReadOnlyList<CMember> Members) : CTypeInfo(Name);

/// <summary>A member of a struct or union.</summary>
public sealed record CMember(string Name, CTypeInfo Type);

/// <summary>The parser.h types by the name used in parser.c declarations.</summary>
public static class ParserHTypes
{
    public static readonly CScalarType UInt8 = new("uint8_t", CScalarKind.UInt8);
    public static readonly CScalarType UInt16 = new("uint16_t", CScalarKind.UInt16);
    public static readonly CScalarType UInt32 = new("uint32_t", CScalarKind.UInt32);
    public static readonly CScalarType Int16 = new("int16_t", CScalarKind.Int16);
    public static readonly CScalarType Int32 = new("int32_t", CScalarKind.Int32);
    public static readonly CScalarType Bool = new("bool", CScalarKind.Bool);
    public static readonly CScalarType CString = new("const char *", CScalarKind.CString);
    public static readonly CScalarType Pointer = new("pointer", CScalarKind.Pointer);
    public static readonly CScalarType FunctionPointer = new("function pointer", CScalarKind.FunctionPointer);

    // typedef uint16_t TSStateId; TSSymbol; TSFieldId (parser.h / api.h)
    public static readonly CScalarType TSStateId = new("TSStateId", CScalarKind.UInt16);
    public static readonly CScalarType TSSymbol = new("TSSymbol", CScalarKind.UInt16);
    public static readonly CScalarType TSFieldId = new("TSFieldId", CScalarKind.UInt16);

    public static readonly CStructType TSFieldMapEntry = new("TSFieldMapEntry",
    [
        new("field_id", TSFieldId),
        new("child_index", UInt8),
        new("inherited", Bool),
    ]);

    public static readonly CStructType TSMapSlice = new("TSMapSlice",
    [
        new("index", UInt16),
        new("length", UInt16),
    ]);

    public static readonly CStructType TSSymbolMetadata = new("TSSymbolMetadata",
    [
        new("visible", Bool),
        new("named", Bool),
        new("supertype", Bool),
    ]);

    public static readonly CStructType TSLexerMode = new("TSLexerMode",
    [
        new("lex_state", UInt16),
        new("external_lex_state", UInt16),
        new("reserved_word_set_id", UInt16),
    ]);

    public static readonly CStructType TSCharacterRange = new("TSCharacterRange",
    [
        new("start", Int32),
        new("end", Int32),
    ]);

    public static readonly CStructType ShiftAction = new("TSParseAction.shift",
    [
        new("type", UInt8),
        new("state", TSStateId),
        new("extra", Bool),
        new("repetition", Bool),
    ]);

    public static readonly CStructType ReduceAction = new("TSParseAction.reduce",
    [
        new("type", UInt8),
        new("child_count", UInt8),
        new("symbol", TSSymbol),
        new("dynamic_precedence", Int16),
        new("production_id", UInt16),
    ]);

    public static readonly CUnionType TSParseAction = new("TSParseAction",
    [
        new("shift", ShiftAction),
        new("reduce", ReduceAction),
        new("type", UInt8),
    ]);

    public static readonly CStructType ActionEntryHeader = new("TSParseActionEntry.entry",
    [
        new("count", UInt8),
        new("reusable", Bool),
    ]);

    public static readonly CUnionType TSParseActionEntry = new("TSParseActionEntry",
    [
        new("action", TSParseAction),
        new("entry", ActionEntryHeader),
    ]);

    public static readonly CStructType TSLanguageMetadata = new("TSLanguageMetadata",
    [
        new("major_version", UInt8),
        new("minor_version", UInt8),
        new("patch_version", UInt8),
    ]);

    public static readonly CStructType ExternalScanner = new("TSLanguage.external_scanner",
    [
        new("states", Pointer),
        new("symbol_map", Pointer),
        new("create", FunctionPointer),
        new("destroy", FunctionPointer),
        new("scan", FunctionPointer),
        new("serialize", FunctionPointer),
        new("deserialize", FunctionPointer),
    ]);

    public static readonly CStructType TSLanguage = new("TSLanguage",
    [
        new("abi_version", UInt32),
        new("symbol_count", UInt32),
        new("alias_count", UInt32),
        new("token_count", UInt32),
        new("external_token_count", UInt32),
        new("state_count", UInt32),
        new("large_state_count", UInt32),
        new("production_id_count", UInt32),
        new("field_count", UInt32),
        new("max_alias_sequence_length", UInt16),
        new("parse_table", Pointer),
        new("small_parse_table", Pointer),
        new("small_parse_table_map", Pointer),
        new("parse_actions", Pointer),
        new("symbol_names", Pointer),
        new("field_names", Pointer),
        new("field_map_slices", Pointer),
        new("field_map_entries", Pointer),
        new("symbol_metadata", Pointer),
        new("public_symbol_map", Pointer),
        new("alias_map", Pointer),
        new("alias_sequences", Pointer),
        new("lex_modes", Pointer),
        new("lex_fn", FunctionPointer),
        new("keyword_lex_fn", FunctionPointer),
        new("keyword_capture_token", TSSymbol),
        new("external_scanner", ExternalScanner),
        new("primary_state_ids", Pointer),
        new("name", CString),
        new("reserved_words", Pointer),
        new("max_reserved_word_set_size", UInt16),
        new("supertype_count", UInt32),
        new("supertype_symbols", Pointer),
        new("supertype_map_slices", Pointer),
        new("supertype_map_entries", Pointer),
        new("metadata", TSLanguageMetadata),
    ]);

    /// <summary>
    /// The type for an element type text as CParser normalizes it (CVariable.ElementType).
    /// Throws KeyNotFoundException for unknown names.
    /// </summary>
    public static CTypeInfo Resolve(string elementType) => elementType switch
    {
        "uint8_t" => UInt8,
        "uint16_t" => UInt16,
        "uint32_t" => UInt32,
        "int16_t" => Int16,
        "int32_t" => Int32,
        "bool" => Bool,
        "char * const" => CString,
        "TSStateId" => TSStateId,
        "TSSymbol" => TSSymbol,
        "TSFieldId" => TSFieldId,
        "TSFieldMapEntry" => TSFieldMapEntry,
        "TSMapSlice" => TSMapSlice,
        "TSSymbolMetadata" => TSSymbolMetadata,
        "TSLexerMode" => TSLexerMode,
        "TSCharacterRange" => TSCharacterRange,
        "TSParseActionEntry" => TSParseActionEntry,
        "TSLanguage" => TSLanguage,
        _ => throw new KeyNotFoundException($"unknown C type '{elementType}'"),
    };
}
