// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/query.c, the query cursor part
// (CaptureListPool, QueryState, the finished-state heap and everything from
// ts_query_cursor_new to the end of the file).
// Rules:
// - Same structs, fields, functions and logic as query.c, in the same order. Internal
//   structs keep the C names in PascalCase (QueryState, CaptureListPool, ...); arrays
//   are TsArray, as in the rest of the port. The internals of Query (steps, patterns,
//   pattern map, ...) are used directly, as the C code uses TSQuery.
// - TSTreeCursor inside TSQueryCursor is the TreeCursor of this port (internal API of
//   tree_cursor.h: GotoFirstChildInternal, GotoNextSiblingInternal, CurrentStatus, ...).
// - TSQueryMatch.captures points into the capture list pool in C and stays valid until
//   the next call; here QueryMatch gets a copy of the captures.
// - ts_query_cursor_new / ts_query_cursor_delete are the constructor and nothing.
// - ts_query_cursor_exec_with_options stores a reference to QueryCursorOptions: upstream
//   stores a pointer to the options and keeps it until the next exec; the port keeps the
//   reference.

using System.Diagnostics;
using static TreeSitter.QueryConstants;
using TreeSitter.Subtrees;

namespace TreeSitter;

/// <summary>QueryState: the state of an in-progress match of a particular pattern.</summary>
internal struct QueryState
{
    internal uint Id;

    internal uint CaptureListId;

    internal uint HeapInsertOrder;

    internal ushort StartDepth;

    internal ushort StepIndex;

    internal ushort PatternIndex;

    internal ushort ConsumedCaptureCount;

    internal bool SeekingImmediateMatch;

    internal bool HasInProgressAlternatives;

    internal bool Dead;

    internal bool NeedsParent;

    internal bool SkippedQuantifier;
}

/// <summary>CaptureListPool: a collection of lists of captures.</summary>
internal sealed class CaptureListPool
{
    /// <summary>Array(CaptureList).</summary>
    internal TsArray<TsArray<QueryCapture>> List;

    /// <summary>CaptureList empty_list.</summary>
    internal TsArray<QueryCapture> EmptyList;

    /// <summary>The maximum number of capture lists allowed (the match limit).</summary>
    internal uint MaxCaptureListCount = uint.MaxValue;

    /// <summary>Array(uint32_t) free_id_stack.</summary>
    internal TsArray<uint> FreeIdStack;

    // capture_list_pool_reset
    internal void Reset()
    {
        FreeIdStack.Clear();
        for (uint i = 0; i < List.Size; i++)
        {
            // This invalid size means that the list is not in use.
            ref TsArray<QueryCapture> list = ref List.Get(i);
            list.Size = QueryCursor.CaptureListNone;
            FreeIdStack.Push(i);
        }
    }

    // capture_list_pool_get (const access)
    internal ref TsArray<QueryCapture> Get(uint id)
    {
        if (id >= List.Size) return ref EmptyList;
        return ref List.Get(id);
    }

    // capture_list_pool_get_mut
    internal ref TsArray<QueryCapture> GetMut(uint id)
    {
        Debug.Assert(id < List.Size);
        return ref List.Get(id);
    }

    // capture_list_pool_is_empty
    internal bool IsEmpty => FreeIdStack.Size == 0 && List.Size >= MaxCaptureListCount;

    // capture_list_pool_acquire: returns the id of an acquired capture list, or
    // CaptureListNone when the maximum is reached.
    internal uint Acquire()
    {
        // First see if any already allocated capture list is currently unused.
        if (FreeIdStack.Size > 0)
        {
            uint id = FreeIdStack.Pop();
            List.Get(id).Clear();
            return id;
        }

        // Otherwise allocate and initialize a new capture list, as long as that
        // doesn't put us over the requested maximum.
        uint i = List.Size;
        if (i >= MaxCaptureListCount) return QueryCursor.CaptureListNone;
        List.Push(default);
        return i;
    }

    // capture_list_pool_release
    internal void Release(uint id)
    {
        if (id >= List.Size) return;
        ref TsArray<QueryCapture> list = ref List.Get(id);
        if (list.Size == QueryCursor.CaptureListNone) return; // Guard against releasing a list twice
        list.Size = QueryCursor.CaptureListNone;
        FreeIdStack.Push(id);
    }
}

/// <summary>QueryRange: start/end points and bytes of a range (TSRange).</summary>
internal struct QueryRange
{
    internal Point StartPoint;

    internal Point EndPoint;

    internal uint StartByte;

    internal uint EndByte;
}

/// <summary>A single capture: the node that was captured, and its index in the query's
/// list of captures (mirrors TSQueryCapture).</summary>
public readonly struct QueryCapture
{
    /// <summary>The captured node.</summary>
    public Node Node { get; }

    /// <summary>The index of the capture in the query's list of captures.</summary>
    public uint Index { get; }

    internal QueryCapture(Node node, uint index)
    {
        Node = node;
        Index = index;
    }
}

/// <summary>A query match: the pattern that matched and its captures (mirrors TSQueryMatch).
/// Unlike the C code, the captures are a copy owned by the match, so they stay valid
/// past the next call.</summary>
public readonly partial struct QueryMatch
{
    /// <summary>The id of the match.</summary>
    public uint Id { get; }

    /// <summary>The index of the pattern that matched.</summary>
    public uint PatternIndex { get; }

    /// <summary>The captures of the match.</summary>
    public QueryCapture[] Captures { get; }

    internal QueryMatch(uint id, uint patternIndex, QueryCapture[] captures)
    {
        Id = id;
        PatternIndex = patternIndex;
        Captures = captures;
    }
}

/// <summary>State passed to the progress callback of ts_query_cursor_exec_with_options.</summary>
public struct QueryCursorState
{
    /// <summary>The current byte offset within the document.</summary>
    public uint CurrentByteOffset { get; set; }
}

/// <summary>Options for ts_query_cursor_exec_with_options.</summary>
public class QueryCursorOptions
{
    /// <summary>
    /// Callback invoked periodically during execution. Returning true halts execution,
    /// as if the end of the tree had been reached; only finished matches are returned
    /// afterwards.
    /// </summary>
    public Func<QueryCursorState, bool>? ProgressCallback { get; set; }
}

/// <summary>A query cursor (TSQueryCursor): runs a query on a syntax tree. Not thread safe.</summary>
public sealed partial class QueryCursor
{
    // The cursor calls the progress callback once it has done this much work.
    private const uint WorkPerQueryCallbackCheck = 1000;

    private static readonly Point PointMax = new(uint.MaxValue, uint.MaxValue);

    private Query _query = null!;

    private readonly TreeCursor _cursor = new();

    private TsArray<QueryState> _states;

    private TsArray<QueryState> _finishedStates;

