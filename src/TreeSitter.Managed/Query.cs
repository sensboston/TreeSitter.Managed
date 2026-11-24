// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/query.c, the query part (up to
// ts_query_disable_pattern; TSQueryCursor is not in this file).
// Rules:
// - Same structs, fields, functions and logic as query.c, in the same order. Internal
//   structs keep the C names in PascalCase (QueryStep, PatternEntry, QueryPattern,
//   StepOffset, SymbolTable, CaptureQuantifiers, QueryAnalysis, AnalysisState, ...);
//   arrays are TsArray, as in the rest of the port.
// - The source is UTF-8 bytes; every offset (error offset, start/end byte of a pattern,
//   the argument of IsPatternGuaranteedAtStep) is a byte offset, as in C.
// - Strings (capture names, predicate strings) are stored as UTF-8 bytes like the C
//   string_buffer; the public API returns them decoded.
// - ts_query_new returns NULL and an error: TryCreate does the same; the constructor throws
//   QueryException with the same error and offset.
// - The CaptureListPool and the finished-state heap of query.c are used only by the query
//   cursor and are ported with the cursor. The ts_assert macros are compiled out of the C
//   runtime, so they are Debug.Assert here (compiled out in Release like in the C build).
// - Stream keeps byte offsets instead of the C pointers (input - start and end - start).
// - iswspace/iswalnum of the C locale: the six ASCII space characters, ASCII letters and
//   digits; the TS_DECODE_ERROR code point -1 matches neither (same as the oracle runtime).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using TreeSitter.Abi;

namespace TreeSitter;

// CaptureQuantifiers: typedef Array(uint8_t) CaptureQuantifiers.
using CaptureQuantifiers = TreeSitter.TsArray<byte>;

// The constants of query.c, used unqualified like in the C file.
using static TreeSitter.QueryConstants;

// The #define and static const section of query.c.
internal static class QueryConstants
{
    // MAX_STEP_CAPTURE_COUNT
    internal const int MaxStepCaptureCount = 3;

    // MAX_NEGATED_FIELD_COUNT
    internal const int MaxNegatedFieldCount = 8;

    // MAX_STATE_PREDECESSOR_COUNT
    internal const int MaxStatePredecessorCount = 256;

    // MAX_ANALYSIS_STATE_DEPTH
    internal const int MaxAnalysisStateDepth = 8;

    // MAX_ANALYSIS_ITERATION_COUNT
    internal const int MaxAnalysisIterationCount = 256;

    // PARENT_DONE (static const TSQueryError PARENT_DONE = -1)
    internal const QueryError ParentDone = (QueryError)(-1);

    // PATTERN_DONE_MARKER
    internal const ushort PatternDoneMarker = ushort.MaxValue;

    // NONE
    internal const ushort None = ushort.MaxValue;

    // WILDCARD_SYMBOL
    internal const ushort WildcardSymbol = 0;

    // ts_builtin_sym_error (language.h)
    internal const ushort BuiltinSymbolError = 0xFFFF;

    // TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION (api.h)
    internal const uint MinCompatibleLanguageVersion = 13;

    // LANGUAGE_VERSION_WITH_RESERVED_WORDS (language.h)
    internal const uint LanguageVersionWithReservedWords = 15;
}

/// <summary>TSQueryError</summary>
public enum QueryError
{
    /// <summary>TSQueryErrorNone</summary>
    None = 0,

    /// <summary>TSQueryErrorSyntax</summary>
    Syntax,

    /// <summary>TSQueryErrorNodeType</summary>
    NodeType,

    /// <summary>TSQueryErrorField</summary>
    Field,

    /// <summary>TSQueryErrorCapture</summary>
    Capture,

    /// <summary>TSQueryErrorStructure</summary>
    Structure,

    /// <summary>TSQueryErrorLanguage</summary>
    Language,
}

/// <summary>TSQuantifier</summary>
public enum Quantifier
{
    /// <summary>TSQuantifierZero</summary>
    Zero = 0,

    /// <summary>TSQuantifierZeroOrOne</summary>
    ZeroOrOne,

    /// <summary>TSQuantifierZeroOrMore</summary>
    ZeroOrMore,

    /// <summary>TSQuantifierOne</summary>
    One,

    /// <summary>TSQuantifierOneOrMore</summary>
    OneOrMore,
}

/// <summary>TSQueryPredicateStepType</summary>
public enum QueryPredicateStepType
{
    /// <summary>TSQueryPredicateStepTypeDone</summary>
    Done,

    /// <summary>TSQueryPredicateStepTypeCapture</summary>
    Capture,

    /// <summary>TSQueryPredicateStepTypeString</summary>
    StringValue,
}

/// <summary>TSQueryPredicateStep</summary>
public readonly record struct QueryPredicateStep(QueryPredicateStepType Type, uint ValueId);

/// <summary>The error of a query that could not be compiled (ts_query_new returned NULL).</summary>
public sealed class QueryException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QueryException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public QueryException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public QueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for an error of ts_query_new.</summary>
    public QueryException(QueryError error, uint offset)
        : base($"Query error {error} at byte {offset}.")
    {
        Error = error;
        Offset = offset;
    }

    /// <summary>
    /// Creates the exception for a predicate error of the query layer (predicate_error of the
    /// Rust binding: the row of the pattern, column 0, offset 0 and the message).
    /// </summary>
    /// <param name="error">The error type.</param>
    /// <param name="offset">The byte offset of the error, 0 for a predicate error.</param>
    /// <param name="row">The row of the pattern whose predicate is invalid.</param>
    /// <param name="message">The message of the error.</param>
    public QueryException(QueryError error, uint offset, uint row, string message)
        : base(message)
    {
        Error = error;
        Offset = offset;
        Row = row;
    }

    /// <summary>The error type.</summary>
    public QueryError Error { get; }

    /// <summary>The byte offset of the error in the source.</summary>
    public uint Offset { get; }

    /// <summary>
    /// The row of the pattern of a predicate error (Rust: QueryError.row); 0 for an error that
    /// the C runtime reports, which gives only a byte offset.
    /// </summary>
    public uint Row { get; }
}

/// <summary>
/// Stream: a sequence of unicode characters derived from a UTF8 string. This struct is
/// used in parsing queries from S-expressions. The C pointers input/start/end are the
/// byte offset Position (input - start) and End (end - start) into Source.
/// </summary>
internal struct Stream
{
    /// <summary>The whole input; the C string the start and end pointers point into.</summary>
    internal byte[] Source;

    /// <summary>The current position (C: input - start).</summary>
    internal uint Position;

    /// <summary>The end of the input (C: end - start).</summary>
    internal uint End;

    /// <summary>The next code point (C: next).</summary>
    internal int Next;

    /// <summary>The size of the next code point in bytes (C: next_size).</summary>
    internal uint NextSize;
}

/// <summary>
/// Slice: a slice of an external array. Within a query, capture names, literal string
/// values, and predicate step information are stored in three contiguous arrays.
/// </summary>
internal struct Slice
{
    internal uint Offset;

    internal uint Length;

    internal Slice(uint offset, uint length)
    {
        Offset = offset;
        Length = length;
    }
}

/// <summary>
/// QueryStep: a step in the process of matching a query. Each node within a query
/// S-expression corresponds to one of these steps. The C bit fields are plain fields
/// with the same defaults (every flag false, every id NONE or 0).
/// </summary>
internal struct QueryStep
{
    /// <summary>The capture_ids array of MAX_STEP_CAPTURE_COUNT entries.</summary>
    [InlineArray(MaxStepCaptureCount)]
    internal struct QueryStepCaptureIds
    {
        private ushort _element0;
    }

    internal ushort Symbol;

    internal ushort SupertypeSymbol;

    internal ushort Field;

    internal QueryStepCaptureIds CaptureIds;

    internal ushort Depth;

    internal ushort AlternativeIndex;

    internal ushort NegatedFieldListId;

    internal bool IsNamed;

    internal bool IsImmediate;

    internal bool IsLastChild;

    internal bool IsPassThrough;

    internal bool IsDeadEnd;

    internal bool IsInsideAlternation;

    internal bool ContainsCaptures;

    internal bool RootPatternGuaranteed;

    internal bool ParentPatternGuaranteed;

    internal bool IsMissing;

    internal bool AlternativeIsSkip;
}

/// <summary>SymbolTable: a two-way mapping of strings to ids. default(SymbolTable) is
/// symbol_table_new().</summary>
internal struct SymbolTable
{
    internal TsArray<byte> Characters;

    internal TsArray<Slice> Slices;
}

/// <summary>
/// PatternEntry: information about the starting point for matching a particular pattern.
/// </summary>
internal struct PatternEntry
{
    internal ushort StepIndex;

    internal ushort PatternIndex;

    internal bool IsRooted;
}

/// <summary>QueryPattern.</summary>
internal struct QueryPattern
{
    internal Slice Steps;

    internal Slice PredicateSteps;

    internal uint StartByte;

    internal uint EndByte;

    internal bool IsNonLocal;
}

/// <summary>StepOffset.</summary>
internal struct StepOffset
{
    internal ushort StepIndex;

    internal uint ByteOffset;

    internal StepOffset(ushort stepIndex, uint byteOffset)
    {
        StepIndex = stepIndex;
        ByteOffset = byteOffset;
    }
}

/// <summary>
/// AnalysisStateEntry: one entry of the state needed for walking the parse table when
/// analyzing a query pattern. The C bit fields field_id: 15 and done: 1 are a property
/// (masked to 15 bits, like the bit field) and a bool.
/// </summary>
internal struct AnalysisStateEntry
{
    private ushort _fieldId;

    internal ushort ParseState;

    internal ushort ParentSymbol;

    internal ushort ChildIndex;

    internal bool Done;

    internal ushort FieldId
    {
        readonly get => _fieldId;
        set => _fieldId = (ushort)(value & 0x7FFF);
    }
}

/// <summary>AnalysisState. A class, like the pointers to it in the C analysis state sets.</summary>
internal sealed class AnalysisState
{
    internal readonly AnalysisStateEntry[] Stack = new AnalysisStateEntry[MaxAnalysisStateDepth];

    internal ushort Depth;

    internal ushort StepIndex;

    internal ushort RootSymbol;

    // The struct assignment of the C code: *self = *other.
    internal void AssignFrom(AnalysisState other)
    {
        Array.Copy(other.Stack, Stack, MaxAnalysisStateDepth);
        Depth = other.Depth;
        StepIndex = other.StepIndex;
        RootSymbol = other.RootSymbol;
    }
}

/// <summary>
/// AnalysisSubgraphNode: one state of a subgraph, with information about the node that
/// could be produced in downstream states. The C bit field child_index: 7 is a property
/// masked to 7 bits.
/// </summary>
internal struct AnalysisSubgraphNode
{
    private ushort _childIndex;

    internal ushort State;

    internal ushort ProductionId;

    internal bool Done;

    internal ushort ChildIndex
    {
        readonly get => _childIndex;
        set => _childIndex = (ushort)(value & 0x7F);
    }
}

/// <summary>AnalysisSubgraph: the subgraph of one symbol of the parse table.</summary>
internal struct AnalysisSubgraph
{
    internal ushort Symbol;

    internal TsArray<ushort> StartStates;

    internal TsArray<AnalysisSubgraphNode> Nodes;
}

/// <summary>
/// StatePredecessorMap: the predecessors of each parse state, used during query analysis
/// to determine which parse states can lead to which reduce actions.
/// </summary>
internal sealed class StatePredecessorMap
{
    private readonly ushort[] _contents;

    // state_predecessor_map_new
    internal StatePredecessorMap(Language language)
    {
        _contents = new ushort[checked((int)(language.StateCount * (MaxStatePredecessorCount + 1)))];
    }

    // state_predecessor_map_add
    internal void Add(ushort state, ushort predecessor)
    {
        uint index = (uint)state * (MaxStatePredecessorCount + 1);
        // The C code increments the stored count through a pointer: `TSStateId *count =
        // &self->contents[index]; (*count)++;`. Keep the same through a ref.
        ref ushort count = ref _contents[index];
        if (count == 0 ||
            (count < MaxStatePredecessorCount && _contents[index + count] != predecessor))
        {
            count++;
            _contents[index + count] = predecessor;
        }
    }

    // state_predecessor_map_get
    internal ReadOnlySpan<ushort> Get(ushort state, out uint count)
    {
        uint index = (uint)state * (MaxStatePredecessorCount + 1);
        count = _contents[index];
        return _contents.AsSpan((int)index + 1, (int)count);
    }
}

/// <summary>QueryAnalysis: the state sets of the query analysis.</summary>
internal sealed class QueryAnalysis
{
    internal TsArray<AnalysisState> States;

    internal TsArray<AnalysisState> NextStates;

    internal TsArray<AnalysisState> DeeperStates;

    internal TsArray<AnalysisState> StatePool;

    internal TsArray<ushort> FinalStepIndices;

    internal TsArray<ushort> FinishedParentSymbols;

    internal bool DidAbort;
}

/// <summary>A compiled query (TSQuery). Immutable except for DisableCapture/DisablePattern.</summary>
public sealed partial class Query
{
    private SymbolTable _captures;

    private SymbolTable _predicateValues;

    private TsArray<CaptureQuantifiers> _captureQuantifiers;

    private TsArray<QueryStep> _steps;

    private TsArray<PatternEntry> _patternMap;

    private TsArray<QueryPredicateStep> _predicateSteps;

