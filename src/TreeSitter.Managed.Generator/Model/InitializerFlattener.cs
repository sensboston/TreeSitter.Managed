// Applies C99 initializer rules (6.7.8) to the parser.h types:
// - a designator [i] or .name sets the current position; items without a designator
//   continue after the previous item (next index / next member);
// - chained designators ([1].count, [1][2]) are not used in parser.c and are an error;
// - members and elements that are not initialized are zero (CIntValue(0) for integers and
//   bools, CNullValue for strings and pointers, recursively for aggregates);
// - a union without a designator initializes its first member; with a designator, the named
//   member; the result keeps only that member (CAggregateValue.ActiveMember);
// - an array with an unknown size ([]) gets the size max index + 1; with a size, an index
//   outside the size is an error;
// - brace elision is not used by parser.c except for the macro forms: an initializer macro
//   call (SHIFT, REDUCE, ...) in an element position of a TSParseActionEntry array is replaced
//   by its expansion (MacroExpander.ExpandInitializer);
// - a scalar initialized from a brace list with one value ({5}) takes that value;
// - integer values are converted to the scalar type like C (truncate, sign-extend for signed);
// - initializing the same element twice is an error (parser.c never does it).
// Errors are CSourceException with the position of the item.

using TreeSitter.Generator.CSource;

namespace TreeSitter.Generator.Model;

public sealed class InitializerFlattener
{
    public InitializerFlattener(ConstEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        Evaluator = evaluator;
    }

    public ConstEvaluator Evaluator { get; }

    /// <summary>
    /// Flattens a variable. Its dimensions are evaluated with the evaluator; the element type
    /// comes from ParserHTypes.Resolve(variable.ElementType). A variable without dimensions
    /// (the TSLanguage object) is an array with no dimensions and one element.
    /// </summary>
    public CArrayValue FlattenVariable(CVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        CTypeInfo elementType = ParserHTypes.Resolve(variable.ElementType);
        int dimensionCount = variable.Dimensions.Count;
        if (dimensionCount == 0)
        {
            return new CArrayValue(
                elementType,
                Array.Empty<int>(),
                new List<CValue> { FlattenValue(variable.Initializer, elementType) });
        }

        var dimensions = new int[dimensionCount];
        for (int i = 0; i < dimensionCount; i++)
        {
            dimensions[i] = variable.Dimensions[i] is CExpr size ? (int)Evaluator.EvaluateInt(size) : -1;
        }

        for (int i = 1; i < dimensionCount; i++)
        {
            if (dimensions[i] < 0)
            {
                throw Error(variable.Initializer.Pos, "Only the first dimension of an array may have an unknown size.");
            }
        }

        if (variable.Initializer is not CInitList list)
        {
            throw Error(variable.Initializer.Pos, "An array initializer must be a brace-enclosed list.");
        }

        var slots = new Dictionary<int, CValue>();
        int maxIndex = -1;
        FillLevel(list, 0, 0, dimensions, elementType, slots, ref maxIndex);

        int first = dimensions[0] >= 0 ? dimensions[0] : maxIndex + 1;
        dimensions[0] = first;
        int total = first;
        for (int i = 1; i < dimensionCount; i++)
        {
            total *= dimensions[i];
        }

        var elements = new CValue[total];
        for (int i = 0; i < total; i++)
        {
            elements[i] = slots.TryGetValue(i, out CValue? value) && value is not null
                ? value
                : ZeroValue(elementType);
        }

        return new CArrayValue(elementType, dimensions, elements);
    }

    /// <summary>Flattens one initializer of the given type (used by FlattenVariable and tests).</summary>
    public CValue FlattenValue(CInitializer initializer, CTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(type);

        // An initializer macro call in an element position of a TSParseActionEntry array is
        // replaced by its expansion (the macro forms of parser.h).
        if (initializer is CInitExpr { Expr: CCall call } &&
            ReferenceEquals(type, ParserHTypes.TSParseActionEntry) &&
            MacroExpander.InitializerMacros.ContainsKey(call.Name))
        {
            return FlattenValue(MacroExpander.ExpandInitializer(call, Evaluator.Unit.FileName), type);
        }

        return type switch
        {
            CScalarType scalar => FlattenScalar(initializer, scalar),
            CStructType structure => FlattenStruct(initializer, structure),
            CUnionType union => FlattenUnion(initializer, union),
            _ => throw Error(initializer.Pos, $"Unsupported C type '{type.Name}'."),
        };
    }

