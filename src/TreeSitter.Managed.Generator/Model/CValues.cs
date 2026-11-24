// Values produced by ConstEvaluator and InitializerFlattener.

namespace TreeSitter.Generator.Model;

/// <summary>A compile-time value of parser.c.</summary>
public abstract record CValue;

/// <summary>Integer (also bool and char). Already converted to the target scalar type when it came from an initializer.</summary>
public sealed record CIntValue(long Value) : CValue;

/// <summary>String literal bytes (without the terminating zero).</summary>
public sealed record CStringValue(byte[] Bytes) : CValue;

/// <summary>NULL, or a pointer / string member that was not initialized.</summary>
public sealed record CNullValue : CValue
{
    public static readonly CNullValue Instance = new();
}

/// <summary>
/// A reference to a file-level variable or function by name: ts_lex, ts_symbol_names,
/// &amp;ts_parse_table[0][0], (const void*)ts_lex_modes. Indices are the subscripts after the
/// name (empty when there are none).
/// </summary>
public sealed record CReferenceValue(string Name, IReadOnlyList<long> Indices) : CValue;

/// <summary>
/// A struct value with all members present (missing ones zero filled), or a union value
/// with exactly one member: the one that was initialized (ActiveMember).
/// </summary>
public sealed record CAggregateValue(CTypeInfo Type, IReadOnlyDictionary<string, CValue> Members, string? ActiveMember) : CValue;

/// <summary>A flattened array: multi-dimensional C arrays become one list in row-major order.</summary>
public sealed record CArrayValue(CTypeInfo ElementType, IReadOnlyList<int> Dimensions, IReadOnlyList<CValue> Elements) : CValue;