    // Tracks how much of finished_states is in heap order.
    private uint _finishedStatesHeapSize;

    private readonly CaptureListPool _captureListPool = new();

    private uint _depth;

    private uint _maxStartDepth = uint.MaxValue;

    private QueryRange _includedRange;

    private QueryRange _containingRange;

    private uint _nextStateId;

    private uint _nextFinishedStateId;

    // ts_query_cursor_exec_with_options keeps a reference to the options until the
    // next exec (like the C code keeps a pointer).
    private QueryCursorOptions? _queryOptions;

    private QueryCursorState _queryState;

    /// <summary>CAPTURE_LIST_NONE</summary>
    internal const uint CaptureListNone = uint.MaxValue;

    private uint _workCount;

    private bool _onVisibleNode;

    private bool _ascending;

    private bool _halted;

    private bool _didExceedMatchLimit;

    /// <summary>ts_query_cursor_new</summary>
    public QueryCursor()
    {
        _includedRange = new QueryRange { EndPoint = PointMax, EndByte = uint.MaxValue };
        _containingRange = new QueryRange { EndPoint = PointMax, EndByte = uint.MaxValue };
    }

    /// <summary>ts_query_cursor_exec</summary>
    public void Exec(Query query, Node node) => ExecCore(query, node, null);

    /// <summary>ts_query_cursor_exec_with_options</summary>
    public void Exec(Query query, Node node, QueryCursorOptions options) => ExecCore(query, node, options);

    /// <summary>ts_query_cursor_did_exceed_match_limit</summary>
    public bool DidExceedMatchLimit => _didExceedMatchLimit;

    /// <summary>ts_query_cursor_match_limit / ts_query_cursor_set_match_limit</summary>
    public uint MatchLimit
    {
        get => _captureListPool.MaxCaptureListCount;
        set => _captureListPool.MaxCaptureListCount = value;
    }

    /// <summary>ts_query_cursor_set_byte_range</summary>
    public bool SetByteRange(uint startByte, uint endByte)
    {
        if (endByte == 0) endByte = uint.MaxValue;
        if (startByte > endByte) return false;
        _includedRange = _includedRange with { StartByte = startByte, EndByte = endByte };
        return true;
    }

    /// <summary>ts_query_cursor_set_point_range</summary>
    public bool SetPointRange(Point startPoint, Point endPoint)
    {
        if (endPoint.Row == 0 && endPoint.Column == 0) endPoint = PointMax;
        if (Point.Gt(startPoint, endPoint)) return false;
        _includedRange = _includedRange with { StartPoint = startPoint, EndPoint = endPoint };
        return true;
    }

    /// <summary>ts_query_cursor_set_containing_byte_range</summary>
    public bool SetContainingByteRange(uint startByte, uint endByte)
    {
        if (endByte == 0) endByte = uint.MaxValue;
        if (startByte > endByte) return false;
        _containingRange = _containingRange with { StartByte = startByte, EndByte = endByte };
        return true;
    }

    /// <summary>ts_query_cursor_set_containing_point_range</summary>
    public bool SetContainingPointRange(Point startPoint, Point endPoint)
    {
        if (endPoint.Row == 0 && endPoint.Column == 0) endPoint = PointMax;
        if (Point.Gt(startPoint, endPoint)) return false;
        _containingRange = _containingRange with { StartPoint = startPoint, EndPoint = endPoint };
        return true;
    }

    /// <summary>ts_query_cursor_set_max_start_depth</summary>
    public void SetMaxStartDepth(uint maxStartDepth) => _maxStartDepth = maxStartDepth;

    // ts_query_cursor_exec
    private void ExecCore(Query query, Node node, QueryCursorOptions? options)
    {
        _states.Clear();
        _finishedStates.Clear();
        _finishedStatesHeapSize = 0;
        _cursor.Reset(node);
        _captureListPool.Reset();
        _onVisibleNode = true;
        _nextStateId = 0;
        _nextFinishedStateId = 0;
        _depth = 0;
        _ascending = false;
        _halted = false;
        _query = query;
        _didExceedMatchLimit = false;
        _workCount = 0;
        _queryOptions = options;
        _queryState.CurrentByteOffset = 0;
    }

    /// <summary>ts_query_cursor_next_match</summary>
    public bool NextMatch(out QueryMatch match)
    {
        match = default;
        if (_finishedStates.Size == 0)
        {
            if (!Advance(false)) return false;
        }

        HeapifyFinishedStates();

        uint stateIndex = 0;
        if (_finishedStatesHeapSize > 0)
        {
            for (uint i = 1; i < _finishedStates.Size; i++)
            {
                QueryState candidate = _finishedStates.Get(i);
                QueryState earliest = _finishedStates.Get(stateIndex);
                if (candidate.HeapInsertOrder < earliest.HeapInsertOrder) stateIndex = i;
            }
        }

        ref QueryState state = ref _finishedStates.Get(stateIndex);
        if (state.Id == uint.MaxValue) state.Id = _nextStateId++;
        match = new QueryMatch(state.Id, state.PatternIndex, CaptureArray(state));

        _captureListPool.Release(state.CaptureListId);

        if (_finishedStatesHeapSize > 0)
        {
            FinishedStateErase(ref _finishedStates, stateIndex, _captureListPool);
            _finishedStatesHeapSize = _finishedStates.Size;
        }
        else
        {
            _finishedStates.Erase(stateIndex);
        }

        return true;
    }

    /// <summary>ts_query_cursor_remove_match</summary>
    public void RemoveMatch(uint matchId)
    {
        HeapifyFinishedStates();

        for (uint i = 0; i < _finishedStates.Size; i++)
        {
            ref QueryState state = ref _finishedStates.Get(i);
            if (state.Id == matchId)
            {
                _captureListPool.Release(state.CaptureListId);
                if (_finishedStatesHeapSize > 0)
                {
                    FinishedStateErase(ref _finishedStates, i, _captureListPool);
                    _finishedStatesHeapSize = _finishedStates.Size;
                }
                else
                {
                    _finishedStates.Erase(i);
                }

                return;
            }
        }

        // Remove unfinished query states as well to prevent future
        // captures for a match being removed.
        for (uint i = 0; i < _states.Size; i++)
        {
            ref QueryState state = ref _states.Get(i);
            if (state.Id == matchId)
            {
                _captureListPool.Release(state.CaptureListId);
                _states.Erase(i);
                return;
            }
        }
    }