    private void FillLevel(
        CInitList list,
        int level,
        int baseOffset,
        int[] dimensions,
        CTypeInfo elementType,
        Dictionary<int, CValue> slots,
        ref int maxIndex)
    {
        int stride = 1;
        for (int i = level + 1; i < dimensions.Length; i++)
        {
            stride *= dimensions[i];
        }

        bool leaf = level == dimensions.Length - 1;
        int current = 0;
        foreach (CInitItem item in list.Items)
        {
            int index;
            if (item.Designators.Count == 0)
            {
                index = current;
            }
            else
            {
                if (item.Designators.Count > 1)
                {
                    throw Error(item.Pos, "Chained designators are not supported.");
                }

                if (item.Designators[0] is not CIndexDesignator designator)
                {
                    throw Error(item.Pos, "An array element needs an index designator.");
                }

                index = (int)Evaluator.EvaluateInt(designator.Index);
            }

            if (index < 0 || (dimensions[level] >= 0 && index >= dimensions[level]))
            {
                throw Error(item.Pos, $"Index {index} is outside the array dimension.");
            }

            current = index + 1;
            int flat = baseOffset + (index * stride);
            if (leaf)
            {
                if (slots.ContainsKey(flat))
                {
                    throw Error(item.Pos, "The same array element is initialized twice.");
                }

                slots[flat] = FlattenValue(item.Value, elementType);
            }
            else
            {
                if (item.Value is not CInitList inner)
                {
                    throw Error(item.Pos, "Nested array dimensions need a brace-enclosed list.");
                }

                FillLevel(inner, level + 1, flat, dimensions, elementType, slots, ref maxIndex);
            }

            if (level == 0 && index > maxIndex)
            {
                maxIndex = index;
            }
        }
    }

    private CValue FlattenScalar(CInitializer initializer, CScalarType scalar) => initializer switch
    {
        CInitExpr expression => ConvertScalar(Evaluator.Evaluate(expression.Expr), scalar, expression.Pos),
        CInitList { Items.Count: 1 } list when list.Items[0].Designators.Count == 0 =>
            FlattenValue(list.Items[0].Value, scalar),
        _ => throw Error(initializer.Pos, "A scalar initializer must contain exactly one untagged value."),
    };

    private CAggregateValue FlattenStruct(CInitializer initializer, CStructType structType)
    {
        if (initializer is not CInitList list)
        {
            throw Error(initializer.Pos, "A struct initializer must be a brace-enclosed list.");
        }

        var members = new Dictionary<string, CValue>(StringComparer.Ordinal);
        int current = 0;
        foreach (CInitItem item in list.Items)
        {
            int memberIndex;
            if (item.Designators.Count == 0)
            {
                memberIndex = current;
            }
            else if (item.Designators.Count > 1)
            {
                throw Error(item.Pos, "Chained designators are not supported.");
            }
            else if (item.Designators[0] is CFieldDesignator field)
            {
                memberIndex = FindMember(structType.Members, field.Name);
                if (memberIndex < 0)
                {
                    throw Error(item.Pos, $"'{structType.Name}' has no member '{field.Name}'.");
                }
            }
            else
            {
                throw Error(item.Pos, "A struct member needs a member designator.");
            }

            if (memberIndex >= structType.Members.Count)
            {
                throw Error(item.Pos, "Too many values in a struct initializer.");
            }

            current = memberIndex + 1;
            CMember member = structType.Members[memberIndex];
            CValue value = FlattenValue(item.Value, member.Type);
            if (!members.TryAdd(member.Name, value))
            {
                throw Error(item.Pos, $"Member '{member.Name}' is initialized twice.");
            }
        }

        foreach (CMember member in structType.Members)
        {
            if (!members.ContainsKey(member.Name))
            {
                members[member.Name] = ZeroValue(member.Type);
            }
        }

        return new CAggregateValue(structType, members, null);
    }

