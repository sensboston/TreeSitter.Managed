// Evaluates constant expressions of one parser.c.
//
// Names are looked up in this order: enum members of the file (an enum member without a
// value is previous + 1, the first is 0), object-like #defines of the file (body parsed as
// an expression), MacroExpander.ObjectMacros (parser.h), true = 1, false = 0, NULL = null.
// Any other name that is a file-level variable or function becomes a CReferenceValue.
// Anything else is a CSourceException at the position of the name.
//
// Expression macro calls (STATE, SMALL_STATE, ACTIONS) are expanded with MacroExpander.
// Initializer macro calls (SHIFT, REDUCE, ...) are not expressions: evaluating one here is an
// error; InitializerFlattener expands them.
//
// Casts: to an integer type (TSSymbol, TSStateId, TSFieldId, uint8_t, uint16_t, uint32_t,
// int16_t, int32_t) convert like C (truncate to the width, then sign-extend for signed
// types); to a pointer type ("const void *" and other "... *") keep the operand value,
// which must be a reference. '-' negates an integer; binary '+' and '-' (CBinary) work on
// integers. '&' takes a reference (only of a name or
// an index of a name). An index on a reference appends the subscript to Indices.

using TreeSitter.Generator.CSource;

namespace TreeSitter.Generator.Model;

public sealed class ConstEvaluator
{
    public ConstEvaluator(CTranslationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        Unit = unit;
    }

    public CTranslationUnit Unit { get; }

    /// <summary>Evaluates an expression to CIntValue, CStringValue, CNullValue or CReferenceValue.</summary>
    public CValue Evaluate(CExpr expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        return expr switch
        {
            CIntLiteral literal => new CIntValue(literal.Value),
            CCharLiteral literal => new CIntValue(literal.Value),
            CStringLiteral literal => new CStringValue(literal.Bytes),
            CIdentifier identifier => LookupName(identifier),
            CCall call => EvaluateCall(call),
            CUnary unary => EvaluateUnary(unary),
            CBinary binary => EvaluateBinary(binary),
            CCast cast => EvaluateCast(cast),
            CIndex index => EvaluateIndex(index),
            _ => throw Error(expr.Pos, "Unsupported expression kind."),
        };
    }

    /// <summary>Evaluates an expression that must be an integer.</summary>
    public long EvaluateInt(CExpr expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        return Evaluate(expr) is CIntValue integer
            ? integer.Value
            : throw Error(expr.Pos, "An integer value is required here.");
    }

