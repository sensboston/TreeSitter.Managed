// Expansion of the parse table macros of parser.h. The definitions below are copied from
// tree-sitter v0.27.2 lib/src/parser.h; ParserHMacrosTests checks that they are equal to
// the vendored header (whitespace normalized). Expansion is token substitution of the
// parameters, then CParser reads the result.

using TreeSitter.Generator.CSource;

namespace TreeSitter.Generator.Model;

/// <summary>One function-like macro: parameter names and body text.</summary>
public sealed record MacroDefinition(string Name, IReadOnlyList<string> Parameters, string Body);

public static class MacroExpander
{
    /// <summary>Macros that expand to an expression: STATE, SMALL_STATE, ACTIONS.</summary>
    public static readonly IReadOnlyDictionary<string, MacroDefinition> ExpressionMacros = new Dictionary<string, MacroDefinition>(StringComparer.Ordinal)
    {
        ["SMALL_STATE"] = new("SMALL_STATE", ["id"], "((id) - LARGE_STATE_COUNT)"),
        ["STATE"] = new("STATE", ["id"], "id"),
        ["ACTIONS"] = new("ACTIONS", ["id"], "id"),
    };

    /// <summary>Macros that expand to an initializer of TSParseActionEntry.</summary>
    public static readonly IReadOnlyDictionary<string, MacroDefinition> InitializerMacros = new Dictionary<string, MacroDefinition>(StringComparer.Ordinal)
    {
        ["SHIFT"] = new("SHIFT", ["state_value"], "{{ .shift = { .type = TSParseActionTypeShift, .state = (state_value) } }}"),
        ["SHIFT_REPEAT"] = new("SHIFT_REPEAT", ["state_value"], "{{ .shift = { .type = TSParseActionTypeShift, .state = (state_value), .repetition = true } }}"),
        ["SHIFT_EXTRA"] = new("SHIFT_EXTRA", [], "{{ .shift = { .type = TSParseActionTypeShift, .extra = true } }}"),
        ["REDUCE"] = new("REDUCE", ["symbol_name", "children", "precedence", "prod_id"], "{{ .reduce = { .type = TSParseActionTypeReduce, .symbol = symbol_name, .child_count = children, .dynamic_precedence = precedence, .production_id = prod_id }, }}"),
        ["RECOVER"] = new("RECOVER", [], "{{ .type = TSParseActionTypeRecover }}"),
        ["ACCEPT_INPUT"] = new("ACCEPT_INPUT", [], "{{ .type = TSParseActionTypeAccept }}"),
    };