    /// <summary>ts_query_cursor_next_capture</summary>
    public bool NextCapture(out QueryMatch match, out uint captureIndex)
    {
        match = default;
        captureIndex = 0;

        for (;;)
        {
            // Sift any newly pushed finished states into the heap.
            HeapifyFinishedStates();

            // First, find the earliest capture in an unfinished match.
            bool foundUnfinishedState = FirstInProgressCapture(
                out uint firstUnfinishedStateIndex,
                out uint firstUnfinishedCaptureByte,
                out uint firstUnfinishedPatternIndex,
                true,
                out bool firstUnfinishedStateIsDefinite);

            // Then find the earliest capture in a finished match. The finished_states
            // array is maintained as a min-heap, so the earliest is always at index 0.
            // Clean up fully-consumed and out-of-range states from the heap root first.
            uint firstFinishedStateIndex = uint.MaxValue;
            uint firstFinishedCaptureByte = firstUnfinishedCaptureByte;
            uint firstFinishedPatternIndex = firstUnfinishedPatternIndex;

            while (_finishedStates.Size > 0)
            {
                ref QueryState state = ref _finishedStates.Get(0);
                ref TsArray<QueryCapture> captures = ref _captureListPool.Get(state.CaptureListId);

                // Remove states whose captures are all consumed.
                if (state.ConsumedCaptureCount >= captures.Size)
                {
                    _captureListPool.Release(state.CaptureListId);
                    FinishedStatePop(ref _finishedStates, _captureListPool);
                    _finishedStatesHeapSize = _finishedStates.Size;
                    continue;
                }

                Node node = captures.Get(state.ConsumedCaptureCount).Node;

                bool nodePrecedesRange =
                    NodeEndByte(node) <= _includedRange.StartByte ||
                    Point.Lte(NodeEndPoint(node), _includedRange.StartPoint);
                bool nodeFollowsRange =
                    NodeStartByte(node) >= _includedRange.EndByte ||
                    Point.Gte(NodeStartPoint(node), _includedRange.EndPoint);
                bool nodeOutsideOfRange = nodePrecedesRange || nodeFollowsRange;

                // Skip captures that are outside of the cursor's range.
                if (nodeOutsideOfRange)
                {
                    state.ConsumedCaptureCount++;
                    FinishedStateSiftDown(_finishedStates, 0, _captureListPool);
                    continue;
                }

                uint nodeStartByte = NodeStartByte(node);
                if (
                    nodeStartByte < firstFinishedCaptureByte ||
                    (nodeStartByte == firstFinishedCaptureByte &&
                     state.PatternIndex < firstFinishedPatternIndex)
                )
                {
                    firstFinishedStateIndex = 0;
                    firstFinishedCaptureByte = nodeStartByte;
                    firstFinishedPatternIndex = state.PatternIndex;
                }

                break;
            }

            bool captureReady = false;
            bool fromFinished = false;
            uint captureStateIndex = 0;

            if (firstFinishedStateIndex != uint.MaxValue)
            {
                captureReady = true;
                fromFinished = true;
                captureStateIndex = firstFinishedStateIndex;
            }
            else if (firstUnfinishedStateIsDefinite)
            {
                captureReady = true;
                captureStateIndex = firstUnfinishedStateIndex;
            }

            if (captureReady)
            {
                // Mirrors C: state is either from _finishedStates (fromFinished) or _states.
                // Do not read _states[captureStateIndex] when fromFinished - _states may be empty.
                ref QueryState state = ref (fromFinished
                    ? ref _finishedStates.Get(captureStateIndex)
                    : ref _states.Get(captureStateIndex));
                if (state.Id == uint.MaxValue) state.Id = _nextStateId++;
                match = new QueryMatch(state.Id, state.PatternIndex, CaptureArray(state));
                captureIndex = state.ConsumedCaptureCount;
                state.ConsumedCaptureCount++;

                // If this state is in the finished_states heap, its sort key has changed
                // (the next capture is now later in the document). Restore heap order.
                if (fromFinished)
                {
                    FinishedStateSiftDown(_finishedStates, 0, _captureListPool);
                }

                return true;
            }

            if (_captureListPool.IsEmpty && foundUnfinishedState)
            {
                _captureListPool.Release(_states.Get(firstUnfinishedStateIndex).CaptureListId);
                _states.Erase(firstUnfinishedStateIndex);
            }

            // If there are no finished matches that are ready to be returned, then
            // continue finding more matches.
            if (!Advance(true) && _finishedStates.Size == 0) return false;
        }
    }

    /// <summary>ts_query_cursor_set_max_start_depth is above; this is internal state access below.</summary>