    private CAggregateValue FlattenUnion(CInitializer initializer, CUnionType unionType)
    {
        if (initializer is not CInitList list || list.Items.Count != 1)
        {
            throw Error(initializer.Pos, "A union initializer must have exactly one item.");
        }

        CInitItem item = list.Items[0];
        int memberIndex;
        if (item.Designators.Count == 0)
        {
            memberIndex = 0;
        }
        else if (item.Designators.Count > 1)
        {
            throw Error(item.Pos, "Chained designators are not supported.");
        }
        else if (item.Designators[0] is CFieldDesignator field)
        {
            memberIndex = FindMember(unionType.Members, field.Name);
            if (memberIndex < 0)
            {
                throw Error(item.Pos, $"'{unionType.Name}' has no member '{field.Name}'.");
            }
        }
        else
        {
            throw Error(item.Pos, "A union member needs a member designator.");
        }

        CMember member = unionType.Members[memberIndex];
        CValue value = FlattenValue(item.Value, member.Type);
        var members = new Dictionary<string, CValue>(StringComparer.Ordinal) { [member.Name] = value };
        return new CAggregateValue(unionType, members, member.Name);
    }

    private CValue ConvertScalar(CValue value, CScalarType scalar, SourcePos pos)
    {
        switch (scalar.Kind)
        {
            case CScalarKind.Bool:
            case CScalarKind.UInt8:
            case CScalarKind.UInt16:
            case CScalarKind.UInt32:
            case CScalarKind.Int16:
            case CScalarKind.Int32:
                return value is CIntValue integer
                    ? new CIntValue(ConvertInteger(integer.Value, scalar.Kind))
                    : throw Error(pos, $"A value of type '{scalar.Name}' must be an integer.");
            case CScalarKind.CString:
                return value is CStringValue or CNullValue
                    ? value
                    : throw Error(pos, "A string member must be a string literal or NULL.");
            case CScalarKind.Pointer:
            case CScalarKind.FunctionPointer:
                return value is CReferenceValue or CNullValue
                    ? value
                    : throw Error(pos, "A pointer member must be a variable, a function or NULL.");
            default:
                throw Error(pos, $"Unsupported scalar kind '{scalar.Kind}'.");
        }
    }

    private static int FindMember(IReadOnlyList<CMember> members, string name)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (string.Equals(members[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Zero value of a type: 0 for integers and bools, NULL for strings and pointers, recursive for aggregates.</summary>
    internal static CValue ZeroValue(CTypeInfo type)
    {
        switch (type)
        {
            case CScalarType scalar:
                return scalar.Kind is CScalarKind.CString or CScalarKind.Pointer or CScalarKind.FunctionPointer
                    ? CNullValue.Instance
                    : new CIntValue(0);
            case CStructType structure:
            {
                var members = new Dictionary<string, CValue>(StringComparer.Ordinal);
                foreach (CMember member in structure.Members)
                {
                    members[member.Name] = ZeroValue(member.Type);
                }

                return new CAggregateValue(structure, members, null);
            }
            case CUnionType union:
            {
                CMember first = union.Members[0];
                var members = new Dictionary<string, CValue>(StringComparer.Ordinal)
                {
                    [first.Name] = ZeroValue(first.Type),
                };
                return new CAggregateValue(union, members, first.Name);
            }
            default:
                throw new InvalidOperationException($"Unsupported C type '{type.Name}'.");
        }
    }

    /// <summary>Converts an integer value to the scalar type like C (truncate, sign-extend for signed).</summary>
    private static long ConvertInteger(long value, CScalarKind kind) => kind switch
    {
        CScalarKind.UInt8 => value & 0xFF,
        CScalarKind.UInt16 => value & 0xFFFF,
        CScalarKind.UInt32 => value & 0xFFFFFFFF,
        CScalarKind.Int16 => (short)(value & 0xFFFF),
        CScalarKind.Int32 => (int)(value & 0xFFFFFFFF),
        _ => value != 0 ? 1 : 0,
    };

    private CSourceException Error(SourcePos pos, string text) => new(Evaluator.Unit.FileName, pos, text);
}