    /// <summary>
    /// Other names from parser.h used in parser.c values: the TSParseActionType enum and the
    /// builtin symbols (#define ts_builtin_sym_error ((TSSymbol)-1), ts_builtin_sym_end 0).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ObjectMacros = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ts_builtin_sym_error"] = "((TSSymbol)-1)",
        ["ts_builtin_sym_end"] = "0",
        ["TSParseActionTypeShift"] = "0",
        ["TSParseActionTypeReduce"] = "1",
        ["TSParseActionTypeAccept"] = "2",
        ["TSParseActionTypeRecover"] = "3",
    };

    private static readonly IReadOnlyDictionary<string, CExpr> NoParameters =
        new Dictionary<string, CExpr>(StringComparer.Ordinal);

    /// <summary>
    /// Expands an expression macro call (STATE, SMALL_STATE, ACTIONS). The body is tokenized
    /// and parsed with CParser.ParseExpression; parameter names stay CIdentifier nodes and are
    /// then replaced by the argument expressions. All result nodes get the call position.
    /// Throws CSourceException for a wrong argument count or an unknown macro name.
    /// </summary>
    public static CExpr ExpandExpression(CCall call, string fileName)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(fileName);
        if (!ExpressionMacros.TryGetValue(call.Name, out MacroDefinition? definition))
        {
            throw new CSourceException(fileName, call.Pos, $"Unknown expression macro '{call.Name}'.");
        }

        IReadOnlyDictionary<string, CExpr> parameters = BindArguments(definition, call, fileName);
        var tokens = new CTokenizer(definition.Body, fileName).Tokenize();
        var body = new CParser(tokens, fileName).ParseExpression();
        return Substitute(body, parameters, call.Pos);
    }

    /// <summary>
    /// Expands an initializer macro call (SHIFT, REDUCE, ...); the body is parsed with
    /// CParser.ParseInitializer and parameters are substituted at the tree level.
    /// </summary>
    public static CInitializer ExpandInitializer(CCall call, string fileName)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(fileName);
        if (!InitializerMacros.TryGetValue(call.Name, out MacroDefinition? definition))
        {
            throw new CSourceException(fileName, call.Pos, $"Unknown initializer macro '{call.Name}'.");
        }

        IReadOnlyDictionary<string, CExpr> parameters = BindArguments(definition, call, fileName);
        var tokens = new CTokenizer(definition.Body, fileName).Tokenize();
        var body = new CParser(tokens, fileName).ParseInitializer();
        return Substitute(body, parameters, call.Pos);
    }

    /// <summary>Parses an object macro value (ObjectMacros) into an expression with usePos on all nodes.</summary>
    public static CExpr ParseObjectMacro(string name, SourcePos usePos, string fileName)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fileName);
        if (!ObjectMacros.TryGetValue(name, out string? body))
        {
            throw new CSourceException(fileName, usePos, $"Unknown parser.h macro '{name}'.");
        }

        var tokens = new CTokenizer(body, fileName).Tokenize();
        var parsed = new CParser(tokens, fileName).ParseExpression();
        return Substitute(parsed, NoParameters, usePos);
    }

    private static Dictionary<string, CExpr> BindArguments(MacroDefinition definition, CCall call, string fileName)
    {
        if (call.Arguments.Count != definition.Parameters.Count)
        {
            throw new CSourceException(
                fileName,
                call.Pos,
                $"Macro '{call.Name}' expects {definition.Parameters.Count} argument(s), but {call.Arguments.Count} were given.");
        }

        var parameters = new Dictionary<string, CExpr>(StringComparer.Ordinal);
        for (int i = 0; i < definition.Parameters.Count; i++)
        {
            parameters[definition.Parameters[i]] = call.Arguments[i];
        }

        return parameters;
    }

    /// <summary>
    /// Replaces every CIdentifier that names a parameter with the corresponding argument and
    /// puts pos on every node of the result. Arguments are copied without further substitution.
    /// </summary>
    internal static CExpr Substitute(CExpr expression, IReadOnlyDictionary<string, CExpr> parameters, SourcePos pos)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(parameters);

        if (expression is CIdentifier identifier &&
            parameters.TryGetValue(identifier.Name, out CExpr? argument) && argument is not null)
        {
            return Substitute(argument, NoParameters, pos);
        }

        CExpr result = expression switch
        {
            CUnary unary => unary with { Operand = Substitute(unary.Operand, parameters, pos) },
            CBinary binary => binary with
            {
                Left = Substitute(binary.Left, parameters, pos),
                Right = Substitute(binary.Right, parameters, pos),
            },
            CCast cast => cast with { Operand = Substitute(cast.Operand, parameters, pos) },
            CIndex index => index with
            {
                Target = Substitute(index.Target, parameters, pos),
                Index = Substitute(index.Index, parameters, pos),
            },
            CCall nested => nested with
            {
                Arguments = nested.Arguments.Select(child => Substitute(child, parameters, pos)).ToList(),
            },
            _ => expression,
        };
        return result with { Pos = pos };
    }

    internal static CInitializer Substitute(CInitializer initializer, IReadOnlyDictionary<string, CExpr> parameters, SourcePos pos)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(parameters);

        CInitializer result = initializer switch
        {
            CInitExpr expression => expression with { Expr = Substitute(expression.Expr, parameters, pos) },
            CInitList list => list with
            {
                Items = list.Items.Select(item => Substitute(item, parameters, pos)).ToList(),
            },
            _ => initializer,
        };
        return result with { Pos = pos };
    }

    internal static CInitItem Substitute(CInitItem item, IReadOnlyDictionary<string, CExpr> parameters, SourcePos pos)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(parameters);

        return item with
        {
            Pos = pos,
            Designators = item.Designators.Select(designator => Substitute(designator, parameters, pos)).ToList(),
            Value = Substitute(item.Value, parameters, pos),
        };
    }

    internal static CDesignator Substitute(CDesignator designator, IReadOnlyDictionary<string, CExpr> parameters, SourcePos pos)
    {
        ArgumentNullException.ThrowIfNull(designator);
        ArgumentNullException.ThrowIfNull(parameters);

        CDesignator result = designator switch
        {
            CIndexDesignator index => index with { Index = Substitute(index.Index, parameters, pos) },
            _ => designator,
        };
        return result with { Pos = pos };
    }
}