    private TsArray<QueryPattern> _patterns;

    private TsArray<StepOffset> _stepOffsets;

    private TsArray<ushort> _negatedFields;

    private TsArray<byte> _stringBuffer;

    private TsArray<ushort> _repeatSymbolsWithRootlessPatterns;

    // C: const TSLanguage *language. Set by TryCompileInto before any use; the private
    // constructor leaves it null, like the uninitialized struct in C.
    private Language _language = null!;

    private ushort _wildcardRootPatternCount;

    private Query()
    {
    }

    /// <summary>TSQuery.pattern_map, for the query cursor.</summary>
    internal ReadOnlySpan<PatternEntry> PatternMap => _patternMap.AsSpan();

    /// <summary>TSQuery.wildcard_root_pattern_count, for the query cursor.</summary>
    internal ushort WildcardRootPatternCount => _wildcardRootPatternCount;

    /// <summary>TSQuery.negated_fields, for the query cursor.</summary>
    internal ReadOnlySpan<ushort> NegatedFields => _negatedFields.AsSpan();

    /// <summary>TSQuery.repeat_symbols_with_rootless_patterns, for the query cursor.</summary>
    internal TsArray<ushort> RepeatSymbolsWithRootlessPatterns => _repeatSymbolsWithRootlessPatterns;

    /// <summary>TSQuery.steps, for the query cursor.</summary>
    internal ref QueryStep StepRef(uint index) => ref _steps.Get(index);

    /// <summary>
    /// ts_query_new, then the predicate analysis of Query::new of the Rust binding: throws
    /// QueryException when the source has an error or when one of its predicates is invalid.
    /// </summary>
    public Query(Language language, ReadOnlySpan<byte> source)
    {
        if (!TryCompileInto(language, source.ToArray(), out uint errorOffset, out QueryError error))
        {
            throw new QueryException(error, errorOffset);
        }

        ThrowForInvalidPredicate();
    }

    /// <summary>ts_query_new over the UTF-8 bytes of <paramref name="source"/>.</summary>
    public Query(Language language, string source)
        : this(language, System.Text.Encoding.UTF8.GetBytes(source ?? throw new ArgumentNullException(nameof(source))))
    {
    }

    /// <summary>
    /// ts_query_new: null and the error type and byte offset when the source has an error,
    /// like the C function. As Query::new_raw of the Rust binding, this does no predicate
    /// analysis of its own: a query whose predicate is invalid is still returned, and the
    /// error is reported when its predicates are read.
    /// </summary>
    public static Query? TryCreate(Language language, ReadOnlySpan<byte> source, out uint errorOffset, out QueryError error)
    {
        Query query = new Query();
        if (query.TryCompileInto(language, source.ToArray(), out errorOffset, out error))
        {
            return query;
        }

        return null;
    }

    // ts_query_new
    private bool TryCompileInto(Language language, byte[] source, out uint errorOffset, out QueryError error)
    {
        errorOffset = 0;
        if (language is null ||
            language.AbiVersion > Language.SupportedAbiVersion ||
            language.AbiVersion < MinCompatibleLanguageVersion)
        {
            error = QueryError.Language;
            return false;
        }

        error = QueryError.None;
        _language = language;
        _negatedFields.Push(0);

        // Parse all of the S-expressions in the given string.
        Stream stream = StreamNew(source, (uint)source.Length);
        StreamSkipWhitespace(ref stream);
        while (stream.Position < stream.End)
        {
            uint patternIndex = _patterns.Size;
            uint startStepIndex = _steps.Size;
            uint startPredicateStepIndex = _predicateSteps.Size;
            _patterns.Push(new QueryPattern
            {
                Steps = new Slice(startStepIndex, 0),
                PredicateSteps = new Slice(startPredicateStepIndex, 0),
                StartByte = StreamOffset(ref stream),
                IsNonLocal = false,
            });
            CaptureQuantifiers captureQuantifiers = CaptureQuantifiersNew();
            error = ParsePattern(ref stream, 0, isImmediate: false, isInsideAlternation: false, ref captureQuantifiers);
            _steps.Push(QueryStepNew(0, PatternDoneMarker, isImmediate: false));

            ref QueryPattern pattern = ref _patterns.Back();
            pattern.Steps.Length = _steps.Size - startStepIndex;
            pattern.PredicateSteps.Length = _predicateSteps.Size - startPredicateStepIndex;
            pattern.EndByte = StreamOffset(ref stream);

            // If any pattern could not be parsed, then report the error information
            // and terminate.
            if (error != QueryError.None)
            {
                if (error == ParentDone)
                {
                    error = QueryError.Syntax;
                }

                errorOffset = StreamOffset(ref stream);
                return false;
            }

            // Maintain a list of capture quantifiers for each pattern
            _captureQuantifiers.Push(captureQuantifiers);

            // Maintain a map that can look up patterns for a given root symbol.
            ushort wildcardRootAlternativeIndex = None;
            for (; ; )
            {
                QueryStep step = _steps.Get(startStepIndex);

                // If a pattern has a wildcard at its root, but it has a non-wildcard child,
                // then optimize the matching process by skipping matching the wildcard.
                // Later, during the matching process, the query cursor will check that
                // there is a parent node, and capture it if necessary.
                if (step.Symbol == WildcardSymbol && step.Depth == 0 && step.Field == 0)
                {
                    QueryStep secondStep = _steps.Get(startStepIndex + 1);
                    if (secondStep.Symbol != WildcardSymbol && secondStep.Depth == 1 && !secondStep.IsImmediate)
                    {
                        wildcardRootAlternativeIndex = step.AlternativeIndex;
                        startStepIndex += 1;
                        step = secondStep;
                    }
                }

                // Determine whether the pattern has a single root node. This affects
                // decisions about whether or not to start matching the pattern when
                // a query cursor has a range restriction or when immediately within an
                // error node.
                uint startDepth = step.Depth;
                bool isRooted = startDepth == 0;
                for (uint stepIndex = startStepIndex + 1; stepIndex < _steps.Size; stepIndex++)
                {
                    QueryStep childStep = _steps.Get(stepIndex);
                    if (childStep.IsDeadEnd)
                    {
                        break;
                    }

                    if (childStep.Depth == startDepth)
                    {
                        isRooted = false;
                        break;
                    }
                }

                PatternMapInsert(step.Symbol, new PatternEntry
                {
                    StepIndex = (ushort)startStepIndex,
                    PatternIndex = (ushort)patternIndex,
                    IsRooted = isRooted,
                });
                if (step.Symbol == WildcardSymbol)
                {
                    _wildcardRootPatternCount++;
                }

                // If there are alternatives or options at the root of the pattern,
                // then add multiple entries to the pattern map.
                if (step.AlternativeIndex != None)
                {
                    startStepIndex = step.AlternativeIndex;
                }
                else if (wildcardRootAlternativeIndex != None)
                {
                    startStepIndex = wildcardRootAlternativeIndex;
                    wildcardRootAlternativeIndex = None;
                }
                else
                {
                    break;
                }
            }

            // Fix up quantifier loop-backs within alternations. When a branch of an
            // alternation has a + or * quantifier, the quantifier's pass_through step
            // loops back to the branch's first step. However, the alternation linking
            // assigns that same step's `alternative_index` to point to the _next_ branch.
            // This causes the quantifier loop to incorrectly explore other alternation branches,
            // when a quantified branch matches, loops back, and then fails to match. To correct
            // this, we create "clean" copies of the branches' first steps without the link to the
            // next branch. After a quantified branch matches, it loops back to the cleaned copy.
            {
                uint patStart = pattern.Steps.Offset;
                uint patEnd = patStart + pattern.Steps.Length - 1; // exclude DONE

                for (uint i = patStart; i < patEnd; i++)
                {
                    QueryStep s = _steps.Get(i);
                    // Ensure this step is a pass_through with a _backward_ alternative (a quantifier loop-back)
                    if (!s.IsPassThrough || !s.IsInsideAlternation
                        || s.AlternativeIndex == None || s.AlternativeIndex >= i)
                    {
                        continue;
                    }

                    uint targetIdx = s.AlternativeIndex;
                    QueryStep target = _steps.Get(targetIdx);

                    // Check if the target has a forward alternative from alternation linking
                    ushort targetAltIndex = target.AlternativeIndex;
                    if (targetAltIndex == None
                        || targetAltIndex <= targetIdx || targetAltIndex >= patEnd)
                    {
                        continue;
                    }

                    // Create a clean copy of the target step without the alternation alternative.
                    uint copyIdx = _steps.Size;
                    QueryStep copy = target;
                    copy.AlternativeIndex = None;
                    ushort targetDepth = target.Depth;
                    _steps.Push(copy);

                    // Add a dead_end that redirects to the pass through step after the target,
                    // so the pattern continues correctly after the cleaned copy matches.
                    QueryStep redirect = QueryStepNew(0, targetDepth, isImmediate: false);
                    redirect.IsDeadEnd = true;
                    redirect.AlternativeIndex = (ushort)(targetIdx + 1);
                    _steps.Push(redirect);

                    // Update the pass_through to loop back to the copy. Reacquire `s` since
                    // `self->steps` may have been reallocated.
                    _steps.Get(i).AlternativeIndex = (ushort)copyIdx;
                }
            }
        }

        if (!AnalyzePatterns(out errorOffset))
        {
            error = QueryError.Structure;
            return false;
        }

        _stringBuffer.Delete();

        // Query::from_raw_parts of the Rust binding, which the binding runs right after
        // ts_query_new succeeded. An invalid predicate is not a TSQueryError, so the C part
        // of the compile stayed successful: the error is kept for ThrowForInvalidPredicate.
        _predicateSource = source;
        BuildPredicates();
        return true;
    }

    /// <summary>ts_query_copy</summary>
    public Query Copy()
    {
        Query copy = new Query
        {
            _language = _language,
            _wildcardRootPatternCount = _wildcardRootPatternCount,
        };

        copy._steps.Assign(_steps.AsSpan());
        copy._patternMap.Assign(_patternMap.AsSpan());
        copy._predicateSteps.Assign(_predicateSteps.AsSpan());
        copy._patterns.Assign(_patterns.AsSpan());
        copy._stepOffsets.Assign(_stepOffsets.AsSpan());
        copy._negatedFields.Assign(_negatedFields.AsSpan());
        copy._stringBuffer.Assign(_stringBuffer.AsSpan());
        copy._repeatSymbolsWithRootlessPatterns.Assign(_repeatSymbolsWithRootlessPatterns.AsSpan());
        copy._captures.Characters.Assign(_captures.Characters.AsSpan());
        copy._captures.Slices.Assign(_captures.Slices.AsSpan());
        copy._predicateValues.Characters.Assign(_predicateValues.Characters.AsSpan());
        copy._predicateValues.Slices.Assign(_predicateValues.Slices.AsSpan());

        copy._captureQuantifiers.Assign(_captureQuantifiers.AsSpan());
        for (uint i = 0; i < copy._captureQuantifiers.Size; i++)
        {
            copy._captureQuantifiers.Get(i) = CaptureQuantifiersNew();
            copy._captureQuantifiers.Get(i).Assign(_captureQuantifiers.Get(i).AsSpan());
        }

        // TSQuery has no predicate lists: they belong to the binding layer (see
        // QueryPredicates.cs), so the copy builds its own from the copied steps and the
        // source it shares with this query.
        copy._predicateSource = _predicateSource;
        copy.BuildPredicates();

        return copy;
    }

    /// <summary>The language of the query (TSQuery.language).</summary>
    public Language Language => _language;

    /// <summary>ts_query_pattern_count</summary>
    public uint PatternCount => _patterns.Size;

    /// <summary>ts_query_capture_count</summary>
    public uint CaptureCount => _captures.Slices.Size;

    /// <summary>ts_query_string_count</summary>
    public uint StringCount => _predicateValues.Slices.Size;

    /// <summary>ts_query_start_byte_for_pattern</summary>
    public uint StartByteForPattern(uint patternIndex) => _patterns.Get(patternIndex).StartByte;

    /// <summary>ts_query_end_byte_for_pattern</summary>
    public uint EndByteForPattern(uint patternIndex) => _patterns.Get(patternIndex).EndByte;

    /// <summary>ts_query_predicates_for_pattern</summary>
    public ReadOnlySpan<QueryPredicateStep> PredicatesForPattern(uint patternIndex)
    {
        Slice slice = _patterns.Get(patternIndex).PredicateSteps;
        if (slice.Length == 0)
        {
            return ReadOnlySpan<QueryPredicateStep>.Empty;
        }

        return _predicateSteps.AsSpan().Slice((int)slice.Offset, (int)slice.Length);
    }

