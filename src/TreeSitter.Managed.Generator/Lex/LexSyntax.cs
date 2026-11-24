// Syntax tree of a generated lex function (ts_lex, ts_lex_keywords) of parser.c.
//
// The body of every such function in the generated files has this form (macros of
// parser.h written out):
//   START_LEXER();
//   eof = lexer->eof(lexer);
//   switch (state) {
//     case N:
//       statements...
//       END_STATE();
//     ...
//     default:
//       return false;
//   }
// A statement is one of:
//   if (CONDITION) ADVANCE(N);   if (CONDITION) SKIP(N);
//   ADVANCE_MAP(c1, s1, c2, s2, ...);      (pairs: character or integer, state)
//   ACCEPT_TOKEN(SYMBOL);
// A condition is built from: lookahead, eof, character and integer literals, the six
// comparisons, !, &&, || and parentheses, and set_contains(SET_NAME, LENGTH, lookahead).
// Statement order is kept: the first matching statement wins at run time, as in C.

using TreeSitter.Generator.CSource;

namespace TreeSitter.Generator.Lex;

/// <summary>One lex function.</summary>
public sealed record LexProgram(string FunctionName, IReadOnlyList<LexState> States);

/// <summary>One case of the switch. Statements in source order, without the final END_STATE.</summary>
public sealed record LexState(SourcePos Pos, int Number, IReadOnlyList<LexStatement> Statements);

public abstract record LexStatement(SourcePos Pos);

/// <summary>if (Condition) ADVANCE(Target); or SKIP(Target) when Skip is true.</summary>
public sealed record LexIf(SourcePos Pos, LexCondition Condition, int Target, bool Skip) : LexStatement(Pos);

/// <summary>ADVANCE_MAP(...): Keys[i] goes to Targets[i]; first match wins.</summary>
public sealed record LexAdvanceMap(SourcePos Pos, IReadOnlyList<int> Keys, IReadOnlyList<int> Targets) : LexStatement(Pos);

/// <summary>ACCEPT_TOKEN(Symbol): Symbol is the name as written (an enum member of parser.c).</summary>
public sealed record LexAccept(SourcePos Pos, string Symbol) : LexStatement(Pos);

public abstract record LexCondition(SourcePos Pos);

/// <summary>The variable lookahead.</summary>
public sealed record LexLookahead(SourcePos Pos) : LexCondition(Pos);

/// <summary>The variable eof.</summary>
public sealed record LexEof(SourcePos Pos) : LexCondition(Pos);

/// <summary>A character or integer literal (characters already unescaped to their value).</summary>
public sealed record LexConstant(SourcePos Pos, int Value) : LexCondition(Pos);

/// <summary>Comparison: Operator is one of "==", "!=", "&lt;", "&lt;=", "&gt;", "&gt;=".</summary>
public sealed record LexCompare(SourcePos Pos, string Operator, LexCondition Left, LexCondition Right) : LexCondition(Pos);

/// <summary>&amp;&amp; (IsAnd) or || of two conditions.</summary>
public sealed record LexLogical(SourcePos Pos, bool IsAnd, LexCondition Left, LexCondition Right) : LexCondition(Pos);

/// <summary>!Operand.</summary>
public sealed record LexNot(SourcePos Pos, LexCondition Operand) : LexCondition(Pos);

/// <summary>set_contains(SetName, Length, lookahead).</summary>
public sealed record LexSetContains(SourcePos Pos, string SetName, int Length) : LexCondition(Pos);
