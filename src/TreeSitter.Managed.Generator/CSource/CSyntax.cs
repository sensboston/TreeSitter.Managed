// Syntax tree for the subset of C that tree-sitter writes into a generated parser.c.
// The reader (CParser) builds it; nothing here evaluates values. Evaluation is done by
// Model/ConstEvaluator and Model/InitializerFlattener.

namespace TreeSitter.Generator.CSource;

/// <summary>A position in a source file, 1-based.</summary>
public readonly record struct SourcePos(int Line, int Column);

// ---------------------------------------------------------------- expressions

/// <summary>An expression inside an initializer, a designator, an enum or a #define.</summary>
public abstract record CExpr(SourcePos Pos);

/// <summary>Integer literal. Text keeps the original spelling (for messages).</summary>
public sealed record CIntLiteral(SourcePos Pos, long Value, string Text) : CExpr(Pos);

/// <summary>Character literal such as 'a' or '\n', already unescaped.</summary>
public sealed record CCharLiteral(SourcePos Pos, int Value) : CExpr(Pos);

/// <summary>String literal, already unescaped. Bytes, because C escapes are byte based.</summary>
public sealed record CStringLiteral(SourcePos Pos, byte[] Bytes) : CExpr(Pos);

/// <summary>Identifier: enum member, #define name, array name, true/false, NULL.</summary>
public sealed record CIdentifier(SourcePos Pos, string Name) : CExpr(Pos);

/// <summary>Function-like macro call: SHIFT(3), REDUCE(sym_x, 2, 0, 1), STATE(5), ACTIONS(7).</summary>
public sealed record CCall(SourcePos Pos, string Name, IReadOnlyList<CExpr> Arguments) : CExpr(Pos);

/// <summary>Unary operator: '-' (negation) or '&amp;' (address of).</summary>
public sealed record CUnary(SourcePos Pos, char Operator, CExpr Operand) : CExpr(Pos);

/// <summary>Binary '+' or '-' (left associative), for example the body of SMALL_STATE: ((id) - LARGE_STATE_COUNT).</summary>
public sealed record CBinary(SourcePos Pos, char Operator, CExpr Left, CExpr Right) : CExpr(Pos);

/// <summary>Cast such as (TSSymbol)-1 or (const void*)ts_lex_modes. TypeName is the text between the parentheses with single spaces.</summary>
public sealed record CCast(SourcePos Pos, string TypeName, CExpr Operand) : CExpr(Pos);

/// <summary>Array subscript such as ts_parse_table[0][0] (nested for more dimensions).</summary>
public sealed record CIndex(SourcePos Pos, CExpr Target, CExpr Index) : CExpr(Pos);

// ---------------------------------------------------------------- initializers

/// <summary>A C99 initializer: a single expression or a brace list.</summary>
public abstract record CInitializer(SourcePos Pos);

/// <summary>Initializer that is one expression.</summary>
public sealed record CInitExpr(SourcePos Pos, CExpr Expr) : CInitializer(Pos);

/// <summary>Brace-enclosed initializer list. A trailing comma is allowed and not stored.</summary>
public sealed record CInitList(SourcePos Pos, IReadOnlyList<CInitItem> Items) : CInitializer(Pos);

/// <summary>One item of a list: optional designators ([i], .field, chained) and a value.</summary>
public sealed record CInitItem(SourcePos Pos, IReadOnlyList<CDesignator> Designators, CInitializer Value);

/// <summary>Designator of a list item.</summary>
public abstract record CDesignator(SourcePos Pos);

/// <summary>Index designator [expr].</summary>
public sealed record CIndexDesignator(SourcePos Pos, CExpr Index) : CDesignator(Pos);

/// <summary>Field designator .name.</summary>
public sealed record CFieldDesignator(SourcePos Pos, string Name) : CDesignator(Pos);

// ---------------------------------------------------------------- declarations

/// <summary>Object-like #define NAME tokens... Body is the token list after the name (may be empty). Function-like defines are an error.</summary>
public sealed record CDefine(SourcePos Pos, string Name, IReadOnlyList<CToken> Body);

/// <summary>enum name { A, B = 5, ... };</summary>
public sealed record CEnumDeclaration(SourcePos Pos, string Name, IReadOnlyList<CEnumMember> Members);

/// <summary>One enum member; Value is null when it is not given (previous + 1).</summary>
public sealed record CEnumMember(SourcePos Pos, string Name, CExpr? Value);

/// <summary>
/// static const TYPE NAME[D1][D2] = INIT; at file level, or the static const TSLanguage
/// object inside tree_sitter_NAME. ElementType is the normalized type text, for example
/// "uint16_t", "TSSymbol", "char * const", "TSParseActionEntry", "bool". Dimensions holds one
/// entry per [], null for [] without a size. Scalars (no []) have no dimensions.
/// </summary>
public sealed record CVariable(SourcePos Pos, string ElementType, string Name, IReadOnlyList<CExpr?> Dimensions, CInitializer Initializer);

/// <summary>A function definition kept as tokens (ts_lex, ts_lex_keywords). Body excludes the outer braces.</summary>
public sealed record CFunction(SourcePos Pos, string ReturnType, string Name, string Parameters, IReadOnlyList<CToken> Body);

/// <summary>A function prototype (external scanner functions).</summary>
public sealed record CPrototype(SourcePos Pos, string ReturnType, string Name, string Parameters);

/// <summary>The language function: TS_PUBLIC const TSLanguage *tree_sitter_NAME(void) { static const TSLanguage language = INIT; return &amp;language; }</summary>
public sealed record CLanguageFunction(SourcePos Pos, string FunctionName, CVariable Language);

/// <summary>Everything read from one parser.c, in source order per kind.</summary>
public sealed record CTranslationUnit(
    string FileName,
    IReadOnlyList<CDefine> Defines,
    IReadOnlyList<CEnumDeclaration> Enums,
    IReadOnlyList<CVariable> Variables,
    IReadOnlyList<CFunction> Functions,
    IReadOnlyList<CPrototype> Prototypes,
    CLanguageFunction Language);