    /// <summary>ts_query_is_pattern_rooted</summary>
    public bool IsPatternRooted(uint patternIndex)
    {
        for (uint i = 0; i < _patternMap.Size; i++)
        {
            ref PatternEntry entry = ref _patternMap.Get(i);
            if (entry.PatternIndex == patternIndex)
            {
                if (!entry.IsRooted)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>ts_query_is_pattern_non_local</summary>
    public bool IsPatternNonLocal(uint patternIndex)
    {
        if (patternIndex < _patterns.Size)
        {
            return _patterns.Get(patternIndex).IsNonLocal;
        }
        else
        {
            return false;
        }
    }

    /// <summary>ts_query_is_pattern_guaranteed_at_step</summary>
    public bool IsPatternGuaranteedAtStep(uint byteOffset)
    {
        uint stepIndex = uint.MaxValue;
        for (uint i = 0; i < _stepOffsets.Size; i++)
        {
            ref StepOffset stepOffset = ref _stepOffsets.Get(i);
            if (stepOffset.ByteOffset > byteOffset)
            {
                break;
            }

            stepIndex = stepOffset.StepIndex;
        }

        if (stepIndex < _steps.Size)
        {
            return _steps.Get(stepIndex).RootPatternGuaranteed;
        }
        else
        {
            return false;
        }
    }

    /// <summary>ts_query_capture_name_for_id</summary>
    public string CaptureNameForId(uint index)
    {
        ReadOnlySpan<byte> name = SymbolTableNameForId(ref _captures, (ushort)index);
        return Encoding.UTF8.GetString(name);
    }

    /// <summary>ts_query_capture_quantifier_for_id</summary>
    public Quantifier CaptureQuantifierForId(uint patternIndex, uint captureIndex)
    {
        ref CaptureQuantifiers captureQuantifiers = ref _captureQuantifiers.Get(patternIndex);
        return CaptureQuantifierForId(ref captureQuantifiers, (ushort)captureIndex);
    }

    /// <summary>ts_query_string_value_for_id</summary>
    public string StringValueForId(uint index)
    {
        ReadOnlySpan<byte> value = SymbolTableNameForId(ref _predicateValues, (ushort)index);
        return Encoding.UTF8.GetString(value);
    }

    /// <summary>ts_query_disable_capture</summary>
    public void DisableCapture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Remove capture information for any pattern step that previously
        // captured with the given name.
        int id = SymbolTableIdForName(ref _captures, Encoding.UTF8.GetBytes(name));
        if (id != -1)
        {
            for (uint i = 0; i < _steps.Size; i++)
            {
                QueryStepRemoveCapture(ref _steps.Get(i), (ushort)id);
            }
        }
    }

    /// <summary>ts_query_disable_pattern</summary>
    public void DisablePattern(uint patternIndex)
    {
        // Remove the given pattern from the pattern map. Its steps will still
        // be in the `steps` array, but they will never be read.
        for (uint i = 0; i < _patternMap.Size; i++)
        {
            if (_patternMap.Get(i).PatternIndex == patternIndex)
            {
                _patternMap.Erase(i);
                i--;
            }
        }
    }

    // ts_query__step_is_fallible
    internal bool StepIsFallible(ushort stepIndex)
    {
        uint i = 1;
        QueryStep step = _steps.Get(stepIndex);
        QueryStep nextStep;
        do
        {
            Debug.Assert((uint)(stepIndex + i) < _steps.Size);
            nextStep = _steps.Get((uint)(stepIndex + i));
            i++;
        }
        while (nextStep.IsPassThrough);
        return (
            nextStep.Depth != PatternDoneMarker &&
            (nextStep.Depth > step.Depth ||
                (nextStep.Depth == step.Depth && nextStep.IsImmediate)) &&
            (!nextStep.ParentPatternGuaranteed || step.Symbol == WildcardSymbol)
        );
    }

    // ---------------------------------------------------------------- Stream

    // stream_advance: advance to the next unicode code point in the stream.
    private static bool StreamAdvance(ref Stream self)
    {
        self.Position += self.NextSize;
        if (self.Position < self.End)
        {
            uint size = Unicode.DecodeUtf8(
                self.Source.AsSpan((int)self.Position, (int)(self.End - self.Position)),
                out int next);
            if (size > 0)
            {
                self.NextSize = size;
                self.Next = next;
                return true;
            }
        }
        else
        {
            self.NextSize = 0;
            self.Next = '\0';
        }

        return false;
    }

    // stream_reset: reset the stream to the given input position, represented as a byte
    // offset into the input string.
    private static void StreamReset(ref Stream self, uint input)
    {
        self.Position = input;
        self.NextSize = 0;
        StreamAdvance(ref self);
    }

    // stream_new
    private static Stream StreamNew(byte[] source, uint length)
    {
        Stream self = new Stream
        {
            Next = 0,
            Position = 0,
            End = length,
            Source = source,
        };
        StreamAdvance(ref self);
        return self;
    }

    // stream_skip_whitespace
    private static void StreamSkipWhitespace(ref Stream self)
    {
        for (; ; )
        {
            if (IsWSpace(self.Next))
            {
                StreamAdvance(ref self);
            }
            else if (self.Next == ';')
            {
                // skip over comments
                StreamAdvance(ref self);
                while (self.Next != 0 && self.Next != '\n')
                {
                    if (!StreamAdvance(ref self))
                    {
                        break;
                    }
                }
            }
            else
            {
                break;
            }
        }
    }

    // stream_is_ident_start
    private static bool StreamIsIdentStart(ref Stream self)
    {
        return IsWAlNum(self.Next) || self.Next == '_' || self.Next == '-';
    }

    // stream_scan_identifier
    private static void StreamScanIdentifier(ref Stream stream)
    {
        do
        {
            StreamAdvance(ref stream);
        }
        while (
            IsWAlNum(stream.Next) ||
            stream.Next == '_' ||
            stream.Next == '-' ||
            stream.Next == '.'
        );
    }

    // stream_offset
    private static uint StreamOffset(ref Stream self)
    {
        return self.Position;
    }

    // iswspace of the C locale.
    private static bool IsWSpace(int c)
    {
        return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';
    }

    // iswalnum of the C locale.
    private static bool IsWAlNum(int c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }

    // ---------------------------------------------------------------- Quantifiers

    // quantifier_mul
    private static Quantifier QuantifierMul(Quantifier left, Quantifier right)
    {
        switch (left)
        {
            case Quantifier.Zero:
                return Quantifier.Zero;
            case Quantifier.ZeroOrOne:
                switch (right)
                {
                    case Quantifier.Zero:
                        return Quantifier.Zero;
                    case Quantifier.ZeroOrOne:
                    case Quantifier.One:
                        return Quantifier.ZeroOrOne;
                    case Quantifier.ZeroOrMore:
                    case Quantifier.OneOrMore:
                        return Quantifier.ZeroOrMore;
                }

                break;
            case Quantifier.ZeroOrMore:
                switch (right)
                {
                    case Quantifier.Zero:
                        return Quantifier.Zero;
                    case Quantifier.ZeroOrOne:
                    case Quantifier.ZeroOrMore:
                    case Quantifier.One:
                    case Quantifier.OneOrMore:
                        return Quantifier.ZeroOrMore;
                }

                break;
            case Quantifier.One:
                return right;
            case Quantifier.OneOrMore:
                switch (right)
                {
                    case Quantifier.Zero:
                        return Quantifier.Zero;
                    case Quantifier.ZeroOrOne:
                    case Quantifier.ZeroOrMore:
                        return Quantifier.ZeroOrMore;
                    case Quantifier.One:
                    case Quantifier.OneOrMore:
                        return Quantifier.OneOrMore;
                }

                break;
        }

        return Quantifier.Zero; // to make compiler happy, but all cases should be covered above!
    }

    // quantifier_join
    private static Quantifier QuantifierJoin(Quantifier left, Quantifier right)
    {
        switch (left)
        {
            case Quantifier.Zero:
                switch (right)
                {
                    case Quantifier.Zero:
                        return Quantifier.Zero;
                    case Quantifier.ZeroOrOne:
                    case Quantifier.One:
                        return Quantifier.ZeroOrOne;
                    case Quantifier.ZeroOrMore:
                    case Quantifier.OneOrMore:
                        return Quantifier.ZeroOrMore;
                }

                break;
            case Quantifier.ZeroOrOne:
                switch (right)
                {
                    case Quantifier.Zero:
                    case Quantifier.ZeroOrOne:
                    case Quantifier.One:
                        return Quantifier.ZeroOrOne;
                    case Quantifier.ZeroOrMore:
                    case Quantifier.OneOrMore:
                        return Quantifier.ZeroOrMore;
                }

                break;
            case Quantifier.ZeroOrMore:
                return Quantifier.ZeroOrMore;
            case Quantifier.One:
                switch (right)
                {
                    case Quantifier.Zero:
                    case Quantifier.ZeroOrOne:
                        return Quantifier.ZeroOrOne;
                    case Quantifier.ZeroOrMore:
                        return Quantifier.ZeroOrMore;
                    case Quantifier.One:
                        return Quantifier.One;
                    case Quantifier.OneOrMore:
                        return Quantifier.OneOrMore;
                }

                break;
            case Quantifier.OneOrMore:
                switch (right)
                {
                    case Quantifier.Zero:
                    case Quantifier.ZeroOrOne:
                    case Quantifier.ZeroOrMore:
                        return Quantifier.ZeroOrMore;
                    case Quantifier.One:
                    case Quantifier.OneOrMore:
                        return Quantifier.OneOrMore;
                }

                break;
        }

        return Quantifier.Zero; // to make compiler happy, but all cases should be covered above!
    }

    // quantifier_add
    private static Quantifier QuantifierAdd(Quantifier left, Quantifier right)
    {
        switch (left)
        {
            case Quantifier.Zero:
                return right;
            case Quantifier.ZeroOrOne:
                switch (right)
                {
                    case Quantifier.Zero:
                        return Quantifier.ZeroOrOne;
                    case Quantifier.ZeroOrOne:
                    case Quantifier.ZeroOrMore:
                        return Quantifier.ZeroOrMore;
                    case Quantifier.One:
                    case Quantifier.OneOrMore:
                        return Quantifier.OneOrMore;
                }

                break;
            case Quantifier.ZeroOrMore:
                switch (right)
                {
                    case Quantifier.Zero:
                        return Quantifier.ZeroOrMore;
                    case Quantifier.ZeroOrOne:
                    case Quantifier.ZeroOrMore:
                        return Quantifier.ZeroOrMore;
                    case Quantifier.One:
                    case Quantifier.OneOrMore:
                        return Quantifier.OneOrMore;
                }

                break;
            case Quantifier.One:
                switch (right)
                {
                    case Quantifier.Zero:
                        return Quantifier.One;
                    case Quantifier.ZeroOrOne:
                    case Quantifier.ZeroOrMore:
                    case Quantifier.One:
                    case Quantifier.OneOrMore:
                        return Quantifier.OneOrMore;
                }

                break;
            case Quantifier.OneOrMore:
                return Quantifier.OneOrMore;
        }

        return Quantifier.Zero; // to make compiler happy, but all cases should be covered above!
    }

    // capture_quantifiers_new: create new capture quantifiers structure.
    private static CaptureQuantifiers CaptureQuantifiersNew() => default;

    // capture_quantifiers_clear
    private static void CaptureQuantifiersClear(ref CaptureQuantifiers self)
    {
        self.Clear();
    }

    // capture_quantifiers_replace: replace capture quantifiers with the given quantifiers.
    private static void CaptureQuantifiersReplace(ref CaptureQuantifiers self, ref CaptureQuantifiers quantifiers)
    {
        self.Clear();
        self.PushAll(quantifiers);
    }

    // capture_quantifier_for_id: return capture quantifier for the given capture id.
    private static Quantifier CaptureQuantifierForId(ref CaptureQuantifiers self, ushort id)
    {
        return (self.Size <= id) ? Quantifier.Zero : (Quantifier)self.Get(id);
    }

    // capture_quantifiers_add_for_id: add the given quantifier to the current value for id.
    private static void CaptureQuantifiersAddForId(ref CaptureQuantifiers self, ushort id, Quantifier quantifier)
    {
        if (self.Size <= id)
        {
            self.GrowBy((uint)(id + 1 - self.Size));
        }

        ref byte ownQuantifier = ref self.Get(id);
        ownQuantifier = (byte)QuantifierAdd((Quantifier)ownQuantifier, quantifier);
    }

    // capture_quantifiers_add_all: point-wise add the given quantifiers to the current values.
    private static void CaptureQuantifiersAddAll(ref CaptureQuantifiers self, ref CaptureQuantifiers quantifiers)
    {
        if (self.Size < quantifiers.Size)
        {
            self.GrowBy(quantifiers.Size - self.Size);
        }

        for (uint id = 0; id < quantifiers.Size; id++)
        {
            ref byte quantifier = ref quantifiers.Get(id);
            ref byte ownQuantifier = ref self.Get(id);
            ownQuantifier = (byte)QuantifierAdd((Quantifier)ownQuantifier, (Quantifier)quantifier);
        }
    }

    // capture_quantifiers_mul: join the given quantifier with the current values.
    private static void CaptureQuantifiersMul(ref CaptureQuantifiers self, Quantifier quantifier)
    {
        for (uint id = 0; id < self.Size; id++)
        {
            ref byte ownQuantifier = ref self.Get(id);
            ownQuantifier = (byte)QuantifierMul((Quantifier)ownQuantifier, quantifier);
        }
    }

    // capture_quantifiers_join_all: point-wise join the quantifiers from a list of
    // alternatives with the current values.
    private static void CaptureQuantifiersJoinAll(ref CaptureQuantifiers self, ref CaptureQuantifiers quantifiers)
    {
        if (self.Size < quantifiers.Size)
        {
            self.GrowBy(quantifiers.Size - self.Size);
        }

        for (uint id = 0; id < quantifiers.Size; id++)
        {
            ref byte quantifier = ref quantifiers.Get(id);
            ref byte ownQuantifier = ref self.Get(id);
            ownQuantifier = (byte)QuantifierJoin((Quantifier)ownQuantifier, (Quantifier)quantifier);
        }

        for (uint id = quantifiers.Size; id < self.Size; id++)
        {
            ref byte ownQuantifier = ref self.Get(id);
            ownQuantifier = (byte)QuantifierJoin((Quantifier)ownQuantifier, Quantifier.Zero);
        }
    }

    // ---------------------------------------------------------------- SymbolTable

    // symbol_table_id_for_name
    private static int SymbolTableIdForName(ref SymbolTable self, ReadOnlySpan<byte> name)
    {
        for (uint i = 0; i < self.Slices.Size; i++)
        {
            Slice slice = self.Slices.Get(i);
            if (slice.Length == (uint)name.Length &&
                Strncmp(self.Characters.Contents!.AsSpan((int)slice.Offset, (int)slice.Length), name, (uint)name.Length) == 0)
            {
                return (int)i;
            }
        }

        return -1;
    }

    // symbol_table_name_for_id
    private static ReadOnlySpan<byte> SymbolTableNameForId(ref SymbolTable self, ushort id)
    {
        Slice slice = self.Slices.Get(id);
        return self.Characters.Contents!.AsSpan((int)slice.Offset, (int)slice.Length);
    }

    // symbol_table_insert_name
    private static ushort SymbolTableInsertName(ref SymbolTable self, ReadOnlySpan<byte> name)
    {
        int id = SymbolTableIdForName(ref self, name);
        if (id >= 0)
        {
            return (ushort)id;
        }

        Slice slice = new Slice(self.Characters.Size, (uint)name.Length);
        self.Characters.GrowBy((uint)name.Length + 1);
        name.CopyTo(self.Characters.Contents!.AsSpan((int)slice.Offset, name.Length));
        self.Characters.Get(self.Characters.Size - 1) = 0;
        self.Slices.Push(slice);
        return (ushort)(self.Slices.Size - 1);
    }

    // strncmp of the C runtime over the stored bytes: the comparison stops at the first
    // difference or when both bytes are the terminator (0).
    private static int Strncmp(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, uint length)
    {
        for (uint i = 0; i < length; i++)
        {
            byte l = i < (uint)left.Length ? left[(int)i] : (byte)0;
            byte r = i < (uint)right.Length ? right[(int)i] : (byte)0;
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

    // ---------------------------------------------------------------- QueryStep

    // query_step__new
    private static QueryStep QueryStepNew(ushort symbol, ushort depth, bool isImmediate)
    {
        QueryStep step = new QueryStep
        {
            Symbol = symbol,
            Depth = depth,
            AlternativeIndex = None,
            IsImmediate = isImmediate,
        };
        for (int i = 0; i < MaxStepCaptureCount; i++)
        {
            step.CaptureIds[i] = None;
        }

        return step;
    }

    // query_step__add_capture
    private static void QueryStepAddCapture(ref QueryStep self, ushort captureId)
    {
        for (int i = 0; i < MaxStepCaptureCount; i++)
        {
            if (self.CaptureIds[i] == None)
            {
                self.CaptureIds[i] = captureId;
                break;
            }
        }
    }

    // query_step__remove_capture
    private static void QueryStepRemoveCapture(ref QueryStep self, ushort captureId)
    {
        for (int i = 0; i < MaxStepCaptureCount; i++)
        {
            if (self.CaptureIds[i] == captureId)
            {
                self.CaptureIds[i] = None;
                while (i + 1 < MaxStepCaptureCount)
                {
                    if (self.CaptureIds[i + 1] == None)
                    {
                        break;
                    }

                    self.CaptureIds[i] = self.CaptureIds[i + 1];
                    self.CaptureIds[i + 1] = None;
                    i++;
                }

                break;
            }
        }
    }

    // ---------------------------------------------------------------- AnalysisState

    // analysis_state__recursion_depth
    private static uint AnalysisStateRecursionDepth(AnalysisState self)
    {
        uint result = 0;
        for (uint i = 0; i < self.Depth; i++)
        {
            ushort symbol = self.Stack[i].ParentSymbol;
            for (uint j = 0; j < i; j++)
            {
                if (self.Stack[j].ParentSymbol == symbol)
                {
                    result++;
                    break;
                }
            }
        }

        return result;
    }

    // analysis_state__compare
    private static int AnalysisStateCompare(AnalysisState self, AnalysisState other)
    {
        if (self.Depth < other.Depth)
        {
            return 1;
        }

        for (uint i = 0; i < self.Depth; i++)
        {
            if (i >= other.Depth)
            {
                return -1;
            }

            AnalysisStateEntry s1 = self.Stack[i];
            AnalysisStateEntry s2 = other.Stack[i];
            if (s1.ChildIndex < s2.ChildIndex)
            {
                return -1;
            }

            if (s1.ChildIndex > s2.ChildIndex)
            {
                return 1;
            }

            if (s1.ParentSymbol < s2.ParentSymbol)
            {
                return -1;
            }

            if (s1.ParentSymbol > s2.ParentSymbol)
            {
                return 1;
            }

            if (s1.ParseState < s2.ParseState)
            {
                return -1;
            }

            if (s1.ParseState > s2.ParseState)
            {
                return 1;
            }

            if (s1.FieldId < s2.FieldId)
            {
                return -1;
            }

            if (s1.FieldId > s2.FieldId)
            {
                return 1;
            }
        }

        if (self.StepIndex < other.StepIndex)
        {
            return -1;
        }

        if (self.StepIndex > other.StepIndex)
        {
            return 1;
        }

        return 0;
    }

    // analysis_state__top
    private static ref AnalysisStateEntry AnalysisStateTop(AnalysisState self)
    {
        if (self.Depth == 0)
        {
            return ref self.Stack[0];
        }

        return ref self.Stack[self.Depth - 1];
    }

    // analysis_state__has_supertype
    private static bool AnalysisStateHasSupertype(AnalysisState self, ushort symbol)
    {
        for (uint i = 0; i < self.Depth; i++)
        {
            if (self.Stack[i].ParentSymbol == symbol)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- AnalysisStateSet

    // analysis_state_pool__clone_or_reuse: obtains an AnalysisState instance, either by
    // consuming one from this set's object pool, or by cloning one from scratch.
    private static AnalysisState AnalysisStatePoolCloneOrReuse(ref TsArray<AnalysisState> self, AnalysisState borrowedItem)
    {
        AnalysisState newItem;
        if (self.Size > 0)
        {
            newItem = self.Pop();
        }
        else
        {
            newItem = new AnalysisState();
        }

        newItem.AssignFrom(borrowedItem);
        return newItem;
    }

    // analysis_state_set__insert_sorted: inserts a clone of the passed-in item at the
    // appropriate position to maintain ordering in this set.
    private static void AnalysisStateSetInsertSorted(
        ref TsArray<AnalysisState> self,
        ref TsArray<AnalysisState> pool,
        AnalysisState borrowedItem)
    {
        self.SearchSortedWith(AnalysisStateCompare, borrowedItem, out uint index, out bool exists);
        if (!exists)
        {
            AnalysisState newItem = AnalysisStatePoolCloneOrReuse(ref pool, borrowedItem);
            self.Insert(index, newItem);
        }
    }

    // analysis_state_set__push: inserts a clone of the passed-in item at the end position
    // of this list.
    private static void AnalysisStateSetPush(
        ref TsArray<AnalysisState> self,
        ref TsArray<AnalysisState> pool,
        AnalysisState borrowedItem)
    {
        AnalysisState newItem = AnalysisStatePoolCloneOrReuse(ref pool, borrowedItem);
        self.Push(newItem);
    }

    // analysis_state_set__clear: removes all items from this set, returning it to an
    // empty state.
    private static void AnalysisStateSetClear(ref TsArray<AnalysisState> self, ref TsArray<AnalysisState> pool)
    {
        pool.PushAll(self);
        self.Clear();
    }

    // ---------------------------------------------------------------- QueryAnalyzer

    // query_analysis__new
    private static QueryAnalysis QueryAnalysisNew()
    {
        return new QueryAnalysis
        {
            DidAbort = false,
        };
    }

    // analysis_subgraph_node__compare
    private static int AnalysisSubgraphNodeCompare(AnalysisSubgraphNode self, AnalysisSubgraphNode other)
    {
        if (self.State < other.State)
        {
            return -1;
        }

        if (self.State > other.State)
        {
            return 1;
        }

        if (self.ChildIndex < other.ChildIndex)
        {
            return -1;
        }

        if (self.ChildIndex > other.ChildIndex)
        {
            return 1;
        }

        if (!self.Done && other.Done)
        {
            return -1;
        }

        if (self.Done && !other.Done)
        {
            return 1;
        }

        if (self.ProductionId < other.ProductionId)
        {
            return -1;
        }

        if (self.ProductionId > other.ProductionId)
        {
            return 1;
        }

        return 0;
    }

    // ---------------------------------------------------------------- Query

    // ts_query__pattern_map_search. This returns `true` if the symbol is present and
    // `false` otherwise. If the symbol is not present `result` is set to the index where
    // the symbol should be inserted.
    internal bool PatternMapSearch(ushort needle, out uint result)
    {
        uint baseIndex = _wildcardRootPatternCount;
        uint size = _patternMap.Size - baseIndex;
        if (size == 0)
        {
            result = baseIndex;
            return false;
        }

        while (size > 1)
        {
            uint halfSize = size / 2;
            uint midIndex = baseIndex + halfSize;
            ushort midSymbol = _steps.Get(_patternMap.Get(midIndex).StepIndex).Symbol;
            if (needle > midSymbol)
            {
                baseIndex = midIndex;
            }

            size -= halfSize;
        }

        ushort symbol = _steps.Get(_patternMap.Get(baseIndex).StepIndex).Symbol;

        if (needle > symbol)
        {
            baseIndex++;
            if (baseIndex < _patternMap.Size)
            {
                symbol = _steps.Get(_patternMap.Get(baseIndex).StepIndex).Symbol;
            }
        }

        result = baseIndex;
        return needle == symbol;
    }

    // ts_query__pattern_map_insert: insert a new pattern's start index into the pattern
    // map, maintaining the pattern map's ordering invariant.
    private void PatternMapInsert(ushort symbol, PatternEntry newEntry)
    {
        PatternMapSearch(symbol, out uint index);

        // Ensure that the entries are sorted not only by symbol, but also
        // by pattern_index. This way, states for earlier patterns will be
        // initiated first, which allows the ordering of the states array
        // to be maintained more efficiently.
        while (index < _patternMap.Size)
        {
            ref PatternEntry entry = ref _patternMap.Get(index);
            if (_steps.Get(entry.StepIndex).Symbol == symbol &&
                entry.PatternIndex < newEntry.PatternIndex)
            {
                index++;
            }
            else
            {
                break;
            }
        }

        _patternMap.Insert(index, newEntry);
    }

    // ts_query__perform_analysis: walk the subgraph for this non-terminal, tracking all
    // of the possible sequences of progress within the pattern.
    private void PerformAnalysis(ref TsArray<AnalysisSubgraph> subgraphs, QueryAnalysis analysis)
    {
        uint recursionDepthLimit = 0;
        uint prevFinalStepCount = 0;
        analysis.FinalStepIndices.Clear();
        analysis.FinishedParentSymbols.Clear();

        // The stack-allocated AnalysisState next_state of the C code: nothing keeps a
        // reference to it across its uses, so one instance is reused here.
        AnalysisState nextState = new AnalysisState();
        for (uint iteration = 0; ; iteration++)
        {
            if (iteration == MaxAnalysisIterationCount)
            {
                analysis.DidAbort = true;
                break;
            }

            // If no further progress can be made within the current recursion depth limit, then
            // bump the depth limit by one, and continue to process the states the exceeded the
            // limit. But only allow this if progress has been made since the last time the depth
            // limit was increased.
            if (analysis.States.Size == 0)
            {
                if (
                    analysis.DeeperStates.Size > 0 &&
                    analysis.FinalStepIndices.Size > prevFinalStepCount
                )
                {
                    prevFinalStepCount = analysis.FinalStepIndices.Size;
                    recursionDepthLimit++;
                    TsArray<AnalysisState> states = analysis.States;
                    analysis.States = analysis.DeeperStates;
                    analysis.DeeperStates = states;
                    continue;
                }

                break;
            }

            AnalysisStateSetClear(ref analysis.NextStates, ref analysis.StatePool);
            for (uint j = 0; j < analysis.States.Size; j++)
            {
                AnalysisState state = analysis.States.Get(j);

                // For efficiency, it's important to avoid processing the same analysis state more
                // than once. To achieve this, keep the states in order of ascending position within
                // their hypothetical syntax trees. In each iteration of this loop, start by advancing
                // the states that have made the least progress. Avoid advancing states that have
                // already made more progress.
                if (analysis.NextStates.Size > 0)
                {
                    int comparison = AnalysisStateCompare(state, analysis.NextStates.Back());
                    if (comparison == 0)
                    {
                        AnalysisStateSetInsertSorted(ref analysis.NextStates, ref analysis.StatePool, state);
                        continue;
                    }
                    else if (comparison > 0)
                    {
                        while (j < analysis.States.Size)
                        {
                            AnalysisStateSetPush(
                                ref analysis.NextStates,
                                ref analysis.StatePool,
                                analysis.States.Get(j));
                            j++;
                        }

                        break;
                    }
                }

                ushort parseState = AnalysisStateTop(state).ParseState;
                ushort parentSymbol = AnalysisStateTop(state).ParentSymbol;
                ushort parentFieldId = AnalysisStateTop(state).FieldId;
                uint childIndex = AnalysisStateTop(state).ChildIndex;
                QueryStep step = _steps.Get(state.StepIndex);

                subgraphs.SearchSortedBy(static s => s.Symbol, parentSymbol, out uint subgraphIndex, out bool exists);
                if (!exists)
                {
                    continue;
                }

                ref AnalysisSubgraph subgraph = ref subgraphs.Get(subgraphIndex);

                // Follow every possible path in the parse table, but only visit states that
                // are part of the subgraph for the current symbol.
                LanguageLookaheadIterator lookaheadIterator = _language.Lookaheads(parseState);
                while (lookaheadIterator.Next())
                {
                    ushort sym = lookaheadIterator.Symbol;

                    AnalysisSubgraphNode successor = new AnalysisSubgraphNode
                    {
                        State = parseState,
                        ChildIndex = (ushort)childIndex,
                    };
                    if (lookaheadIterator.Actions.Length != 0)
                    {
                        ParseActionEntry action = lookaheadIterator.Actions[lookaheadIterator.Actions.Length - 1];
                        if (action.Type == ParseActionType.Shift)
                        {
                            if (!action.ShiftExtra)
                            {
                                successor.State = action.ShiftState;
                                successor.ChildIndex++;
                            }
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else if (lookaheadIterator.NextState != 0)
                    {
                        successor.State = lookaheadIterator.NextState;
                        successor.ChildIndex++;
                    }
                    else
                    {
                        continue;
                    }

                    subgraph.Nodes.SearchSortedWith(
                        AnalysisSubgraphNodeCompare, successor, out uint nodeIndex, out bool nodeExists);
                    while (nodeIndex < subgraph.Nodes.Size)
                    {
                        AnalysisSubgraphNode node = subgraph.Nodes.Get(nodeIndex);
                        nodeIndex++;
                        if (node.State != successor.State || node.ChildIndex != successor.ChildIndex)
                        {
                            break;
                        }

                        // Use the subgraph to determine what alias and field will eventually be applied
                        // to this child node.
                        ushort alias = _language.AliasAt(node.ProductionId, childIndex);
                        ushort visibleSymbol = alias != 0
                            ? alias
                            : _language.GetSymbolMetadata(sym).Visible
                                ? _language.PublicSymbol(sym)
                                : (ushort)0;
                        ushort fieldId = parentFieldId;
                        if (fieldId == 0)
                        {
                            foreach (FieldMapEntry fieldMap in _language.FieldMap(node.ProductionId))
                            {
                                if (!fieldMap.Inherited && fieldMap.ChildIndex == childIndex)
                                {
                                    fieldId = fieldMap.FieldId;
                                    break;
                                }
                            }
                        }

                        // Create a new state that has advanced past this hypothetical subtree.
                        nextState.AssignFrom(state);
                        ref AnalysisStateEntry nextStateTop = ref AnalysisStateTop(nextState);
                        nextStateTop.ChildIndex = successor.ChildIndex;
                        nextStateTop.ParseState = successor.State;
                        if (node.Done)
                        {
                            nextStateTop.Done = true;
                        }

                        // Determine if this hypothetical child node would match the current step
                        // of the query pattern.
                        bool doesMatch = false;

                        // ERROR nodes can appear anywhere, so if the step is
                        // looking for an ERROR node, consider it potentially matchable.
                        if (step.Symbol == BuiltinSymbolError)
                        {
                            doesMatch = true;
                        }
                        else if (visibleSymbol != 0)
                        {
                            doesMatch = true;
                            if (step.Symbol == WildcardSymbol)
                            {
                                if (
                                    step.IsNamed &&
                                    !_language.GetSymbolMetadata(visibleSymbol).Named
                                )
                                {
                                    doesMatch = false;
                                }
                            }
                            else if (step.Symbol != visibleSymbol)
                            {
                                doesMatch = false;
                            }

                            if (step.Field != 0 && step.Field != fieldId)
                            {
                                doesMatch = false;
                            }

                            if (
                                step.SupertypeSymbol != 0 &&
                                !AnalysisStateHasSupertype(state, step.SupertypeSymbol)
                            )
                            {
                                doesMatch = false;
                            }
                        }

                        // If this child is hidden, then descend into it and walk through its children.
                        // If the top entry of the stack is at the end of its rule, then that entry can
                        // be replaced. Otherwise, push a new entry onto the stack.
                        else if (sym >= _language.TokenCount)
                        {
                            if (!nextStateTop.Done)
                            {
                                if (nextState.Depth + 1 >= MaxAnalysisStateDepth)
                                {
                                    analysis.DidAbort = true;
                                    continue;
                                }

                                nextState.Depth++;
                                nextStateTop = ref AnalysisStateTop(nextState);
                            }

                            nextStateTop = new AnalysisStateEntry
                            {
                                ParseState = parseState,
                                ParentSymbol = sym,
                                ChildIndex = 0,
                                FieldId = fieldId,
                                Done = false,
                            };

                            if (AnalysisStateRecursionDepth(nextState) > recursionDepthLimit)
                            {
                                AnalysisStateSetInsertSorted(
                                    ref analysis.DeeperStates,
                                    ref analysis.StatePool,
                                    nextState);
                                continue;
                            }
                        }

                        // Pop from the stack when this state reached the end of its current syntax node.
                        while (nextState.Depth > 0 && nextStateTop.Done)
                        {
                            nextState.Depth--;
                            nextStateTop = ref AnalysisStateTop(nextState);
                        }

                        // If this hypothetical child did match the current step of the query pattern,
                        // then advance to the next step at the current depth. This involves skipping
                        // over any descendant steps of the current child.
                        QueryStep nextStep = step;
                        if (doesMatch)
                        {
                            for (; ; )
                            {
                                nextState.StepIndex++;
                                nextStep = _steps.Get(nextState.StepIndex);
                                if (
                                    nextStep.Depth == PatternDoneMarker ||
                                    nextStep.Depth <= step.Depth
                                )
                                {
                                    break;
                                }
                            }
                        }
                        else if (successor.State == parseState)
                        {
                            continue;
                        }

                        for (; ; )
                        {
                            // Skip pass-through states. Although these states have alternatives, they are only
                            // used to implement repetitions, and query analysis does not need to process
                            // repetitions in order to determine whether steps are possible and definite.
                            if (nextStep.IsPassThrough)
                            {
                                nextState.StepIndex++;
                                nextStep = _steps.Get(nextState.StepIndex);
                                continue;
                            }

                            // If the pattern is finished or hypothetical parent node is complete, then
                            // record that matching can terminate at this step of the pattern. Otherwise,
                            // add this state to the list of states to process on the next iteration.
                            if (!nextStep.IsDeadEnd)
                            {
                                bool didFinishPattern = _steps.Get(nextState.StepIndex).Depth != step.Depth;
                                if (didFinishPattern)
                                {
                                    analysis.FinishedParentSymbols.InsertSortedBy(static s => (int)s, state.RootSymbol);
                                }
                                else if (nextState.Depth == 0)
                                {
                                    analysis.FinalStepIndices.InsertSortedBy(static s => (int)s, (ushort)nextState.StepIndex);
                                }
                                else
                                {
                                    AnalysisStateSetInsertSorted(ref analysis.NextStates, ref analysis.StatePool, nextState);
                                }
                            }

                            // If the state has advanced to a step with an alternative step, then add another state
                            // at that alternative step. This process is simpler than the process of actually matching a
                            // pattern during query execution, because for the purposes of query analysis, there is no
                            // need to process repetitions.
                            if (
                                doesMatch &&
                                nextStep.AlternativeIndex != None &&
                                nextStep.AlternativeIndex > nextState.StepIndex
                            )
                            {
                                nextState.StepIndex = nextStep.AlternativeIndex;
                                nextStep = _steps.Get(nextState.StepIndex);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
            }

            TsArray<AnalysisState> nextStates = analysis.States;
            analysis.States = analysis.NextStates;
            analysis.NextStates = nextStates;
        }
    }

    // ts_query__analyze_patterns
    private bool AnalyzePatterns(out uint errorOffset)
    {
        errorOffset = 0;
        TsArray<ushort> nonRootedPatternStartSteps = new TsArray<ushort>();
        for (uint i = 0; i < _patternMap.Size; i++)
        {
            ref PatternEntry pattern = ref _patternMap.Get(i);
            if (!pattern.IsRooted)
            {
                QueryStep step = _steps.Get(pattern.StepIndex);
                if (step.Symbol != WildcardSymbol)
                {
                    nonRootedPatternStartSteps.Push((ushort)i);
                }
            }
        }

        // Walk forward through all of the steps in the query, computing some
        // basic information about each step. Mark all of the steps that contain
        // captures, and record the indices of all of the steps that have child steps.
        TsArray<uint> parentStepIndices = new TsArray<uint>();
        bool allPatternsAreValid = true;
        for (uint i = 0; i < _steps.Size; i++)
        {
            ref QueryStep step = ref _steps.Get(i);
            if (step.Depth == PatternDoneMarker)
            {
                step.ParentPatternGuaranteed = true;
                step.RootPatternGuaranteed = true;
                continue;
            }

            bool hasChildren = false;
            bool isWildcard = step.Symbol == WildcardSymbol;
            step.ContainsCaptures = step.CaptureIds[0] != None;
            for (uint j = i + 1; j < _steps.Size; j++)
            {
                ref QueryStep nextStep = ref _steps.Get(j);
                if (
                    nextStep.Depth == PatternDoneMarker ||
                    nextStep.Depth <= step.Depth
                )
                {
                    break;
                }

                if (nextStep.CaptureIds[0] != None)
                {
                    step.ContainsCaptures = true;
                }

                if (!isWildcard)
                {
                    nextStep.RootPatternGuaranteed = true;
                    nextStep.ParentPatternGuaranteed = true;
                }

                hasChildren = true;
            }

            if (hasChildren)
            {
                if (!isWildcard)
                {
                    parentStepIndices.Push(i);
                }
                else if (step.SupertypeSymbol != 0 && _language.AbiVersion >= LanguageVersionWithReservedWords)
                {
                    // Look at the child steps to see if any aren't valid subtypes for this supertype.
                    ReadOnlySpan<ushort> subtypes = _language.Subtypes(step.SupertypeSymbol);

                    for (uint j = i + 1; j < _steps.Size; j++)
                    {
                        QueryStep childStep = _steps.Get(j);
                        if (childStep.Depth == PatternDoneMarker || childStep.Depth <= step.Depth)
                        {
                            break;
                        }

                        if (childStep.Depth == step.Depth + 1 && childStep.Symbol != WildcardSymbol)
                        {
                            bool isValidSubtype = false;
                            for (uint k = 0; k < (uint)subtypes.Length; k++)
                            {
                                if (childStep.Symbol == subtypes[(int)k])
                                {
                                    isValidSubtype = true;
                                    break;
                                }
                            }

                            if (!isValidSubtype)
                            {
                                for (uint offsetIdx = 0; offsetIdx < _stepOffsets.Size; offsetIdx++)
                                {
                                    ref StepOffset stepOffset = ref _stepOffsets.Get(offsetIdx);
                                    if (stepOffset.StepIndex >= j)
                                    {
                                        errorOffset = stepOffset.ByteOffset;
                                        allPatternsAreValid = false;
                                        goto SupertypeCleanup;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // For every parent symbol in the query, initialize an 'analysis subgraph'.
        // This subgraph lists all of the states in the parse table that are directly
        // involved in building subtrees for this symbol.
        //
        // In addition to the parent symbols in the query, construct subgraphs for all
        // of the hidden symbols in the grammar, because these might occur within
        // one of the parent nodes, such that their children appear to belong to the
        // parent.
        TsArray<AnalysisSubgraph> subgraphs = new TsArray<AnalysisSubgraph>();
        for (uint i = 0; i < parentStepIndices.Size; i++)
        {
            uint parentStepIndex = parentStepIndices.Get(i);
            ushort parentSymbol = _steps.Get(parentStepIndex).Symbol;
            AnalysisSubgraph subgraph = new AnalysisSubgraph { Symbol = parentSymbol };
            subgraphs.InsertSortedBy(static s => (int)s.Symbol, subgraph);
        }

        for (ushort sym = (ushort)_language.TokenCount; sym < (ushort)_language.RawSymbolCount; sym++)
        {
            if (!_language.GetSymbolMetadata(sym).Visible)
            {
                AnalysisSubgraph subgraph = new AnalysisSubgraph { Symbol = sym };
                subgraphs.InsertSortedBy(static s => (int)s.Symbol, subgraph);
            }
        }

        // Scan the parse table to find the data needed to populate these subgraphs.
        // Collect three things during this scan:
        //   1) All of the parse states where one of these symbols can start.
        //   2) All of the parse states where one of these symbols can end, along
        //      with information about the node that would be created.
        //   3) A list of predecessor states for each state.
        StatePredecessorMap predecessorMap = new StatePredecessorMap(_language);
        for (ushort state = 1; state < (ushort)_language.StateCount; state++)
        {
            LanguageLookaheadIterator lookaheadIterator = _language.Lookaheads(state);
            while (lookaheadIterator.Next())
            {
                if (lookaheadIterator.Actions.Length != 0)
                {
                    for (uint i = 0; i < (uint)lookaheadIterator.Actions.Length; i++)
                    {
                        ParseActionEntry action = lookaheadIterator.Actions[(int)i];
                        if (action.Type == ParseActionType.Reduce)
                        {
                            ReadOnlySpan<ushort> aliases = _language.AliasesForSymbol(action.ReduceSymbol);
                            for (int symbol = 0; symbol < aliases.Length; symbol++)
                            {
                                subgraphs.SearchSortedBy(static s => s.Symbol, aliases[symbol], out uint subgraphIndex, out bool exists);
                                if (exists)
                                {
                                    ref AnalysisSubgraph subgraph = ref subgraphs.Get(subgraphIndex);
                                    if (subgraph.Nodes.Size == 0 || subgraph.Nodes.Back().State != state)
                                    {
                                        subgraph.Nodes.Push(new AnalysisSubgraphNode
                                        {
                                            State = state,
                                            ProductionId = action.ReduceProductionId,
                                            ChildIndex = action.ReduceChildCount,
                                            Done = true,
                                        });
                                    }
                                }
                            }
                        }
                        else if (action.Type == ParseActionType.Shift && !action.ShiftExtra)
                        {
                            ushort nextState = action.ShiftState;
                            predecessorMap.Add(nextState, state);
                        }
                    }
                }
                else if (lookaheadIterator.NextState != 0)
                {
                    if (lookaheadIterator.NextState != state)
                    {
                        predecessorMap.Add(lookaheadIterator.NextState, state);
                    }

                    if (_language.StateIsPrimary(state))
                    {
                        ReadOnlySpan<ushort> aliases = _language.AliasesForSymbol(lookaheadIterator.Symbol);
                        for (int symbol = 0; symbol < aliases.Length; symbol++)
                        {
                            subgraphs.SearchSortedBy(static s => s.Symbol, aliases[symbol], out uint subgraphIndex, out bool exists);
                            if (exists)
                            {
                                ref AnalysisSubgraph subgraph = ref subgraphs.Get(subgraphIndex);
                                if (
                                    subgraph.StartStates.Size == 0 ||
                                    subgraph.StartStates.Back() != state
                                )
                                {
                                    subgraph.StartStates.Push(state);
                                }
                            }
                        }
                    }
                }
            }
        }

        // For each subgraph, compute the preceding states by walking backward
        // from the end states using the predecessor map.
        TsArray<AnalysisSubgraphNode> nextNodes = new TsArray<AnalysisSubgraphNode>();
        for (uint i = 0; i < subgraphs.Size; i++)
        {
            ref AnalysisSubgraph subgraph = ref subgraphs.Get(i);
            if (subgraph.Nodes.Size == 0)
            {
                subgraph.StartStates.Delete();
                subgraphs.Erase(i);
                i--;
                continue;
            }

            nextNodes.Assign(subgraph.Nodes.AsSpan());
            while (nextNodes.Size > 0)
            {
                AnalysisSubgraphNode node = nextNodes.Pop();
                if (node.ChildIndex > 1)
                {
                    ReadOnlySpan<ushort> predecessors = predecessorMap.Get(node.State, out uint predecessorCount);
                    for (uint j = 0; j < predecessorCount; j++)
                    {
                        AnalysisSubgraphNode predecessorNode = new AnalysisSubgraphNode
                        {
                            State = predecessors[(int)j],
                            ChildIndex = (ushort)(node.ChildIndex - 1),
                            ProductionId = node.ProductionId,
                            Done = false,
                        };
                        subgraph.Nodes.SearchSortedWith(
                            AnalysisSubgraphNodeCompare, predecessorNode, out uint index, out bool exists);
                        if (!exists)
                        {
                            subgraph.Nodes.Insert(index, predecessorNode);
                            nextNodes.Push(predecessorNode);
                        }
                    }
                }
            }
        }

        // For each non-terminal pattern, determine if the pattern can successfully match,
        // and identify all of the possible children within the pattern where matching could fail.
        QueryAnalysis analysis = QueryAnalysisNew();
        for (uint i = 0; i < parentStepIndices.Size; i++)
        {
            ushort parentStepIndex = (ushort)parentStepIndices.Get(i);
            ushort parentDepth = _steps.Get(parentStepIndex).Depth;
            ushort parentSymbol = _steps.Get(parentStepIndex).Symbol;
            if (parentSymbol == BuiltinSymbolError)
            {
                continue;
            }

            // Find the subgraph that corresponds to this pattern's root symbol. If the pattern's
            // root symbol is a terminal, then return an error.
            subgraphs.SearchSortedBy(static s => s.Symbol, parentSymbol, out uint subgraphIndex, out bool exists);
            if (!exists)
            {
                uint firstChildStepIndex = (uint)(parentStepIndex + 1);
                _stepOffsets.SearchSortedBy(static s => (int)s.StepIndex, (int)firstChildStepIndex, out uint j, out bool childExists);
                Debug.Assert(childExists);
                errorOffset = _stepOffsets.Get(j).ByteOffset;
                allPatternsAreValid = false;
                break;
            }

            // Initialize an analysis state at every parse state in the table where
            // this parent symbol can occur.
            ref AnalysisSubgraph subgraph = ref subgraphs.Get(subgraphIndex);
            AnalysisStateSetClear(ref analysis.States, ref analysis.StatePool);
            AnalysisStateSetClear(ref analysis.DeeperStates, ref analysis.StatePool);
            for (uint j = 0; j < subgraph.StartStates.Size; j++)
            {
                ushort parseState = subgraph.StartStates.Get(j);
                AnalysisState initialState = new AnalysisState
                {
                    StepIndex = (ushort)(parentStepIndex + 1),
                    Depth = 1,
                    RootSymbol = parentSymbol,
                };
                initialState.Stack[0] = new AnalysisStateEntry
                {
                    ParseState = parseState,
                    ParentSymbol = parentSymbol,
                    ChildIndex = 0,
                    FieldId = 0,
                    Done = false,
                };
                AnalysisStateSetPush(ref analysis.States, ref analysis.StatePool, initialState);
            }

            analysis.DidAbort = false;
            PerformAnalysis(ref subgraphs, analysis);

            // If this pattern could not be fully analyzed, then every step should
            // be considered fallible.
            if (analysis.DidAbort)
            {
                for (uint j = (uint)(parentStepIndex + 1); j < _steps.Size; j++)
                {
                    ref QueryStep step = ref _steps.Get(j);
                    if (
                        step.Depth <= parentDepth ||
                        step.Depth == PatternDoneMarker
                    )
                    {
                        break;
                    }

                    if (!step.IsDeadEnd)
                    {
                        step.ParentPatternGuaranteed = false;
                        step.RootPatternGuaranteed = false;
                    }
                }

                continue;
            }

            // If this pattern cannot match, store the pattern index so that it can be
            // returned to the caller.
            if (analysis.FinishedParentSymbols.Size == 0)
            {
                ushort impossibleStepIndex;
                if (analysis.FinalStepIndices.Size > 0)
                {
                    impossibleStepIndex = analysis.FinalStepIndices.Back();
                }
                else
                {
                    // If there isn't a final step, then that means the parent step itself is unreachable.
                    impossibleStepIndex = parentStepIndex;
                }

                _stepOffsets.SearchSortedBy(static s => (int)s.StepIndex, (int)impossibleStepIndex, out uint j, out bool impossibleExists);
                if (j >= _stepOffsets.Size)
                {
                    j = _stepOffsets.Size - 1;
                }

                errorOffset = _stepOffsets.Get(j).ByteOffset;
                allPatternsAreValid = false;
                break;
            }

            // Mark as fallible any step where a match terminated.
            // Later, this property will be propagated to all of the step's predecessors.
            for (uint j = 0; j < analysis.FinalStepIndices.Size; j++)
            {
                uint finalStepIndex = analysis.FinalStepIndices.Get(j);
                ref QueryStep step = ref _steps.Get(finalStepIndex);
                if (
                    step.Depth != PatternDoneMarker &&
                    step.Depth > parentDepth &&
                    !step.IsDeadEnd
                )
                {
                    step.ParentPatternGuaranteed = false;
                    step.RootPatternGuaranteed = false;
                }
            }
        }

        // Mark as indefinite any step with captures that are used in predicates.
        TsArray<ushort> predicateCaptureIds = new TsArray<ushort>();
        for (uint i = 0; i < _patterns.Size; i++)
        {
            ref QueryPattern pattern = ref _patterns.Get(i);

            // Gather all of the captures that are used in predicates for this pattern.
            predicateCaptureIds.Clear();
            for (
                uint start = pattern.PredicateSteps.Offset,
                end = start + pattern.PredicateSteps.Length,
                j = start; j < end; j++
            )
            {
                QueryPredicateStep step = _predicateSteps.Get(j);
                if (step.Type == QueryPredicateStepType.Capture)
                {
                    ushort valueId = (ushort)step.ValueId;
                    predicateCaptureIds.InsertSortedBy(static s => (int)s, valueId);
                }
            }

            // Find all of the steps that have these captures.
            for (
                uint start = pattern.Steps.Offset,
                end = start + pattern.Steps.Length,
                j = start; j < end; j++
            )
            {
                ref QueryStep step = ref _steps.Get(j);
                for (uint k = 0; k < MaxStepCaptureCount; k++)
                {
                    ushort captureId = step.CaptureIds[(int)k];
                    if (captureId == None)
                    {
                        break;
                    }

                    predicateCaptureIds.SearchSortedBy(static s => (int)s, captureId, out uint index, out bool exists);
                    if (exists)
                    {
                        step.RootPatternGuaranteed = false;
                        break;
                    }
                }
            }
        }

        // Propagate fallibility. If a pattern is fallible at a given step, then it is
        // fallible at all of its preceding steps.
        bool done = _steps.Size == 0;
        while (!done)
        {
            done = true;
            for (uint i = _steps.Size - 1; i > 0; i--)
            {
                ref QueryStep step = ref _steps.Get(i);
                if (step.Depth == PatternDoneMarker)
                {
                    continue;
                }

                // Determine if this step is definite or has definite alternatives.
                bool parentPatternGuaranteed = false;
                for (; ; )
                {
                    if (step.RootPatternGuaranteed)
                    {
                        parentPatternGuaranteed = true;
                        break;
                    }

                    if (step.AlternativeIndex == None || step.AlternativeIndex < i)
                    {
                        break;
                    }

                    step = ref _steps.Get(step.AlternativeIndex);
                }

                // If not, mark its predecessor as indefinite.
                if (!parentPatternGuaranteed)
                {
                    ref QueryStep prevStep = ref _steps.Get(i - 1);
                    if (
                        !prevStep.IsDeadEnd &&
                        prevStep.Depth != PatternDoneMarker &&
                        prevStep.RootPatternGuaranteed
                    )
                    {
                        prevStep.RootPatternGuaranteed = false;
                        done = false;
                    }
                }
            }
        }

        // Determine which repetition symbols in this language have the possibility
        // of matching non-rooted patterns in this query. These repetition symbols
        // prevent certain optimizations with range restrictions.
        analysis.DidAbort = false;
        for (uint i = 0; i < nonRootedPatternStartSteps.Size; i++)
        {
            ushort patternEntryIndex = nonRootedPatternStartSteps.Get(i);
            ref PatternEntry patternEntry = ref _patternMap.Get(patternEntryIndex);

            AnalysisStateSetClear(ref analysis.States, ref analysis.StatePool);
            AnalysisStateSetClear(ref analysis.DeeperStates, ref analysis.StatePool);
            for (uint j = 0; j < subgraphs.Size; j++)
            {
                ref AnalysisSubgraph subgraph = ref subgraphs.Get(j);
                SymbolMetadata metadata = _language.GetSymbolMetadata(subgraph.Symbol);
                if (metadata.Visible || metadata.Named)
                {
                    continue;
                }

                for (uint k = 0; k < subgraph.StartStates.Size; k++)
                {
                    ushort parseState = subgraph.StartStates.Get(k);
                    AnalysisState initialState = new AnalysisState
                    {
                        StepIndex = patternEntry.StepIndex,
                        RootSymbol = subgraph.Symbol,
                        Depth = 1,
                    };
                    initialState.Stack[0] = new AnalysisStateEntry
                    {
                        ParseState = parseState,
                        ParentSymbol = subgraph.Symbol,
                        ChildIndex = 0,
                        FieldId = 0,
                        Done = false,
                    };
                    AnalysisStateSetPush(ref analysis.States, ref analysis.StatePool, initialState);
                }
            }

            PerformAnalysis(ref subgraphs, analysis);

            if (analysis.FinishedParentSymbols.Size > 0)
            {
                _patterns.Get(patternEntry.PatternIndex).IsNonLocal = true;
            }

            for (uint k = 0; k < analysis.FinishedParentSymbols.Size; k++)
            {
                ushort symbol = analysis.FinishedParentSymbols.Get(k);
                _repeatSymbolsWithRootlessPatterns.InsertSortedBy(static s => (int)s, symbol);
            }
        }

        // Cleanup: the garbage collector frees the arrays (the array_delete calls of the
        // C code, including the jumps from the supertype check above to supertype_cleanup).

SupertypeCleanup:
        return allPatternsAreValid;
    }

    // ts_query__add_negated_fields
    private void AddNegatedFields(ushort stepIndex, ushort[] fieldIds, ushort fieldCount)
    {
        ref QueryStep step = ref _steps.Get(stepIndex);

        // The negated field array stores a list of field lists, separated by zeros.
        // Try to find the start index of an existing list that matches this new list.
        bool failedMatch = false;
        uint matchCount = 0;
        uint startI = 0;
        for (uint i = 0; i < _negatedFields.Size; i++)
        {
            ushort existingFieldId = _negatedFields.Get(i);

            // At each zero value, terminate the match attempt. If we've exactly
            // matched the new field list, then reuse this index. Otherwise,
            // start over the matching process.
            if (existingFieldId == 0)
            {
                if (matchCount == fieldCount)
                {
                    step.NegatedFieldListId = (ushort)startI;
                    return;
                }
                else
                {
                    startI = i + 1;
                    matchCount = 0;
                    failedMatch = false;
                }
            }

            // If the existing list matches our new list so far, then advance
            // to the next element of the new list.
            else if (
                matchCount < fieldCount &&
                existingFieldId == fieldIds[matchCount] &&
                !failedMatch
            )
            {
                matchCount++;
            }

            // Otherwise, this existing list has failed to match.
            else
            {
                matchCount = 0;
                failedMatch = true;
            }
        }

        step.NegatedFieldListId = (ushort)_negatedFields.Size;
        _negatedFields.Extend(fieldIds.AsSpan(0, fieldCount));
        _negatedFields.Push(0);
    }

    // ts_query__parse_string_literal
    private QueryError ParseStringLiteral(ref Stream stream)
    {
        uint stringStart = stream.Position;
        if (stream.Next != '"')
        {
            return QueryError.Syntax;
        }

        StreamAdvance(ref stream);
        uint prevPosition = stream.Position;

        bool isEscaped = false;
        _stringBuffer.Clear();
        for (; ; )
        {
            if (isEscaped)
            {
                isEscaped = false;
                switch (stream.Next)
                {
                    case 'n':
                        _stringBuffer.Push((byte)'\n');
                        break;
                    case 'r':
                        _stringBuffer.Push((byte)'\r');
                        break;
                    case 't':
                        _stringBuffer.Push((byte)'\t');
                        break;
                    case '0':
                        _stringBuffer.Push(0);
                        break;
                    default:
                        _stringBuffer.Extend(stream.Source.AsSpan((int)stream.Position, (int)stream.NextSize));
                        break;
                }

                prevPosition = stream.Position + stream.NextSize;
            }
            else
            {
                if (stream.Next == '\\')
                {
                    _stringBuffer.Extend(stream.Source.AsSpan((int)prevPosition, (int)(stream.Position - prevPosition)));
                    prevPosition = stream.Position + 1;
                    isEscaped = true;
                }
                else if (stream.Next == '"')
                {
                    _stringBuffer.Extend(stream.Source.AsSpan((int)prevPosition, (int)(stream.Position - prevPosition)));
                    StreamAdvance(ref stream);
                    return QueryError.None;
                }
                else if (stream.Next == '\n')
                {
                    StreamReset(ref stream, stringStart);
                    return QueryError.Syntax;
                }
            }

            if (!StreamAdvance(ref stream))
            {
                StreamReset(ref stream, stringStart);
                return QueryError.Syntax;
            }
        }
    }

    // ts_query__parse_predicate: parse a single predicate associated with a pattern,
    // adding it to the query's internal `predicate_steps` array. Predicates are arbitrary
    // S-expressions associated with a pattern which are meant to be handled at a higher
    // level of abstraction. They can contain '@'-prefixed capture names, double-quoted
    // strings, and bare symbols, which also represent strings.
    private QueryError ParsePredicate(ref Stream stream)
    {
        if (!StreamIsIdentStart(ref stream))
        {
            return QueryError.Syntax;
        }

        uint predicateName = stream.Position;
        StreamScanIdentifier(ref stream);
        if (stream.Next != '?' && stream.Next != '!')
        {
            return QueryError.Syntax;
        }

        StreamAdvance(ref stream);
        uint length = stream.Position - predicateName;
        ushort id = SymbolTableInsertName(ref _predicateValues, stream.Source.AsSpan((int)predicateName, (int)length));
        _predicateSteps.Push(new QueryPredicateStep
        {
            Type = QueryPredicateStepType.StringValue,
            ValueId = id,
        });
        StreamSkipWhitespace(ref stream);

        for (; ; )
        {
            if (stream.Next == ')')
            {
                StreamAdvance(ref stream);
                StreamSkipWhitespace(ref stream);
                _predicateSteps.Push(new QueryPredicateStep
                {
                    Type = QueryPredicateStepType.Done,
                    ValueId = 0,
                });
                break;
            }

            // Parse an '@'-prefixed capture name
            else if (stream.Next == '@')
            {
                StreamAdvance(ref stream);

                // Parse the capture name
                if (!StreamIsIdentStart(ref stream))
                {
                    return QueryError.Syntax;
                }

                uint captureName = stream.Position;
                StreamScanIdentifier(ref stream);
                uint captureLength = stream.Position - captureName;

                // Add the capture id to the first step of the pattern
                int captureId = SymbolTableIdForName(ref _captures, stream.Source.AsSpan((int)captureName, (int)captureLength));
                if (captureId == -1)
                {
                    StreamReset(ref stream, captureName);
                    return QueryError.Capture;
                }

                _predicateSteps.Push(new QueryPredicateStep
                {
                    Type = QueryPredicateStepType.Capture,
                    ValueId = (uint)captureId,
                });
            }

            // Parse a string literal
            else if (stream.Next == '"')
            {
                QueryError e = ParseStringLiteral(ref stream);
                if (e != QueryError.None)
                {
                    return e;
                }

                ushort queryId = SymbolTableInsertName(ref _predicateValues, _stringBuffer.AsSpan());
                _predicateSteps.Push(new QueryPredicateStep
                {
                    Type = QueryPredicateStepType.StringValue,
                    ValueId = queryId,
                });
            }

            // Parse a bare symbol
            else if (StreamIsIdentStart(ref stream))
            {
                uint symbolStart = stream.Position;
                StreamScanIdentifier(ref stream);
                uint symbolLength = stream.Position - symbolStart;
                ushort queryId = SymbolTableInsertName(ref _predicateValues, stream.Source.AsSpan((int)symbolStart, (int)symbolLength));
                _predicateSteps.Push(new QueryPredicateStep
                {
                    Type = QueryPredicateStepType.StringValue,
                    ValueId = queryId,
                });
            }
            else
            {
                return QueryError.Syntax;
            }

            StreamSkipWhitespace(ref stream);
        }

        return QueryError.None;
    }

    // ts_query__parse_pattern: read one S-expression pattern from the stream, and
    // incorporate it into the query's internal state machine representation. For nested
    // patterns, this function calls itself recursively.
    //
    // The caller is responsible for passing in a dedicated CaptureQuantifiers.
    // These should not be shared between different calls to ts_query__parse_pattern!
    private QueryError ParsePattern(
        ref Stream stream,
        uint depth,
        bool isImmediate,
        bool isInsideAlternation,
        ref CaptureQuantifiers captureQuantifiers)
    {
        if (stream.Next == 0)
        {
            return QueryError.Syntax;
        }

        if (stream.Next == ')' || stream.Next == ']')
        {
            return ParentDone;
        }

        uint startingStepIndex = _steps.Size;

        // Store the byte offset of each step in the query.
        if (
            _stepOffsets.Size == 0 ||
            _stepOffsets.Back().StepIndex != startingStepIndex
        )
        {
            _stepOffsets.Push(new StepOffset
            {
                StepIndex = (ushort)startingStepIndex,
                ByteOffset = StreamOffset(ref stream),
            });
        }

        // An open bracket is the start of an alternation.
        if (stream.Next == '[')
        {
            StreamAdvance(ref stream);
            StreamSkipWhitespace(ref stream);

            // Parse each branch, and add a placeholder step in between the branches.
            TsArray<uint> branchStepIndices = new TsArray<uint>();
            CaptureQuantifiers branchCaptureQuantifiers = CaptureQuantifiersNew();
            for (; ; )
            {
                uint startIndex = _steps.Size;
                QueryError e = ParsePattern(
                    ref stream,
                    depth,
                    isImmediate,
                    isInsideAlternation: true,
                    ref branchCaptureQuantifiers);

                if (e == ParentDone)
                {
                    if (stream.Next == ']' && branchStepIndices.Size > 0)
                    {
                        StreamAdvance(ref stream);
                        break;
                    }

                    e = QueryError.Syntax;
                }

                if (e != QueryError.None)
                {
                    return e;
                }

                if (startIndex == startingStepIndex)
                {
                    CaptureQuantifiersReplace(ref captureQuantifiers, ref branchCaptureQuantifiers);
                }
                else
                {
                    CaptureQuantifiersJoinAll(ref captureQuantifiers, ref branchCaptureQuantifiers);
                }

                branchStepIndices.Push(startIndex);
                _steps.Push(QueryStepNew(0, (ushort)depth, isImmediate: false));
                CaptureQuantifiersClear(ref branchCaptureQuantifiers);
            }

            _ = _steps.Pop();

            // For all of the branches except for the last one, add the subsequent branch as an
            // alternative, and link the end of the branch to the current end of the steps.
            for (uint i = 0; i < branchStepIndices.Size - 1; i++)
            {
                uint stepIndex = branchStepIndices.Get(i);
                uint nextStepIndex = branchStepIndices.Get(i + 1);
                _steps.Get(stepIndex).AlternativeIndex = (ushort)nextStepIndex;
                _steps.Get(nextStepIndex - 1).AlternativeIndex = (ushort)_steps.Size;
                _steps.Get(nextStepIndex - 1).IsDeadEnd = true;
            }
        }

        // An open parenthesis can be the start of three possible constructs:
        // * A grouped sequence
        // * A predicate
        // * A named node
        else if (stream.Next == '(')
        {
            StreamAdvance(ref stream);
            StreamSkipWhitespace(ref stream);

            // If this parenthesis is followed by a node, then it represents a grouped sequence.
            if (stream.Next == '(' || stream.Next == '"' || stream.Next == '[')
            {
                bool childIsImmediate = isImmediate;
                CaptureQuantifiers childCaptureQuantifiers = CaptureQuantifiersNew();
                for (; ; )
                {
                    if (stream.Next == '.')
                    {
                        uint anchorStart = stream.Position;
                        childIsImmediate = true;
                        StreamAdvance(ref stream);
                        StreamSkipWhitespace(ref stream);

                        // A `.` at a group's end has no sibling to anchor, and a group is not a
                        // node, so there is no last child to anchor against.
                        if (stream.Next == ')')
                        {
                            StreamReset(ref stream, anchorStart);
                            return QueryError.Syntax;
                        }
                    }

                    QueryError e = ParsePattern(
                        ref stream,
                        depth,
                        childIsImmediate,
                        isInsideAlternation,
                        ref childCaptureQuantifiers);
                    if (e == ParentDone)
                    {
                        if (stream.Next == ')')
                        {
                            StreamAdvance(ref stream);
                            break;
                        }

                        e = QueryError.Syntax;
                    }

                    if (e != QueryError.None)
                    {
                        return e;
                    }

                    CaptureQuantifiersAddAll(ref captureQuantifiers, ref childCaptureQuantifiers);
                    CaptureQuantifiersClear(ref childCaptureQuantifiers);
                    childIsImmediate = false;
                }
            }

            // A dot/pound character indicates the start of a predicate.
            else if (stream.Next == '.' || stream.Next == '#')
            {
                StreamAdvance(ref stream);
                return ParsePredicate(ref stream);
            }

            // Otherwise, this parenthesis is the start of a named node.
            else
            {
                ushort symbol;
                bool isMissing = false;
                uint nodeName = stream.Position;

                // Parse a normal node name
                if (StreamIsIdentStart(ref stream))
                {
                    StreamScanIdentifier(ref stream);
                    uint length = stream.Position - nodeName;

                    // Parse the wildcard symbol
                    if (length == 1 && stream.Source[nodeName] == '_')
                    {
                        symbol = WildcardSymbol;
                    }
                    else if (length == 7 && Strncmp(stream.Source.AsSpan((int)nodeName, 7), "MISSING"u8, 7) == 0)
                    {
                        isMissing = true;
                        StreamSkipWhitespace(ref stream);

                        if (StreamIsIdentStart(ref stream))
                        {
                            uint missingNodeName = stream.Position;
                            StreamScanIdentifier(ref stream);
                            uint missingNodeLength = stream.Position - missingNodeName;
                            symbol = SymbolForName(stream.Source.AsSpan((int)missingNodeName, (int)missingNodeLength), isNamed: true);
                            if (symbol == 0)
                            {
                                StreamReset(ref stream, missingNodeName);
                                return QueryError.NodeType;
                            }
                        }
                        else if (stream.Next == '"')
                        {
                            uint stringStart = stream.Position;
                            QueryError e = ParseStringLiteral(ref stream);
                            if (e != QueryError.None)
                            {
                                return e;
                            }

                            symbol = SymbolForName(_stringBuffer.AsSpan(), isNamed: false);
                            if (symbol == 0)
                            {
                                StreamReset(ref stream, stringStart + 1);
                                return QueryError.NodeType;
                            }
                        }
                        else if (stream.Next == ')')
                        {
                            symbol = WildcardSymbol;
                        }
                        else
                        {
                            StreamReset(ref stream, stream.Position);
                            return QueryError.Syntax;
                        }
                    }
                    else
                    {
                        symbol = SymbolForName(stream.Source.AsSpan((int)nodeName, (int)length), isNamed: true);
                        if (symbol == 0)
                        {
                            StreamReset(ref stream, nodeName);
                            return QueryError.NodeType;
                        }
                    }
                }
                else
                {
                    return QueryError.Syntax;
                }

                // Add a step for the node.
                _steps.Push(QueryStepNew(symbol, (ushort)depth, isImmediate));
                ref QueryStep step = ref _steps.Back();
                if (_language.GetSymbolMetadata(symbol).Supertype)
                {
                    step.SupertypeSymbol = step.Symbol;
                    step.Symbol = WildcardSymbol;
                }

                if (isMissing)
                {
                    step.IsMissing = true;
                }

                if (symbol == WildcardSymbol)
                {
                    step.IsNamed = true;
                }

                // Parse a supertype symbol
                if (stream.Next == '/')
                {
                    if (step.SupertypeSymbol == 0)
                    {
                        StreamReset(ref stream, nodeName - 1); // reset to the start of the node
                        return QueryError.Structure;
                    }

                    StreamAdvance(ref stream);

                    uint subtypeNodeName = stream.Position;

                    if (StreamIsIdentStart(ref stream))
                    {
                        // Named node
                        StreamScanIdentifier(ref stream);
                        uint length = stream.Position - subtypeNodeName;
                        step.Symbol = SymbolForName(stream.Source.AsSpan((int)subtypeNodeName, (int)length), isNamed: true);
                    }
                    else if (stream.Next == '"')
                    {
                        // Anonymous leaf node
                        QueryError e = ParseStringLiteral(ref stream);
                        if (e != QueryError.None)
                        {
                            return e;
                        }

                        step.Symbol = SymbolForName(_stringBuffer.AsSpan(), isNamed: false);
                    }
                    else
                    {
                        return QueryError.Syntax;
                    }

                    if (step.Symbol == 0)
                    {
                        StreamReset(ref stream, subtypeNodeName);
                        return QueryError.NodeType;
                    }

                    // Get all the possible subtypes for the given supertype,
                    // and check if the given subtype is valid.
                    if (_language.AbiVersion >= LanguageVersionWithReservedWords)
                    {
                        ReadOnlySpan<ushort> subtypes = _language.Subtypes(step.SupertypeSymbol);

                        bool subtypeIsValid = false;
                        for (uint i = 0; i < (uint)subtypes.Length; i++)
                        {
                            if (subtypes[(int)i] == step.Symbol)
                            {
                                subtypeIsValid = true;
                                break;
                            }
                        }

                        // This subtype is not valid for the given supertype.
                        if (!subtypeIsValid)
                        {
                            StreamReset(ref stream, nodeName - 1); // reset to the start of the node
                            return QueryError.Structure;
                        }
                    }
                }

                StreamSkipWhitespace(ref stream);

                // Parse the child patterns
                bool childIsImmediate = false;
                ushort lastChildStepIndex = 0;
                ushort negatedFieldCount = 0;
                ushort[] negatedFieldIds = new ushort[MaxNegatedFieldCount];
                CaptureQuantifiers childCaptureQuantifiers = CaptureQuantifiersNew();
                for (; ; )
                {
                    // Parse a negated field assertion
                    if (stream.Next == '!')
                    {
                        StreamAdvance(ref stream);
                        StreamSkipWhitespace(ref stream);
                        if (!StreamIsIdentStart(ref stream))
                        {
                            return QueryError.Syntax;
                        }

                        uint fieldName = stream.Position;
                        StreamScanIdentifier(ref stream);
                        uint length = stream.Position - fieldName;
                        StreamSkipWhitespace(ref stream);

                        ushort fieldId = _language.FieldIdForName(Encoding.UTF8.GetString(stream.Source.AsSpan((int)fieldName, (int)length)));
                        if (fieldId == 0)
                        {
                            stream.Position = fieldName;
                            return QueryError.Field;
                        }

                        // Keep the field ids sorted.
                        if (negatedFieldCount < MaxNegatedFieldCount)
                        {
                            negatedFieldIds[negatedFieldCount] = fieldId;
                            negatedFieldCount++;
                        }

                        continue;
                    }

                    // Parse a sibling anchor
                    if (stream.Next == '.')
                    {
                        childIsImmediate = true;
                        StreamAdvance(ref stream);
                        StreamSkipWhitespace(ref stream);
                    }

                    ushort stepIndex = (ushort)_steps.Size;
                    QueryError e = ParsePattern(
                        ref stream,
                        depth + 1,
                        childIsImmediate,
                        isInsideAlternation,
                        ref childCaptureQuantifiers);

                    // In the event we only parsed a predicate, meaning no new steps were added,
                    // then subtract one so we're not indexing past the end of the array
                    if ((uint)stepIndex == _steps.Size)
                    {
                        stepIndex--;
                    }

                    if (e == ParentDone)
                    {
                        if (stream.Next == ')')
                        {
                            if (childIsImmediate)
                            {
                                if (lastChildStepIndex == 0)
                                {
                                    return QueryError.Syntax;
                                }

                                // Mark this step *and* its alternatives as the last child of the parent.
                                _steps.Get(lastChildStepIndex).IsLastChild = true;
                                if (
                                    _steps.Get(lastChildStepIndex).AlternativeIndex != None &&
                                    _steps.Get(lastChildStepIndex).AlternativeIndex < _steps.Size
                                )
                                {
                                    uint alternativeIndex = _steps.Get(lastChildStepIndex).AlternativeIndex;
                                    _steps.Get(alternativeIndex).IsLastChild = true;
                                    while (
                                        _steps.Get(alternativeIndex).AlternativeIndex != None &&
                                        _steps.Get(alternativeIndex).AlternativeIndex < _steps.Size
                                    )
                                    {
                                        alternativeIndex = _steps.Get(alternativeIndex).AlternativeIndex;
                                        _steps.Get(alternativeIndex).IsLastChild = true;
                                    }
                                }
                            }

                            if (negatedFieldCount != 0)
                            {
                                AddNegatedFields(
                                    (ushort)startingStepIndex,
                                    negatedFieldIds,
                                    negatedFieldCount);
                            }

                            StreamAdvance(ref stream);
                            break;
                        }

                        e = QueryError.Syntax;
                    }

                    if (e != QueryError.None)
                    {
                        return e;
                    }

                    CaptureQuantifiersAddAll(ref captureQuantifiers, ref childCaptureQuantifiers);

                    lastChildStepIndex = stepIndex;
                    childIsImmediate = false;
                    CaptureQuantifiersClear(ref childCaptureQuantifiers);
                }
            }
        }

        // Parse a wildcard pattern
        else if (stream.Next == '_')
        {
            StreamAdvance(ref stream);
            StreamSkipWhitespace(ref stream);

            // Add a step that matches any kind of node
            _steps.Push(QueryStepNew(WildcardSymbol, (ushort)depth, isImmediate));
        }

        // Parse a double-quoted anonymous leaf node expression
        else if (stream.Next == '"')
        {
            uint stringStart = stream.Position;
            QueryError e = ParseStringLiteral(ref stream);
            if (e != QueryError.None)
            {
                return e;
            }

            // Add a step for the node
            ushort symbol = SymbolForName(_stringBuffer.AsSpan(), isNamed: false);
            if (symbol == 0)
            {
                StreamReset(ref stream, stringStart + 1);
                return QueryError.NodeType;
            }

            _steps.Push(QueryStepNew(symbol, (ushort)depth, isImmediate));
        }

        // Parse a field-prefixed pattern
        else if (StreamIsIdentStart(ref stream))
        {
            // Parse the field name
            uint fieldName = stream.Position;
            StreamScanIdentifier(ref stream);
            uint length = stream.Position - fieldName;
            StreamSkipWhitespace(ref stream);

            if (stream.Next != ':')
            {
                StreamReset(ref stream, fieldName);
                return QueryError.Syntax;
            }

            StreamAdvance(ref stream);
            StreamSkipWhitespace(ref stream);

            // Parse the pattern
            CaptureQuantifiers fieldCaptureQuantifiers = CaptureQuantifiersNew();
            QueryError e = ParsePattern(
                ref stream,
                depth,
                isImmediate,
                isInsideAlternation,
                ref fieldCaptureQuantifiers);
            if (e != QueryError.None)
            {
                if (e == ParentDone)
                {
                    e = QueryError.Syntax;
                }

                return e;
            }

            // Add the field name to the first step of the pattern
            ushort fieldId = _language.FieldIdForName(Encoding.UTF8.GetString(stream.Source.AsSpan((int)fieldName, (int)length)));
            if (fieldId == 0)
            {
                stream.Position = fieldName;
                return QueryError.Field;
            }

            uint stepIndex = startingStepIndex;
            ref QueryStep step = ref _steps.Get(stepIndex);
            for (; ; )
            {
                step.Field = fieldId;
                if (
                    step.AlternativeIndex != None &&
                    step.AlternativeIndex > stepIndex &&
                    step.AlternativeIndex < _steps.Size
                )
                {
                    stepIndex = step.AlternativeIndex;
                    step = ref _steps.Get(stepIndex);
                }
                else
                {
                    break;
                }
            }

            CaptureQuantifiersAddAll(ref captureQuantifiers, ref fieldCaptureQuantifiers);
        }
        else
        {
            return QueryError.Syntax;
        }

        StreamSkipWhitespace(ref stream);

        // Parse suffixes modifiers for this pattern
        Quantifier quantifier = Quantifier.One;
        for (; ; )
        {
            // Parse the one-or-more operator.
            if (stream.Next == '+')
            {
                quantifier = QuantifierJoin(Quantifier.OneOrMore, quantifier);

                StreamAdvance(ref stream);
                StreamSkipWhitespace(ref stream);
            }

            // Parse the zero-or-more repetition operator.
            else if (stream.Next == '*')
            {
                quantifier = QuantifierJoin(Quantifier.ZeroOrMore, quantifier);

                StreamAdvance(ref stream);
                StreamSkipWhitespace(ref stream);
            }

            // Parse the optional operator.
            else if (stream.Next == '?')
            {
                quantifier = QuantifierJoin(Quantifier.ZeroOrOne, quantifier);

                StreamAdvance(ref stream);
                StreamSkipWhitespace(ref stream);
            }

            // Parse an '@'-prefixed capture pattern
            else if (stream.Next == '@')
            {
                StreamAdvance(ref stream);
                if (!StreamIsIdentStart(ref stream))
                {
                    return QueryError.Syntax;
                }

                uint captureName = stream.Position;
                StreamScanIdentifier(ref stream);
                uint length = stream.Position - captureName;
                StreamSkipWhitespace(ref stream);

                // Add the capture id to the first step of the pattern
                ushort captureId = SymbolTableInsertName(ref _captures, stream.Source.AsSpan((int)captureName, (int)length));

                // Add the capture quantifier
                CaptureQuantifiersAddForId(ref captureQuantifiers, captureId, Quantifier.One);

                uint stepIndex = startingStepIndex;
                for (; ; )
                {
                    ref QueryStep step = ref _steps.Get(stepIndex);
                    QueryStepAddCapture(ref step, captureId);
                    if (
                        step.AlternativeIndex != None &&
                        step.AlternativeIndex > stepIndex &&
                        step.AlternativeIndex < _steps.Size
                    )
                    {
                        stepIndex = step.AlternativeIndex;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // No more suffix modifiers
            else
            {
                break;
            }
        }

        switch (quantifier)
        {
            case Quantifier.OneOrMore:
                {
                    QueryStep repeatStep = QueryStepNew(WildcardSymbol, (ushort)depth, isImmediate: false);
                    repeatStep.IsInsideAlternation = isInsideAlternation;
                    repeatStep.AlternativeIndex = (ushort)startingStepIndex;
                    repeatStep.IsPassThrough = true;
                    _steps.Push(repeatStep);
                }

                break;
            case Quantifier.ZeroOrMore:
                {
                    QueryStep repeatStep = QueryStepNew(WildcardSymbol, (ushort)depth, isImmediate: false);
                    repeatStep.IsInsideAlternation = isInsideAlternation;
                    repeatStep.AlternativeIndex = (ushort)startingStepIndex;
                    repeatStep.IsPassThrough = true;
                    _steps.Push(repeatStep);

                    // Stop when `step->alternative_index` is `NONE` or it points to
                    // `repeat_step` or beyond. Note that having just been pushed,
                    // `repeat_step` occupies slot `self->steps.size - 1`.
                    ref QueryStep step = ref _steps.Get(startingStepIndex);
                    while (step.AlternativeIndex != None && step.AlternativeIndex < _steps.Size - 1)
                    {
                        step = ref _steps.Get(step.AlternativeIndex);
                    }

                    step.AlternativeIndex = (ushort)_steps.Size;
                    step.AlternativeIsSkip = true;
                }

                break;
            case Quantifier.ZeroOrOne:
                {
                    ref QueryStep step = ref _steps.Get(startingStepIndex);
                    while (step.AlternativeIndex != None && step.AlternativeIndex < _steps.Size)
                    {
                        step = ref _steps.Get(step.AlternativeIndex);
                    }

                    step.AlternativeIndex = (ushort)_steps.Size;
                    step.AlternativeIsSkip = true;
                }

                break;
            default:
                break;
        }

        CaptureQuantifiersMul(ref captureQuantifiers, quantifier);

        return QueryError.None;
    }

    // ts_language_symbol_for_name over a byte range of the source: the bytes are decoded
    // and compared through Language.SymbolForName, like the strncmp of the C code.
    private ushort SymbolForName(ReadOnlySpan<byte> name, bool isNamed)
    {
        return _language.SymbolForName(Encoding.UTF8.GetString(name), isNamed);
    }
}