    /// <summary>Value of an object-like #define of this file, evaluated as an integer.</summary>
    public long DefineValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        CDefine? define = FindDefine(name);
        return define is null
            ? throw new KeyNotFoundException($"'{name}' is not an object-like #define of {Unit.FileName}.")
            : EvaluateInt(ParseDefineBody(define));
    }

    private CValue LookupName(CIdentifier identifier)
    {
        string name = identifier.Name;

        foreach (CEnumDeclaration declaration in Unit.Enums)
        {
            long value = -1;
            foreach (CEnumMember member in declaration.Members)
            {
                value = member.Value is null ? value + 1 : EvaluateInt(member.Value);
                if (string.Equals(member.Name, name, StringComparison.Ordinal))
                {
                    return new CIntValue(value);
                }
            }
        }

        CDefine? define = FindDefine(name);
        if (define is not null)
        {
            return Evaluate(ParseDefineBody(define));
        }

        if (MacroExpander.ObjectMacros.ContainsKey(name))
        {
            return Evaluate(MacroExpander.ParseObjectMacro(name, identifier.Pos, Unit.FileName));
        }

        switch (name)
        {
            case "true":
                return new CIntValue(1);
            case "false":
                return new CIntValue(0);
            case "NULL":
                return CNullValue.Instance;
        }

        if (IsFileLevelName(name))
        {
            return new CReferenceValue(name, Array.Empty<long>());
        }

        throw Error(identifier.Pos, $"Unknown name '{name}'.");
    }

    private CValue EvaluateCall(CCall call)
    {
        if (MacroExpander.ExpressionMacros.ContainsKey(call.Name))
        {
            return Evaluate(MacroExpander.ExpandExpression(call, Unit.FileName));
        }

        throw Error(call.Pos, MacroExpander.InitializerMacros.ContainsKey(call.Name)
            ? $"'{call.Name}' is an initializer macro and cannot be used as an expression."
            : $"Unknown macro or function call '{call.Name}'.");
    }

    private CValue EvaluateUnary(CUnary unary)
    {
        return unary.Operator switch
        {
            '-' => new CIntValue(-EvaluateInt(unary.Operand)),
            '&' => Evaluate(unary.Operand) is CReferenceValue reference
                ? reference
                : throw Error(unary.Pos, "The '&' operator requires a name or an indexed name."),
            _ => throw Error(unary.Pos, $"Unsupported unary operator '{unary.Operator}'."),
        };
    }

    private CIntValue EvaluateBinary(CBinary binary)
    {
        long left = EvaluateInt(binary.Left);
        long right = EvaluateInt(binary.Right);
        return binary.Operator switch
        {
            '+' => new CIntValue(left + right),
            '-' => new CIntValue(left - right),
            _ => throw Error(binary.Pos, $"Unsupported binary operator '{binary.Operator}'."),
        };
    }

    private CValue EvaluateCast(CCast cast)
    {
        if (cast.TypeName.EndsWith('*'))
        {
            return Evaluate(cast.Operand) is CReferenceValue reference
                ? reference
                : throw Error(cast.Pos, "A pointer cast requires a reference to a variable or function.");
        }

        long value = EvaluateInt(cast.Operand);
        return cast.TypeName switch
        {
            "TSSymbol" or "TSStateId" or "TSFieldId" or "uint16_t" => new CIntValue(ConvertInteger(value, 16, false)),
            "uint8_t" => new CIntValue(ConvertInteger(value, 8, false)),
            "uint32_t" => new CIntValue(ConvertInteger(value, 32, false)),
            "int16_t" => new CIntValue(ConvertInteger(value, 16, true)),
            "int32_t" => new CIntValue(ConvertInteger(value, 32, true)),
            _ => throw Error(cast.Pos, $"Unsupported cast type '{cast.TypeName}'."),
        };
    }

    private CReferenceValue EvaluateIndex(CIndex index)
    {
        if (Evaluate(index.Target) is not CReferenceValue reference)
        {
            throw Error(index.Pos, "Only a name or a function can be indexed.");
        }

        long subscript = EvaluateInt(index.Index);
        var indices = new List<long>(reference.Indices) { subscript };
        return new CReferenceValue(reference.Name, indices);
    }

    private CDefine? FindDefine(string name)
    {
        foreach (CDefine define in Unit.Defines)
        {
            if (string.Equals(define.Name, name, StringComparison.Ordinal))
            {
                return define;
            }
        }

        return null;
    }

    private CExpr ParseDefineBody(CDefine define) =>
        new CParser(define.Body, Unit.FileName).ParseExpression();

    private bool IsFileLevelName(string name)
    {
        foreach (CVariable variable in Unit.Variables)
        {
            if (string.Equals(variable.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (CFunction function in Unit.Functions)
        {
            if (string.Equals(function.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (CPrototype prototype in Unit.Prototypes)
        {
            if (string.Equals(prototype.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private CSourceException Error(SourcePos pos, string text) => new(Unit.FileName, pos, text);

    /// <summary>Truncates to the width, then sign-extends for signed types (C conversion).</summary>
    private static long ConvertInteger(long value, int bits, bool signed)
    {
        long mask = (1L << bits) - 1;
        long truncated = value & mask;
        return signed && (truncated & (1L << (bits - 1))) != 0
            ? truncated - (1L << bits)
            : truncated;
    }
}