    // Walk the tree, processing patterns until at least one pattern finishes.
    // If one or more patterns finish, return `true` and store their states in the
    // `finished_states` array. If there are no more matches, return `false`.
    private bool Advance(bool stopOnDefiniteStep)
    {
        bool didMatch = false;
        for (;;)
        {
            if (_halted)
            {
                while (_states.Size > 0)
                {
                    QueryState state = _states.Pop();
                    _captureListPool.Release(state.CaptureListId);
                }
            }

            if (didMatch || _halted) return didMatch;

            // Consult the progress callback after a bounded amount of work.
            // Only iterations that do work are charged.
            _workCount += 1u + _states.Size;
            if (_workCount >= WorkPerQueryCallbackCheck)
            {
                _workCount = 0;
                QueryCursorOptions? options = _queryOptions;
                if (options is not null)
                {
                    Func<QueryCursorState, bool>? callback = options.ProgressCallback;
                    if (callback is not null)
                    {
                        _queryState.CurrentByteOffset = _cursor.CurrentNode.StartByte;
                        if (callback(_queryState))
                        {
                            // Halt the same way reaching the end of the tree does. The next
                            // iteration discards the in-progress states, so only finished
                            // matches are returned.
                            _halted = true;
                            continue;
                        }
                    }
                }
            }

            // Exit the current node.
            if (_ascending)
            {
                if (_onVisibleNode)
                {
                    // After leaving a node, remove any states that cannot make further progress.
                    uint deletedCount = 0;
                    uint n = _states.Size;
                    for (uint i = 0; i < n; i++)
                    {
                        ref QueryState state = ref _states.Get(i);
                        ref QueryStep step = ref _query.StepRef(state.StepIndex);

                        // If a state completed its pattern inside of this node, but was deferred
                        // from finishing in order to search for longer matches, mark it as finished.
                        if (step.Depth == PatternDoneMarker &&
                            (state.StartDepth > _depth || _depth == 0))
                        {
                            PushFinishedState(ref state);
                            didMatch = true;
                            deletedCount++;
                        }

                        // If a state needed to match something within this node, then remove that
                        // state as it has failed to match.
                        else if (
                            step.Depth != PatternDoneMarker &&
                            (uint)state.StartDepth + (uint)step.Depth > _depth)
                        {
                            _captureListPool.Release(state.CaptureListId);
                            deletedCount++;
                        }

                        else if (deletedCount > 0)
                        {
                            _states.Get(i - deletedCount) = state;
                        }
                    }
                    _states.Size -= deletedCount;
                }

                // Leave this node by stepping to its next sibling or to its parent.
                switch (_cursor.GotoNextSiblingInternal())
                {
                    case TreeCursorStep.Visible:
                        if (!_onVisibleNode)
                        {
                            _depth++;
                            _onVisibleNode = true;
                        }

                        _ascending = false;
                        break;

                    case TreeCursorStep.Hidden:
                        if (_onVisibleNode)
                        {
                            _depth--;
                            _onVisibleNode = false;
                        }

                        _ascending = false;
                        break;

                    default:
                        if (_cursor.GotoParent()) _depth--;
                        else _halted = true;
                        break;
                }
            }

            // Enter a new node.
            else
            {
                Node node = _cursor.CurrentNode;
                Node parentNode = _cursor.ParentNode();

                bool parentIntersectsRange =
                    parentNode.IsNull ||
                    RangeIntersects(Range(parentNode), _includedRange);
                QueryRange nodeRange = Range(node);
                bool nodeIntersectsRange =
                    parentIntersectsRange && RangeIntersects(nodeRange, _includedRange);
                bool nodeIntersectsContainingRange = RangeIntersects(nodeRange, _containingRange);
                bool nodeWithinContainingRange = RangeWithin(nodeRange, _containingRange);

                if (nodeWithinContainingRange && _onVisibleNode)
                {
                    ushort fieldId = 0;
                    bool hasLaterSiblings;
                    bool hasLaterNamedSiblings;
                    bool canHaveLaterSiblingsWithThisField;
                    ushort[] supertypes = new ushort[MaxNegatedFieldCount];
                    uint supertypeCount = MaxNegatedFieldCount;
                    _cursor.CurrentStatus(
                        out fieldId,
                        out hasLaterSiblings,
                        out hasLaterNamedSiblings,
                        out canHaveLaterSiblingsWithThisField,
                        supertypes,
                        out supertypeCount);

                    bool nodeIsError = node.Symbol == (ushort)BuiltinSymbolError;
                    bool parentIsError =
                        !parentNode.IsNull && parentNode.Symbol == (ushort)BuiltinSymbolError;

                    // Add new states for any patterns whose root node is a wildcard.
                    if (!nodeIsError)
                    {
                        for (uint patternIndex = 0; patternIndex < _query.WildcardRootPatternCount; patternIndex++)
                        {
                            PatternEntry pattern = _query.PatternMap[(int)patternIndex];
                            ref QueryStep step = ref _query.StepRef(pattern.StepIndex);
                            uint startDepth = _depth - step.Depth;
                            if (
                                (pattern.IsRooted ?
                                    nodeIntersectsRange :
                                    (parentIntersectsRange && !parentIsError)) &&
                                (step.Field == 0 || fieldId == step.Field) &&
                                (step.SupertypeSymbol == 0 || supertypeCount > 0) &&
                                startDepth <= _maxStartDepth)
                            {
                                AddState(pattern);
                            }
                        }
                    }

                    // Add new states for any patterns whose root node matches this node.
                    if (_query.PatternMapSearch(node.Symbol, out uint i))
                    {
                        for (; ; )
                        {
                            PatternEntry pattern = _query.PatternMap[(int)i];
                            ref QueryStep step = ref _query.StepRef(pattern.StepIndex);
                            uint startDepth = _depth - step.Depth;
                            if (
                                (pattern.IsRooted ?
                                    nodeIntersectsRange :
                                    (parentIntersectsRange && !parentIsError)) &&
                                (step.Field == 0 || fieldId == step.Field) &&
                                startDepth <= _maxStartDepth)
                            {
                                AddState(pattern);
                            }

                            // Advance to the next pattern whose root node matches this node.
                            i++;
                            if (i == _query.PatternMap.Length) break;
                            pattern = _query.PatternMap[(int)i];
                            step = ref _query.StepRef(pattern.StepIndex);
                            if (step.Symbol != node.Symbol) break;
                        }
                    }

                    // Update all of the in-progress states with the current node.
                    for (uint j = 0, copyCount = 0; j < _states.Size; j += 1u + copyCount)
                    {
                        ref QueryState state = ref _states.Get(j);
                        ref QueryStep step = ref _query.StepRef(state.StepIndex);
                        state.HasInProgressAlternatives = false;
                        copyCount = 0;

                        // Check that the node matches all of the criteria for the next
                        // step of the pattern.
                        if ((uint)state.StartDepth + (uint)step.Depth != _depth) continue;

                        // Determine if this node matches this step of the pattern, and also
                        // if this node can have later siblings that match this step of the pattern.
                        bool nodeDoesMatch;
                        if (step.Symbol == WildcardSymbol)
                        {
                            if (step.IsMissing) nodeDoesMatch = node.IsMissing;
                            else nodeDoesMatch = !nodeIsError && (node.IsNamed || !step.IsNamed);
                        }
                        else
                        {
                            nodeDoesMatch = node.Symbol == step.Symbol && (!step.IsMissing || node.IsMissing);
                        }

                        bool laterSiblingCanMatch = hasLaterSiblings;
                        if ((step.IsImmediate && node.IsNamed && !state.SkippedQuantifier) ||
                            state.SeekingImmediateMatch)
                        {
                            laterSiblingCanMatch = false;
                        }

                        if (step.IsLastChild && hasLaterNamedSiblings) nodeDoesMatch = false;

                        if (step.SupertypeSymbol != 0)
                        {
                            bool hasSupertype = false;
                            for (uint k = 0; k < supertypeCount; k++)
                            {
                                if (supertypes[k] == step.SupertypeSymbol)
                                {
                                    hasSupertype = true;
                                    break;
                                }
                            }

                            if (!hasSupertype) nodeDoesMatch = false;
                        }

                        if (step.Field != 0)
                        {
                            if (step.Field == fieldId)
                            {
                                if (!canHaveLaterSiblingsWithThisField) laterSiblingCanMatch = false;
                            }
                            else
                            {
                                nodeDoesMatch = false;
                            }
                        }

                        if (step.NegatedFieldListId != 0)
                        {
                            ReadOnlySpan<ushort> negatedFieldIds =
                                _query.NegatedFields.Slice((int)step.NegatedFieldListId);
                            foreach (ushort negatedFieldId in negatedFieldIds)
                            {
                                if (negatedFieldId == 0) break;
                                if (!node.ChildByFieldId(negatedFieldId).IsNull)
                                {
                                    nodeDoesMatch = false;
                                    break;
                                }
                            }
                        }

                        // Remove states immediately if it is ever clear that they cannot match.
                        if (!nodeDoesMatch)
                        {
                            if (!laterSiblingCanMatch)
                            {
                                _captureListPool.Release(state.CaptureListId);
                                _states.Erase(j);
                                j--;
                            }

                            continue;
                        }

                        // Some patterns can match their root node in multiple ways, capturing
                        // different children. If this pattern step could match later children
                        // within the same parent, then this query state cannot simply be updated
                        // in place. It must be split into two states: one that matches this node,
                        // and one which skips over this node.
                        // `contains_captures` is the flag of the C's QueryStep: it is true when
                        // the step itself or one of its child steps has captures (query.c:1683).
                        if (laterSiblingCanMatch &&
                            (step.ContainsCaptures || _query.StepIsFallible(state.StepIndex)))
                        {
                            if (CopyState(j) >= 0)
                            {
                                copyCount++;
                                // CopyState may have reallocated the states array.
                                state = ref _states.Get(j);
                            }
                        }

                        // If the pattern started with a wildcard, such that the pattern map
                        // actually points to the *second* step of the pattern, then check
                        // that the node has a parent, and capture the parent node if necessary.
                        if (state.NeedsParent)
                        {
                            Node parent = _cursor.ParentNode();
                            if (parent.IsNull)
                            {
                                state.Dead = true;
                            }
                            else
                            {
                                state.NeedsParent = false;

                                uint skippedWildcardStepIndex = state.StepIndex;
                                for (; ; )
                                {
                                    if (skippedWildcardStepIndex == 0) break;
                                    skippedWildcardStepIndex--;
                                    ref QueryStep skippedWildcardStep = ref _query.StepRef(skippedWildcardStepIndex);
                                    if (
                                        !skippedWildcardStep.IsDeadEnd &&
                                        !skippedWildcardStep.IsPassThrough &&
                                        skippedWildcardStep.Depth == 0)
                                    {
                                        if (skippedWildcardStep.CaptureIds[0] != None)
                                        {
                                            Capture(ref state, skippedWildcardStep, parent);
                                        }
                                        break;
                                    }
                                }
                            }
                        }

                        // If the current node is captured in this pattern, add it to the capture list.
                        if (step.CaptureIds[0] != None)
                        {
                            Capture(ref state, step, node);
                        }

                        if (state.Dead)
                        {
                            _captureListPool.Release(state.CaptureListId);
                            _states.Erase(j);
                            j--;
                            continue;
                        }

                        // Advance this state to the next step of its pattern.
                        state.StepIndex++;
                        ref QueryStep nextStep = ref _query.StepRef(state.StepIndex);

                        // For a given step, if the current symbol is the wildcard symbol, `_`, and it
                        // is not named, and the next step is immediate, we reuse the
                        // seeking_immediate_match flag to indicate that we are looking for an
                        // immediate match due to an unnamed wildcard symbol.
                        if (step.Symbol == WildcardSymbol && !step.IsNamed && nextStep.IsImmediate)
                        {
                            state.SeekingImmediateMatch = true;
                        }
                        else
                        {
                            state.SeekingImmediateMatch = false;
                        }

                        // The zero-skip's vacuous-anchor exemption only covers the immediate
                        // step it lands on. Once the state advances, a later anchor is normal.
                        state.SkippedQuantifier = false;

                        if (stopOnDefiniteStep && nextStep.RootPatternGuaranteed) didMatch = true;

                        // If this state's next step has an alternative step, then copy the state
                        // in order to pursue both alternatives.
                        uint endIndex = j + 1;
                        for (uint k = j; k < endIndex; k++)
                        {
                            ref QueryState childState = ref _states.Get(k);
                            ref QueryStep childStep = ref _query.StepRef(childState.StepIndex);
                            // Preserve the original state index before any k-- below.
                            // CopyState must copy the state at this index, mirroring C's
                            // ts_query_cursor__copy_state(self, &child_state), where
                            // child_state still points to states[original_k] after k--.
                            uint copyStateIndex = k;
                            if (childStep.AlternativeIndex != None)
                            {
                                // A "dead-end" step exists only to add a non-sequential jump into
                                // the step sequence, via its alternative index.
                                if (childStep.IsDeadEnd)
                                {
                                    childState.StepIndex = childStep.AlternativeIndex;
                                    k--;
                                    continue;
                                }

                                // A "pass-through" step splits in order to process the
                                // alternative step, and then advances to the next step.
                                if (childStep.IsPassThrough)
                                {
                                    childState.StepIndex++;
                                    k--;
                                }

                                // A `?`/`*` zero-skip past a step that carries a trailing
                                // last-child anchor transfers that requirement to the last
                                // matched node. The skip is only valid if that node really is
                                // the last named child.
                                if (
                                    childStep.AlternativeIsSkip &&
                                    childStep.IsLastChild &&
                                    hasLaterNamedSiblings)
                                {
                                    continue;
                                }

                                uint childStepIndex = childState.StepIndex;
                                if (CopyState(copyStateIndex) >= 0)
                                {
                                    endIndex++;
                                    copyCount++;
                                    ref QueryState copy = ref _states.Get((uint)(copyStateIndex + 1));
                                    copy.StepIndex = childStep.AlternativeIndex;

                                    if (childStep.IsPassThrough)
                                    {
                                        copy.SeekingImmediateMatch = true;
                                    }

                                    if (childStep.AlternativeIsSkip)
                                    {
                                        if (!childStep.IsImmediate)
                                        {
                                            ref QueryStep skipTarget = ref _query.StepRef(childStep.AlternativeIndex);
                                            // No leading anchor on the skipped step, so an
                                            // immediately-following anchor on the skip target
                                            // is vacuous.
                                            copy.SkippedQuantifier = skipTarget.Depth == childStep.Depth;
                                        }
                                        else if (
                                            childStepIndex > 0 &&
                                            _query.StepRef(childStepIndex - 1).Depth < childStep.Depth)
                                        {
                                            // The skipped step was the parent's first child
                                            // pattern and carried a leading boundary anchor.
                                            copy.SeekingImmediateMatch = true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Order states by capture position so the dedup pass below can stop
                    // scanning a group once the remaining states are disjoint from the current one.
                    SortStatesByCapture();

                    for (uint j = 0; j < _states.Size; j++)
                    {
                        ref QueryState state = ref _states.Get(j);
                        if (state.Dead)
                        {
                            _captureListPool.Release(state.CaptureListId);
                            _states.Erase(j);
                            j--;
                            continue;
                        }

                        // Enforce the longest-match criteria.
                        bool didRemove = false;
                        for (uint k = j + 1; k < _states.Size; k++)
                        {
                            ref QueryState otherState = ref _states.Get(k);

                            // Query states are kept in ascending order of start_depth and
                            // pattern_index, and in ascending order of first-capture position
                            // within each such group.
                            if (
                                otherState.StartDepth != state.StartDepth ||
                                otherState.PatternIndex != state.PatternIndex)
                            {
                                break;
                            }

                            // Once other_state's captures begin at or after where state's
                            // captures end, neither can be a capture-subset of the other.
                            ref TsArray<QueryCapture> stateCaptures =
                                ref _captureListPool.Get(state.CaptureListId);
                            ref TsArray<QueryCapture> otherCaptures =
                                ref _captureListPool.Get(otherState.CaptureListId);
                            if (
                                stateCaptures.Size > 0 &&
                                otherCaptures.Size > 0 &&
                                NodeStartByte(otherCaptures.Get(0).Node) >=
                                    NodeEndByte(stateCaptures.Get(stateCaptures.Size - 1).Node))
                            {
                                break;
                            }

                            CompareCaptures(
                                state,
                                otherState,
                                out bool leftContainsRight,
                                out bool rightContainsLeft);
                            if (leftContainsRight)
                            {
                                if (
                                    state.StepIndex == otherState.StepIndex &&
                                    (otherState.SeekingImmediateMatch || !state.SeekingImmediateMatch))
                                {
                                    _captureListPool.Release(otherState.CaptureListId);
                                    _states.Erase(k);
                                    k--;
                                    continue;
                                }

                                otherState.HasInProgressAlternatives = true;
                            }

                            if (rightContainsLeft)
                            {
                                if (
                                    state.StepIndex == otherState.StepIndex &&
                                    (state.SeekingImmediateMatch || !otherState.SeekingImmediateMatch))
                                {
                                    _captureListPool.Release(state.CaptureListId);
                                    _states.Erase(j);
                                    j--;
                                    didRemove = true;
                                    break;
                                }

                                state.HasInProgressAlternatives = true;
                            }
                        }

                        if (didRemove) continue;

                        // If the state is at the end of its pattern, remove it from the list
                        // of in-progress states and add it to the list of finished states.
                        ref QueryStep nextStep = ref _query.StepRef(state.StepIndex);
                        if (nextStep.Depth == PatternDoneMarker)
                        {
                            if (state.HasInProgressAlternatives)
                            {
                                // Defer finishing: search for a longer match first.
                            }
                            else
                            {
                                PushFinishedState(ref state);
                                _states.Erase(j);
                                didMatch = true;
                                j--;
                            }
                        }
                    }
                }

                if (nodeIntersectsContainingRange && ShouldDescend(nodeIntersectsRange))
                {
                    switch (_cursor.GotoFirstChildInternal())
                    {
                        case TreeCursorStep.Visible:
                            _depth++;
                            _onVisibleNode = true;
                            continue;

                        case TreeCursorStep.Hidden:
                            _onVisibleNode = false;
                            continue;

                        default:
                            break;
                    }
                }

                _ascending = true;
            }
        }
    }

    // Search through all of the in-progress states, and find the captured node that occurs
    // earliest in the document.
    //
    // `computeDefinite` mirrors the C's `bool *is_definite` argument of
    // ts_query_cursor__first_in_progress_capture (query.c:3611):
    // - true: the C passes a pointer, so the definiteness of the winning state is
    //   computed (`step->root_pattern_guaranteed && !step->is_immediate`).
    // - false: the C passes NULL, so a state whose next step is root_pattern_guaranteed
    //   is not a candidate at all: such a state is guaranteed to complete its pattern and
    //   must not be sacrificed when the capture list pool is exhausted (query.c:3665).
    private bool FirstInProgressCapture(
        out uint stateIndex,
        out uint byteOffset,
        out uint patternIndex,
        bool computeDefinite,
        out bool isDefinite)
    {
        bool result = false;
        stateIndex = uint.MaxValue;
        byteOffset = uint.MaxValue;
        patternIndex = uint.MaxValue;
        isDefinite = false;

        for (uint i = 0; i < _states.Size; i++)
        {
            ref QueryState state = ref _states.Get(i);
            if (state.Dead) continue;

            ref TsArray<QueryCapture> captures = ref _captureListPool.Get(state.CaptureListId);
            if (state.ConsumedCaptureCount >= captures.Size) continue;

            Node node = captures.Get(state.ConsumedCaptureCount).Node;
            if (
                NodeEndByte(node) <= _includedRange.StartByte ||
                Point.Lte(NodeEndPoint(node), _includedRange.StartPoint))
            {
                state.ConsumedCaptureCount++;
                i--;
                continue;
            }

            uint nodeStartByte = NodeStartByte(node);
            if (
                !result ||
                nodeStartByte < byteOffset ||
                (nodeStartByte == byteOffset && state.PatternIndex < patternIndex))
            {
                ref QueryStep step = ref _query.StepRef(state.StepIndex);
                if (computeDefinite)
                {
                    // We're being a bit conservative here by asserting that the following step
                    // is not immediate, because this capture might end up being discarded if the
                    // following symbol in the tree isn't the required symbol for this step.
                    isDefinite = step.RootPatternGuaranteed && !step.IsImmediate;
                }
                else if (step.RootPatternGuaranteed)
                {
                    continue;
                }
                result = true;
                stateIndex = i;
                byteOffset = nodeStartByte;
                patternIndex = state.PatternIndex;
            }
        }

        return result;
    }

    // Determine if either state contains a superset of the other state's captures.
    private void CompareCaptures(
        QueryState leftState,
        QueryState rightState,
        out bool leftContainsRight,
        out bool rightContainsLeft)
    {
        ref TsArray<QueryCapture> leftCaptures = ref _captureListPool.Get(leftState.CaptureListId);
        ref TsArray<QueryCapture> rightCaptures = ref _captureListPool.Get(rightState.CaptureListId);
        leftContainsRight = true;
        rightContainsLeft = true;
        uint i = 0;
        uint j = 0;

        for (;;)
        {
            if (i < leftCaptures.Size)
            {
                if (j < rightCaptures.Size)
                {
                    QueryCapture left = leftCaptures.Get(i);
                    QueryCapture right = rightCaptures.Get(j);
                    if (left.Node.IdEquals(right.Node) && left.Index == right.Index)
                    {
                        i++;
                        j++;
                    }
                    else
                    {
                        switch (CompareNodes(left.Node, right.Node))
                        {
                            case -1:
                                rightContainsLeft = false;
                                i++;
                                break;
                            case 1:
                                leftContainsRight = false;
                                j++;
                                break;
                            default:
                                rightContainsLeft = false;
                                leftContainsRight = false;
                                i++;
                                j++;
                                break;
                        }
                    }
                }
                else
                {
                    rightContainsLeft = false;
                    break;
                }
            }
            else
            {
                if (j < rightCaptures.Size) leftContainsRight = false;
                break;
            }
        }
    }

    // Order two in-progress states for the longest-match dedup pass.
    private bool StatePrecedes(QueryState a, QueryState b)
    {
        if (a.StartDepth != b.StartDepth) return a.StartDepth < b.StartDepth;
        if (a.PatternIndex != b.PatternIndex) return a.PatternIndex < b.PatternIndex;

        ref TsArray<QueryCapture> aCaps = ref _captureListPool.Get(a.CaptureListId);
        ref TsArray<QueryCapture> bCaps = ref _captureListPool.Get(b.CaptureListId);
        bool aEmpty = aCaps.Size == 0;
        bool bEmpty = bCaps.Size == 0;
        if (aEmpty != bEmpty) return aEmpty;
        if (aEmpty) return false;

        return NodeStartByte(aCaps.Get(0).Node) < NodeStartByte(bCaps.Get(0).Node);
    }

    // Stable-sort the in-progress states with the order dictated by StatePrecedes.
    // This runs once per node, right before the dedup pass.
    private void SortStatesByCapture()
    {
        TsArray<QueryState> states = _states;
        for (uint i = 1; i < states.Size; i++)
        {
            // Fast+common path: this state is already ordered after its predecessor.
            if (!StatePrecedes(states.Get(i), states.Get(i - 1))) continue;

            QueryState key = states.Get(i);
            uint j = i;
            do
            {
                states.Get(j) = states.Get(j - 1);
                j--;
            }
            while (j > 0 && StatePrecedes(key, states.Get(j - 1)));

            states.Get(j) = key;
        }
    }

    // Acquire a capture list for this state. If there are no capture lists left in the
    // pool, this will steal the capture list from another existing state, and mark that
    // other state as 'dead'. Returns false when the pool is exhausted and no state can
    // be sacrificed; the caller must then mark the state dead.
    private ref TsArray<QueryCapture> PrepareToCapture(
        ref QueryState state,
        uint stateIndexToPreserve,
        out bool success)
    {
        if (state.CaptureListId == CaptureListNone)
        {
            state.CaptureListId = _captureListPool.Acquire();

            // If there are no capture lists left in the pool, then terminate whichever
            // state has captured the earliest node in the document, and steal its
            // capture list.
            if (state.CaptureListId == CaptureListNone)
            {
                _didExceedMatchLimit = true;
                uint stateIndex, byteOffset, patternIndex;
                if (
                    FirstInProgressCapture(
                        out stateIndex,
                        out byteOffset,
                        out patternIndex,
                        false,
                        out _) &&
                    stateIndex != stateIndexToPreserve)
                {
                    ref QueryState otherState = ref _states.Get(stateIndex);
                    state.CaptureListId = otherState.CaptureListId;
                    otherState.CaptureListId = CaptureListNone;
                    otherState.Dead = true;
                    success = true;
                    ref TsArray<QueryCapture> stolen = ref _captureListPool.GetMut(state.CaptureListId);

                    // The stolen list still holds the captures of the state it was taken from:
                    // it must be emptied before the new owner appends its own captures
                    // (query.c:3898, array_clear).
                    stolen.Clear();
                    return ref stolen;
                }

                success = false;
                return ref _captureListPool.EmptyList;
            }
        }

        success = true;
        return ref _captureListPool.GetMut(state.CaptureListId);
    }

    private void Capture(ref QueryState state, QueryStep step, Node node)
    {
        if (state.Dead) return;

        bool acquired;
        ref TsArray<QueryCapture> captureList = ref PrepareToCapture(ref state, CaptureListNone, out acquired);
        if (!acquired)
        {
            state.Dead = true;
            return;
        }

        for (int j = 0; j < MaxStepCaptureCount; j++)
        {
            ushort captureId = step.CaptureIds[(int)j];
            if (captureId == None) break;
            captureList.Push(new QueryCapture(node, captureId));
        }
    }

    // Duplicate the given state and insert the newly-created state immediately after
    // the given state in the `states` array. Returns the index of the copy, or -1 when
    // the capture-list pool is exhausted.
    private int CopyState(uint stateIndex)
    {
        QueryState state = _states.Get(stateIndex);
        QueryState copy = state;
        copy.CaptureListId = QueryCursor.CaptureListNone;

        // If the state has captures, copy its capture list.
        if (state.CaptureListId != QueryCursor.CaptureListNone)
        {
            bool acquired;
            ref TsArray<QueryCapture> newCaptures = ref PrepareToCapture(ref copy, stateIndex, out acquired);
            if (!acquired) return -1;

            ref TsArray<QueryCapture> oldCaptures = ref _captureListPool.Get(state.CaptureListId);
            newCaptures.PushAll(oldCaptures);
        }

        _states.Insert(stateIndex + 1, copy);
        return (int)(stateIndex + 1);
    }

    // Determine if the cursor should descend into the current node's children.
    private bool ShouldDescend(bool nodeIntersectsRange)
    {
        if (nodeIntersectsRange && _depth < _maxStartDepth) return true;

        // If there are in-progress matches whose remaining steps occur deeper in the tree.
        for (uint i = 0; i < _states.Size; i++)
        {
            ref QueryState state = ref _states.Get(i);
            ref QueryStep nextStep = ref _query.StepRef(state.StepIndex);
            if (
                nextStep.Depth != PatternDoneMarker &&
                (uint)state.StartDepth + (uint)nextStep.Depth > _depth)
            {
                return true;
            }
        }

        if (_depth >= _maxStartDepth) return false;

        // If the current node is hidden, then a non-rooted pattern might match one of its
        // roots inside this node and another root as part of a sibling node.
        if (!_onVisibleNode)
        {
            Subtree subtree = _cursor.CurrentSubtree;
            if (subtree.IsRepetition)
            {
                // Avoid descending into repetition nodes unless we have already determined
                // that this query can match rootless patterns inside this repetition node.
                _query.RepeatSymbolsWithRootlessPatterns.SearchSortedBy(
                    static (ushort s) => (int)s,
                    (int)subtree.Symbol,
                    out _,
                    out bool exists);
                return exists;
            }

            return true;
        }

        return false;
    }

    // Push the state onto the finished states list, assigning it an id.
    private void PushFinishedState(ref QueryState state)
    {
        state.HeapInsertOrder = _nextFinishedStateId++;
        _finishedStates.Push(state);
    }

    private void HeapifyFinishedStates()
    {
        while (_finishedStatesHeapSize < _finishedStates.Size)
        {
            FinishedStateSiftUp(_finishedStates, _finishedStatesHeapSize, _captureListPool);
            _finishedStatesHeapSize++;
        }
    }

    // A copy of the state's captures: QueryMatch owns its captures, unlike the C code where
    // they point into the capture list pool and stay valid until the next call.
    private QueryCapture[] CaptureArray(QueryState state)
    {
        ref TsArray<QueryCapture> captures = ref _captureListPool.Get(state.CaptureListId);
        QueryCapture[] result = new QueryCapture[(int)captures.Size];
        for (uint i = 0; i < captures.Size; i++) result[(int)i] = captures.Get(i);
        return result;
    }

    // ------------------------------------------------------------------
    // Finished-state heap (min-heap ordered by capture byte, pattern index, insert order)

    private static void FinishedStateSwap(TsArray<QueryState> states, uint a, uint b)
    {
        QueryState tmp = states.Get(a);
        states.Get(a) = states.Get(b);
        states.Get(b) = tmp;
    }

    // Compare two finished states by (byte offset of next unconsumed capture,
    // pattern_index, insertion order).
    private static bool FinishedStatePrecedes(
        QueryState a,
        QueryState b,
        CaptureListPool pool)
    {
        ref TsArray<QueryCapture> aCaps = ref pool.Get(a.CaptureListId);
        ref TsArray<QueryCapture> bCaps = ref pool.Get(b.CaptureListId);
        if (a.ConsumedCaptureCount >= aCaps.Size) return false;
        if (b.ConsumedCaptureCount >= bCaps.Size) return true;
        uint aByte = aCaps.Get(a.ConsumedCaptureCount).Node.StartByte;
        uint bByte = bCaps.Get(b.ConsumedCaptureCount).Node.StartByte;
        if (aByte != bByte) return aByte < bByte;
        if (a.PatternIndex != b.PatternIndex) return a.PatternIndex < b.PatternIndex;
        return a.HeapInsertOrder < b.HeapInsertOrder;
    }

    private static void FinishedStateSiftDown(
        TsArray<QueryState> states,
        uint index,
        CaptureListPool pool)
    {
        uint size = states.Size;
        while (true)
        {
            uint smallest = index;
            uint left = 2u * index + 1u;
            uint right = 2u * index + 2u;
            if (left < size && FinishedStatePrecedes(states.Get(left), states.Get(smallest), pool))
            {
                smallest = left;
            }

            if (right < size && FinishedStatePrecedes(states.Get(right), states.Get(smallest), pool))
            {
                smallest = right;
            }

            if (smallest == index) break;
            FinishedStateSwap(states, index, smallest);
            index = smallest;
        }
    }

    private static void FinishedStateSiftUp(
        TsArray<QueryState> states,
        uint index,
        CaptureListPool pool)
    {
        while (index > 0)
        {
            uint parent = (index - 1u) / 2u;
            if (FinishedStatePrecedes(states.Get(index), states.Get(parent), pool))
            {
                FinishedStateSwap(states, index, parent);
                index = parent;
            }
            else
            {
                break;
            }
        }
    }

    private static void FinishedStatePop(ref TsArray<QueryState> states, CaptureListPool pool)
    {
        if (states.Size > 1) states.Get(0) = states.Get(states.Size - 1);
        states.Size--;
        if (states.Size > 0) FinishedStateSiftDown(states, 0, pool);
    }

    // Remove an element at an arbitrary index and restore heap order.
    private static void FinishedStateErase(
        ref TsArray<QueryState> states,
        uint index,
        CaptureListPool pool)
    {
        if (index == states.Size - 1)
        {
            states.Size--;
            return;
        }

        states.Get(index) = states.Get(states.Size - 1);
        states.Size--;
        // The replacement element may need to go up or down.
        if (index > 0 && FinishedStatePrecedes(states.Get(index), states.Get((index - 1u) / 2u), pool))
        {
            FinishedStateSiftUp(states, index, pool);
        }
        else
        {
            FinishedStateSiftDown(states, index, pool);
        }
    }

    // ------------------------------------------------------------------
    // Range helpers

    private static QueryRange Range(Node node) => new()
    {
        StartPoint = node.StartPoint,
        EndPoint = node.EndPoint,
        StartByte = node.StartByte,
        EndByte = node.EndByte,
    };

    private static bool RangeIntersects(QueryRange a, QueryRange b)
    {
        bool isEmpty = a.StartByte == a.EndByte;
        return (
            (a.EndByte > b.StartByte || (isEmpty && a.EndByte == b.StartByte)) &&
            (Point.Gt(a.EndPoint, b.StartPoint) || (isEmpty && a.EndPoint == b.StartPoint)) &&
            a.StartByte < b.EndByte &&
            Point.Lt(a.StartPoint, b.EndPoint)
        );
    }

    private static bool RangeWithin(QueryRange a, QueryRange b) =>
        a.StartByte >= b.StartByte &&
        Point.Gte(a.StartPoint, b.StartPoint) &&
        a.EndByte <= b.EndByte &&
        Point.Lte(a.EndPoint, b.EndPoint);

    // ------------------------------------------------------------------
    // Node helpers (ts_node_*)

    private static uint NodeStartByte(Node node) => node.StartByte;

    private static uint NodeEndByte(Node node) => node.EndByte;

    private static Point NodeStartPoint(Node node) => node.StartPoint;

    private static Point NodeEndPoint(Node node) => node.EndPoint;

    // Determine which node is first in a depth-first traversal (ts_query_cursor__compare_nodes).
    private static int CompareNodes(Node left, Node right)
    {
        if (!left.IdEquals(right))
        {
            uint leftStart = NodeStartByte(left);
            uint rightStart = NodeStartByte(right);
            if (leftStart < rightStart) return -1;
            if (leftStart > rightStart) return 1;
            uint leftNodeCount = NodeEndByte(left);
            uint rightNodeCount = NodeEndByte(right);
            if (leftNodeCount > rightNodeCount) return -1;
            if (leftNodeCount < rightNodeCount) return 1;
        }

        return 0;
    }

    // Add a new state for a pattern whose root node matches the current node.
    // ts_query_cursor__add_state
    private void AddState(PatternEntry pattern)
    {
        ref QueryStep step = ref _query.StepRef(pattern.StepIndex);
        uint startDepth = _depth - step.Depth;

        // Keep the states array in ascending order of start_depth and pattern_index.
        uint index = _states.Size;
        while (index > 0)
        {
            ref QueryState prevState = ref _states.Get(index - 1);
            if (prevState.StartDepth < startDepth) break;
            if (prevState.StartDepth == startDepth)
            {
                // Avoid inserting an unnecessary duplicate state.
                if (prevState.PatternIndex == pattern.PatternIndex &&
                    prevState.StepIndex == pattern.StepIndex)
                {
                    return;
                }

                if (prevState.PatternIndex <= pattern.PatternIndex) break;
            }

            index--;
        }

        _states.Insert(index, new QueryState
        {
            Id = uint.MaxValue,
            CaptureListId = CaptureListNone,
            HeapInsertOrder = uint.MaxValue,
            StepIndex = pattern.StepIndex,
            PatternIndex = pattern.PatternIndex,
            StartDepth = (ushort)startDepth,
            ConsumedCaptureCount = 0,
            SeekingImmediateMatch = true,
            HasInProgressAlternatives = false,
            NeedsParent = step.Depth == 1,
            Dead = false,
            SkippedQuantifier = false,
        });
    }
}
