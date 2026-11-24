// Translates a LexProgram into a C# method with the behavior of the parser.h lexer macros
// (tree-sitter v0.27.2):
//   START_LEXER()       result = false, skip = false, then the loop:
//                       next_state: lexer.Advance(skip); start: skip = false; lookahead = ...
//   eof = lexer->eof()  runs on every pass, right after START_LEXER, as in parser.c.
//   ADVANCE(n)          state = n; goto next_state;
//   SKIP(n)             skip = true; state = n; goto next_state;
//   ADVANCE_MAP(...)    LexHelpers.AdvanceMap over a static ushort span (C: uint16_t map[]).
//   ACCEPT_TOKEN(sym)   result = true; lexer.ResultSymbol = sym; lexer.MarkEnd();
//   END_STATE()         return result;
//   default:            return false;
// Conditions keep the C types: lookahead and literals are int, eof and the results of
// comparisons, !, &&, || and set_contains are bool. An int used as a truth value becomes
// (x != 0), a bool used as an int becomes (x ? 1 : 0), as in C.
// Values that do not fit the C types (uint16_t map entries, TSStateId targets, TSSymbol)
// are errors, not truncated.

using System.Globalization;
using TreeSitter.Abi;

namespace TreeSitter.Generator.Lex;

/// <summary>A static data member needed by an emitted lex function.</summary>
public sealed record LexData(string Name, string ElementType, IReadOnlyList<ulong> Values);

/// <summary>Lines of one emitted method (indented relative to the class body) and its data.</summary>
public sealed record LexEmitResult(IReadOnlyList<string> MethodLines, IReadOnlyList<LexData> Data);

public static class LexEmitter
{
    private const string Indent = "    ";

    /// <summary>
    /// Emits <c>private static bool {methodName}(Lexer lexer, ushort state)</c>. Character
    /// sets are referenced by <see cref="CharacterSetName"/> and emitted by
    /// <see cref="EmitCharacterSets"/>, once for all lex functions of a grammar.
    /// </summary>
    public static LexEmitResult Emit(
        LexProgram program,
        string methodName,
        IReadOnlyDictionary<string, long> symbolValues,
        IReadOnlyDictionary<string, CharacterRange[]> characterSets)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(symbolValues);
        ArgumentNullException.ThrowIfNull(characterSets);

        var lines = new List<string>();
        var data = new List<LexData>();
        List<List<LexState>> chunks = Chunks(program.States);
        if (chunks.Count == 1)
        {
            EmitSingle(lines, data, program.States, methodName, symbolValues, characterSets);
        }
        else
        {
            EmitSplit(lines, data, chunks, methodName, symbolValues, characterSets);
        }

        return new LexEmitResult(lines, data);
    }

    /// <summary>
    /// The most lex statements (if, ADVANCE_MAP, ACCEPT_TOKEN, END_STATE) in one method.
    /// RyuJIT compiles a method with too many basic blocks or locals with MinOpts (no
    /// optimization): measured, the C lexer (324 states, about 1000 statements) is compiled
    /// with MinOpts, the Python lexer (150 states, about 700 statements) is not. A lex
    /// function above this limit is split into chunk methods of consecutive states.
    /// </summary>
    internal const int MaxStatementsPerMethod = 400;

    private static List<List<LexState>> Chunks(IReadOnlyList<LexState> states)
    {
        var chunks = new List<List<LexState>> { new List<LexState>() };
        int count = 0;
        foreach (LexState state in states)
        {
            int size = state.Statements.Count + 1;
            if (count > 0 && count + size > MaxStatementsPerMethod)
            {
                chunks.Add(new List<LexState>());
                count = 0;
            }
            chunks[^1].Add(state);
            count += size;
        }
        return chunks;
    }

    /// <summary>One method: the loop of START_LEXER around one switch (state).</summary>
    private static void EmitSingle(
        List<string> lines,
        List<LexData> data,
        IReadOnlyList<LexState> states,
        string methodName,
        IReadOnlyDictionary<string, long> symbolValues,
        IReadOnlyDictionary<string, CharacterRange[]> characterSets)
    {
        lines.Add($"private static bool {methodName}(Lexer lexer, ushort state)");
        lines.Add("{");
        lines.Add(Indent + "bool result = false;");
        lines.Add(Indent + "bool skip = false;");
        lines.Add(Indent + "bool eof;");
        lines.Add(Indent + "int lookahead;");
        lines.Add(Indent + "goto start;");
        lines.Add("next_state:");
        lines.Add(Indent + "lexer.Advance(skip);");
        lines.Add("start:");
        lines.Add(Indent + "skip = false;");
        lines.Add(Indent + "lookahead = lexer.Lookahead;");
        lines.Add(Indent + "eof = lexer.Eof();");
        lines.Add(Indent + "switch (state)");
        lines.Add(Indent + "{");
        var jumps = new Jumps("goto next_state;", "return result;", "return false;");
        EmitCases(lines, data, states, methodName, symbolValues, characterSets, jumps);
        lines.Add(Indent + "}");
        lines.Add("}");
    }

    /// <summary>
    /// Split form: the driver keeps the START_LEXER loop and calls the chunk method of the
    /// state; a chunk method runs the same statements and returns -1 for goto next_state,
    /// 1 or 0 for return result, 0 for the default case. The order of the calls on the
    /// lexer (Advance, Lookahead, Eof, MarkEnd) is the same as in the single form.
    /// </summary>
    private static void EmitSplit(
        List<string> lines,
        List<LexData> data,
        List<List<LexState>> chunks,
        string methodName,
        IReadOnlyDictionary<string, long> symbolValues,
        IReadOnlyDictionary<string, CharacterRange[]> characterSets)
    {
        lines.Add($"private static bool {methodName}(Lexer lexer, ushort state)");
        lines.Add("{");
        lines.Add(Indent + "bool result = false;");
        lines.Add(Indent + "bool skip = false;");
        lines.Add(Indent + "int next;");
        lines.Add(Indent + "goto start;");
        lines.Add("next_state:");
        lines.Add(Indent + "lexer.Advance(skip);");
        lines.Add("start:");
        lines.Add(Indent + "skip = false;");
        for (int i = 0; i < chunks.Count; i++)
        {
            string call = $"next = {methodName}{Int(i)}(lexer.Lookahead, lexer.Eof(), lexer, ref state, ref skip, ref result);";
            if (i == chunks.Count - 1)
            {
                lines.Add(Indent + (i == 0 ? string.Empty : "else ") + "{ " + call + " }");
            }
            else
            {
                int end = chunks[i + 1][0].Number;
                lines.Add(Indent + (i == 0 ? string.Empty : "else ") + "if (state < " + Int(end) + ") { " + call + " }");
            }
        }
        lines.Add(Indent + "if (next < 0)");
        lines.Add(Indent + "{");
        lines.Add(Indent + Indent + "goto next_state;");
        lines.Add(Indent + "}");
        lines.Add(Indent + "return next != 0;");
        lines.Add("}");

        var jumps = new Jumps("return -1;", "return result ? 1 : 0;", "return 0;");
        for (int i = 0; i < chunks.Count; i++)
        {
            lines.Add(string.Empty);
            lines.Add($"private static int {methodName}{Int(i)}(int lookahead, bool eof, Lexer lexer, ref ushort state, ref bool skip, ref bool result)");
            lines.Add("{");
            lines.Add(Indent + "switch (state)");
            lines.Add(Indent + "{");
            EmitCases(lines, data, chunks[i], methodName, symbolValues, characterSets, jumps);
            lines.Add(Indent + "}");
            lines.Add("}");
        }
    }

    /// <summary>The C# text of the three ways out of a state.</summary>
    private sealed record Jumps(string NextState, string EndState, string Default);

    private static void EmitCases(
        List<string> lines,
        List<LexData> data,
        IReadOnlyList<LexState> states,
        string methodName,
        IReadOnlyDictionary<string, long> symbolValues,
        IReadOnlyDictionary<string, CharacterRange[]> characterSets,
        Jumps jumps)
    {
        string caseIndent = Indent + Indent;
        string bodyIndent = caseIndent + Indent;
        foreach (LexState state in states)
        {
            lines.Add(caseIndent + "case " + Int(state.Number) + ":");
            foreach (LexStatement statement in state.Statements)
            {
                switch (statement)
                {
                    case LexIf lexIf:
                    {
                        string condition = Bool(lexIf.Condition, characterSets);
                        string target = StateId(lexIf.Target, lexIf);
                        lines.Add(bodyIndent + "if (" + Strip(condition) + ")");
                        lines.Add(bodyIndent + "{");
                        if (lexIf.Skip)
                        {
                            lines.Add(bodyIndent + Indent + "skip = true;");
                        }
                        lines.Add(bodyIndent + Indent + "state = " + target + ";");
                        lines.Add(bodyIndent + Indent + jumps.NextState);
                        lines.Add(bodyIndent + "}");
                        break;
                    }
                    case LexAdvanceMap map:
                    {
                        string name = methodName + "Map" + Int(data.Count);
                        var values = new List<ulong>(map.Keys.Count * 2);
                        for (int i = 0; i < map.Keys.Count; i++)
                        {
                            values.Add(UInt16(map.Keys[i], map, "ADVANCE_MAP key"));
                            values.Add(UInt16(map.Targets[i], map, "ADVANCE_MAP state"));
                        }
                        data.Add(new LexData(name, "ushort", values));
                        lines.Add(bodyIndent + "if (LexHelpers.AdvanceMap(" + name + ", lookahead, ref state))");
                        lines.Add(bodyIndent + "{");
                        lines.Add(bodyIndent + Indent + jumps.NextState);
                        lines.Add(bodyIndent + "}");
                        break;
                    }
                    case LexAccept accept:
                    {
                        if (!symbolValues.TryGetValue(accept.Symbol, out long symbol))
                        {
                            throw Error(accept, $"ACCEPT_TOKEN symbol '{accept.Symbol}' has no value.");
                        }
                        string value = UInt16(symbol, accept, "ACCEPT_TOKEN symbol")
                            .ToString(CultureInfo.InvariantCulture);
                        lines.Add(bodyIndent + "result = true;");
                        lines.Add(bodyIndent + "lexer.ResultSymbol = " + value + "; // " + accept.Symbol);
                        lines.Add(bodyIndent + "lexer.MarkEnd();");
                        break;
                    }
                    default:
                        throw Error(statement, $"Unknown lex statement {statement.GetType().Name}.");
                }
            }
            lines.Add(bodyIndent + jumps.EndState);
        }
        lines.Add(caseIndent + "default:");
        lines.Add(bodyIndent + jumps.Default);
    }

    /// <summary>Name of the static span of a character set.</summary>
    public static string CharacterSetName(string setName)
    {
        ArgumentNullException.ThrowIfNull(setName);
        return "CharacterSet_" + setName;
    }

    /// <summary>The character sets used by the programs (start, end pairs), in name order.</summary>
    public static IReadOnlyList<LexData> EmitCharacterSets(
        IEnumerable<LexProgram> programs,
        IReadOnlyDictionary<string, CharacterRange[]> characterSets)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(characterSets);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (LexProgram program in programs)
        {
            foreach (LexState state in program.States)
            {
                foreach (LexStatement statement in state.Statements)
                {
                    if (statement is LexIf lexIf)
                    {
                        CollectSets(lexIf.Condition, used);
                    }
                }
            }
        }

        var result = new List<LexData>();
        foreach (string name in used)
        {
            var values = new List<ulong>();
            foreach (CharacterRange range in characterSets[name])
            {
                if (range.Start < 0 || range.End < 0)
                {
                    throw new InvalidOperationException($"Character set '{name}' has a negative bound.");
                }
                values.Add((ulong)range.Start);
                values.Add((ulong)range.End);
            }
            result.Add(new LexData(CharacterSetName(name), "int", values));
        }
        return result;
    }

    private static void CollectSets(LexCondition condition, ISet<string> used)
    {
        switch (condition)
        {
            case LexSetContains set:
                used.Add(set.SetName);
                break;
            case LexCompare compare:
                CollectSets(compare.Left, used);
                CollectSets(compare.Right, used);
                break;
            case LexLogical logical:
                CollectSets(logical.Left, used);
                CollectSets(logical.Right, used);
                break;
            case LexNot not:
                CollectSets(not.Operand, used);
                break;
        }
    }

    // A condition as a C# bool expression.
    private static string Bool(LexCondition condition, IReadOnlyDictionary<string, CharacterRange[]> sets) =>
        condition switch
        {
            LexEof => "eof",
            LexCompare compare =>
                "(" + IntExpr(compare.Left, sets) + " " + compare.Operator + " " + IntExpr(compare.Right, sets) + ")",
            LexLogical logical =>
                "(" + Bool(logical.Left, sets) + (logical.IsAnd ? " && " : " || ") + Bool(logical.Right, sets) + ")",
            LexNot not => "!" + Bool(not.Operand, sets),
            LexSetContains set => SetContains(set, sets),
            _ => "(" + IntExpr(condition, sets) + " != 0)",
        };

    // A condition as a C# int expression.
    private static string IntExpr(LexCondition condition, IReadOnlyDictionary<string, CharacterRange[]> sets) =>
        condition switch
        {
            LexLookahead => "lookahead",
            LexConstant constant => Constant(constant.Value),
            _ => "(" + Bool(condition, sets) + " ? 1 : 0)",
        };

    private static string SetContains(LexSetContains set, IReadOnlyDictionary<string, CharacterRange[]> sets)
    {
        if (!sets.TryGetValue(set.SetName, out CharacterRange[]? ranges))
        {
            throw Error(set, $"Character set '{set.SetName}' is not a TSCharacterRange array of the file.");
        }
        if (set.Length != ranges.Length)
        {
            throw Error(set, $"set_contains length {set.Length} differs from the size {ranges.Length} of '{set.SetName}'.");
        }
        return "LexHelpers.SetContains(" + CharacterSetName(set.SetName) + ", " + Int(set.Length) + ", lookahead)";
    }

    // Printable ASCII as a C# char literal (an int in comparisons), everything else as a number.
    private static string Constant(int value)
    {
        if (value is >= 0x20 and < 0x7F && value != '\'' && value != '\\')
        {
            return "'" + (char)value + "'";
        }
        return Int(value);
    }

    private static string StateId(int value, LexStatement statement) =>
        UInt16(value, statement, "state").ToString(CultureInfo.InvariantCulture);

    private static ulong UInt16(long value, LexStatement statement, string what) =>
        value is >= 0 and <= ushort.MaxValue
            ? (ulong)value
            : throw Error(statement, $"{what} {value} does not fit uint16_t.");

    private static string Strip(string condition) =>
        condition.Length > 1 && condition[0] == '(' && condition[^1] == ')' && IsOneGroup(condition)
            ? condition[1..^1]
            : condition;

    // True when the opening parenthesis at 0 closes at the last character.
    private static bool IsOneGroup(string text)
    {
        int depth = 0;
        bool inChar = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inChar)
            {
                if (c == '\'')
                {
                    inChar = false;
                }
                continue;
            }
            if (c == '\'')
            {
                inChar = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0 && i != text.Length - 1)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static InvalidOperationException Error(LexStatement statement, string message) =>
        new(Position(statement.Pos) + message);

    private static InvalidOperationException Error(LexCondition condition, string message) =>
        new(Position(condition.Pos) + message);

    private static string Position(CSource.SourcePos pos) =>
        string.Create(CultureInfo.InvariantCulture, $"{pos.Line}:{pos.Column}: ");
}
