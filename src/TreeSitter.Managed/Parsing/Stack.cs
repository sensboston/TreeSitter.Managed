// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/stack.c, lib/src/stack.h.
// Rules: docs/ARCHITECTURE.md D1. Each method has the C name above it and the same logic.
// - StackNode is a class; links hold Subtree values with the same ref counting as C
//   (SubtreeOps.Retain / Release at exactly the places C calls them).
// - The node pool (MAX_NODE_POOL_SIZE) is kept: freed nodes go back to the pool like in C, so the
//   order of node reuse matches. Node identity in the dot graph is a sequence number in order of
//   first appearance (the oracle normalizes the C pointers the same way).
// - Callbacks of stack__iter become instance methods that read the fields of the stack (the goal
//   count, the error flag, the summary being built): no closures and no payload objects.
// - C dereferences NULL_SUBTREE in one path of stack_node_add_link (two NULL links to the same
//   node). The oracle scripts never reach it: the random generator does not push NULL subtrees.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using TreeSitter.Subtrees;

namespace TreeSitter.Parsing;

/// <summary>StackSlice: the subtrees popped for one version.</summary>
internal struct StackSlice
{
    internal TsArray<Subtree> Subtrees;
    internal uint Version;
}

/// <summary>StackSummaryEntry.</summary>
internal readonly record struct StackSummaryEntry(Length Position, uint Depth, ushort State);

/// <summary>Stack: the graph-structured parse stack of the parser.</summary>
internal sealed class Stack
{
    /// <summary>STACK_VERSION_NONE.</summary>
    internal const uint VersionNone = uint.MaxValue;

    /// <summary>MAX_LINK_COUNT: links of one node.</summary>
    private const int MaxLinkCount = 8;

    /// <summary>MAX_NODE_POOL_SIZE: freed nodes kept for reuse.</summary>
    private const int MaxNodePoolSize = 50;

    /// <summary>MAX_ITERATOR_COUNT: iterators of one stack__iter call.</summary>
    private const int MaxIteratorCount = 64;

    private readonly SubtreePool subtreePool;
    private readonly StackNode baseNode;
    private TsArray<StackHead> heads;
    private TsArray<StackSlice> slices;
    private TsArray<StackIterator> iterators;
    private TsArray<StackNode> nodePool;

    // The state of the callbacks of stack__iter (the C payloads).
    private uint goalSubtreeCount;
    private bool foundError;
    private uint maxDepth;
    private TsArray<StackSummaryEntry> summary;

    // ts_stack_new
    internal Stack(SubtreePool subtreePool)
    {
        this.subtreePool = subtreePool;
        baseNode = NodeNew(null, Subtree.Null, false, 1);
        Clear();
    }

    /// <summary>StackStatus.</summary>
    private enum StackStatus
    {
        Active,
        Paused,
        Halted,
    }

    /// <summary>StackAction.</summary>
    [Flags]
    private enum StackAction : uint
    {
        None = 0,
        Stop = 1,
        Pop = 2,
    }

    /// <summary>The callback of stack__iter: it sees one iterator and tells what to do with it.</summary>
    private delegate StackAction StackCallback(StackIterator iterator);

    /// <summary>StackLink: an edge to the previous node, with the subtree on it.</summary>
    private struct StackLink
    {
        internal StackNode Node;
        internal Subtree Tree;
        internal bool IsPending;
    }

    /// <summary>The links of a node: an InlineArray of MAX_LINK_COUNT elements (decision D6). The
    /// compiler provides the indexer; the type declares no indexer of its own (CS9181).</summary>
    [InlineArray(MaxLinkCount)]
    private struct StackLinks
    {
        private StackLink first;
    }

    /// <summary>StackNode: a node of the graph-structured stack. Nodes are shared by the versions.</summary>
    private sealed class StackNode
    {
        internal ushort State;
        internal Length Position;
        internal StackLinks Links;
        internal int LinkCount;
        internal uint RefCount;
        internal uint ErrorCost;
        internal uint NodeCount;
        internal int DynamicPrecedence;

        /// <summary>The state of a new node: `*node = (StackNode) {...}` in C.</summary>
        internal void Reset(ushort newState)
        {
            State = newState;
            Position = Length.Zero;
            for (int i = 0; i < MaxLinkCount; i++)
            {
                Links[i] = default;
            }

            LinkCount = 0;
            RefCount = 1;
            ErrorCost = 0;
            NodeCount = 0;
            DynamicPrecedence = 0;
        }
    }

    /// <summary>StackIterator: a walk back from a node, with the subtrees it has passed.</summary>
    private struct StackIterator
    {
        internal StackNode Node;
        internal TsArray<Subtree> Subtrees;
        internal uint SubtreeCount;
        internal bool IsPending;
    }

    /// <summary>StackHead: one version of the stack.</summary>
    private struct StackHead
    {
        internal StackNode Node;
        internal TsArray<StackSummaryEntry>? Summary;
        internal uint NodeCountAtLastError;
        internal Subtree LastExternalToken;
        internal Subtree LookaheadWhenPaused;
        internal StackStatus Status;
    }

    // ------------------------------------------------------------ nodes

    /// <summary>stack_node_retain</summary>
    private static void NodeRetain(StackNode? self)
    {
        // No caller passes NULL here (kept as in C).
        if (self is null)
        {
            return;
        }

        Debug.Assert(self.RefCount > 0, "retain of a freed node");
        self.RefCount++;
    }

    /// <summary>stack_node_release: frees the node and, recursively, the predecessors that lose their last reference.
    /// The first predecessor is released in a loop (the goto of C) so that long chains do not recurse.</summary>
    private void NodeRelease(StackNode self)
    {
        StackNode? current = self;
        while (current is not null)
        {
            Debug.Assert(current.RefCount != 0, "release of a freed node");
            current.RefCount--;
            if (current.RefCount > 0)
            {
                return;
            }

            StackNode? firstPredecessor = null;
            if (current.LinkCount > 0)
            {
                for (int i = current.LinkCount - 1; i > 0; i--)
                {
                    StackLink link = current.Links[i];
                    if (!link.Tree.IsNull)
                    {
                        SubtreeOps.Release(subtreePool, link.Tree);
                    }

                    NodeRelease(link.Node);
                }

                StackLink first = current.Links[0];
                if (!first.Tree.IsNull)
                {
                    SubtreeOps.Release(subtreePool, first.Tree);
                }

                firstPredecessor = first.Node;
            }

            if (nodePool.Size < MaxNodePoolSize)
            {
                nodePool.Push(current);
            }

            current = firstPredecessor;
        }
    }

    /// <summary>stack__subtree_node_count: the number of nodes a subtree adds to the progress of a version.</summary>
    private static uint SubtreeNodeCount(Subtree subtree)
    {
        uint count = subtree.VisibleDescendantCount;
        if (subtree.Visible)
        {
            count++;
        }

        // Count intermediate error nodes even though they are not visible,
        // because a stack version's node count is used to check whether it
        // has made any progress since the last time it encountered an error.
        if (subtree.Symbol == BuiltinSymbols.ErrorRepeat)
        {
            count++;
        }

        return count;
    }

    /// <summary>stack_node_new</summary>
    private StackNode NodeNew(StackNode? previousNode, Subtree subtree, bool isPending, ushort state)
    {
        StackNode node = nodePool.Size > 0 ? nodePool.Pop() : new StackNode();
        node.Reset(state);
        if (previousNode is not null)
        {
            node.LinkCount = 1;
            node.Links[0] = new StackLink { Node = previousNode, Tree = subtree, IsPending = isPending };
            node.Position = previousNode.Position;
            node.ErrorCost = previousNode.ErrorCost;
            node.DynamicPrecedence = previousNode.DynamicPrecedence;
            node.NodeCount = previousNode.NodeCount;
            if (!subtree.IsNull)
            {
                node.ErrorCost += subtree.ErrorCost;
                node.Position = Length.Add(node.Position, subtree.TotalSize);
                node.NodeCount += SubtreeNodeCount(subtree);
                node.DynamicPrecedence += subtree.DynamicPrecedence;
            }
        }
        else
        {
            node.Position = Length.Zero;
            node.ErrorCost = 0;
        }

        return node;
    }

    /// <summary>The external scanner state of a subtree: empty for NULL_SUBTREE, as in
    /// ts_subtree_external_scanner_state (SubtreeOps.GetExternalScannerState does not accept NULL).</summary>
    private static ExternalScannerState ExternalScannerStateOf(Subtree tree) =>
        tree.IsNull ? default : SubtreeOps.GetExternalScannerState(tree);

    /// <summary>ts_subtree_external_scanner_state_eq</summary>
    private static bool ExternalScannerStateEqual(Subtree left, Subtree right)
    {
        ExternalScannerState leftState = ExternalScannerStateOf(left);
        ExternalScannerState rightState = ExternalScannerStateOf(right);
        return leftState.Eq(rightState.Data);
    }

    /// <summary>stack__subtree_is_equivalent</summary>
    private static bool SubtreeIsEquivalent(Subtree left, Subtree right)
    {
        if (left.Equals(right))
        {
            return true;
        }

        if (left.IsNull || right.IsNull)
        {
            return false;
        }

        // Symbols must match
        if (left.Symbol != right.Symbol)
        {
            return false;
        }

        // If both have errors, don't bother keeping both.
        if (left.ErrorCost > 0 && right.ErrorCost > 0)
        {
            return true;
        }

        return
            left.Padding.Bytes == right.Padding.Bytes &&
            left.Size.Bytes == right.Size.Bytes &&
            left.ChildCount == right.ChildCount &&
            left.Extra == right.Extra &&
            ExternalScannerStateEqual(left, right);
    }

    /// <summary>stack_node_add_link</summary>
    private void NodeAddLink(StackNode self, StackLink link)
    {
        if (ReferenceEquals(link.Node, self))
        {
            return;
        }

        for (int i = 0; i < self.LinkCount; i++)
        {
            ref StackLink existing = ref self.Links[i];
            if (SubtreeIsEquivalent(existing.Tree, link.Tree))
            {
                // In general, we preserve ambiguities until they are removed from the stack
                // during a pop operation where multiple paths lead to the same node. But in
                // the special case where two links directly connect the same pair of nodes,
                // we can safely remove the ambiguity ahead of time without changing behavior.
                if (ReferenceEquals(existing.Node, link.Node))
                {
                    if (link.Tree.DynamicPrecedence > existing.Tree.DynamicPrecedence)
                    {
                        SubtreeOps.Retain(link.Tree);
                        SubtreeOps.Release(subtreePool, existing.Tree);
                        existing.Tree = link.Tree;
                        self.DynamicPrecedence = link.Node.DynamicPrecedence + link.Tree.DynamicPrecedence;
                    }

                    return;
                }

                // If the previous nodes are mergeable, merge them recursively.
                StackNode previous = existing.Node;
                if (
                    previous.State == link.Node.State &&
                    previous.Position.Bytes == link.Node.Position.Bytes &&
                    previous.ErrorCost == link.Node.ErrorCost)
                {
                    for (int j = 0; j < link.Node.LinkCount; j++)
                    {
                        NodeAddLink(previous, link.Node.Links[j]);
                    }

                    int dynamicPrecedence = link.Node.DynamicPrecedence;
                    if (!link.Tree.IsNull)
                    {
                        dynamicPrecedence += link.Tree.DynamicPrecedence;
                    }

                    if (dynamicPrecedence > self.DynamicPrecedence)
                    {
                        self.DynamicPrecedence = dynamicPrecedence;
                    }

                    return;
                }
            }
        }

        if (self.LinkCount == MaxLinkCount)
        {
            return;
        }

        NodeRetain(link.Node);
        uint nodeCount = link.Node.NodeCount;
        int precedence = link.Node.DynamicPrecedence;
        self.Links[self.LinkCount] = link;
        self.LinkCount++;
        if (!link.Tree.IsNull)
        {
            SubtreeOps.Retain(link.Tree);
            nodeCount += SubtreeNodeCount(link.Tree);
            precedence += link.Tree.DynamicPrecedence;
        }

        if (nodeCount > self.NodeCount)
        {
            self.NodeCount = nodeCount;
        }

        if (precedence > self.DynamicPrecedence)
        {
            self.DynamicPrecedence = precedence;
        }
    }

    // ------------------------------------------------------------ heads

    /// <summary>stack_head_delete</summary>
    private void HeadDelete(ref StackHead head)
    {
        if (!head.LastExternalToken.IsNull)
        {
            SubtreeOps.Release(subtreePool, head.LastExternalToken);
        }

        if (!head.LookaheadWhenPaused.IsNull)
        {
            SubtreeOps.Release(subtreePool, head.LookaheadWhenPaused);
        }

        head.Summary = null;
        NodeRelease(head.Node);
    }

    /// <summary>ts_stack__add_version: a new version on the same node as the original, appended at the end.</summary>
    private uint AddVersion(uint originalVersion, StackNode node)
    {
        StackHead original = heads.Get(originalVersion);
        StackHead head = new()
        {
            Node = node,
            NodeCountAtLastError = original.NodeCountAtLastError,
            LastExternalToken = original.LastExternalToken,
            Status = StackStatus.Active,
            LookaheadWhenPaused = Subtree.Null,
        };
        heads.Push(head);
        NodeRetain(node);
        if (!head.LastExternalToken.IsNull)
        {
            SubtreeOps.Retain(head.LastExternalToken);
        }

        return heads.Size - 1;
    }

    /// <summary>ts_stack__add_slice: the slice goes next to a slice with the same node, or to a new version.</summary>
    private void AddSlice(uint originalVersion, StackNode node, TsArray<Subtree> subtrees)
    {
        // The C loop runs from the last slice to the first. Here index i is the slice i - 1.
        for (uint i = slices.Size; i > 0; i--)
        {
            uint version = slices.Get(i - 1).Version;
            if (ReferenceEquals(heads.Get(version).Node, node))
            {
                slices.Insert(i, new StackSlice { Subtrees = subtrees, Version = version });
                return;
            }
        }

        uint newVersion = AddVersion(originalVersion, node);
        slices.Push(new StackSlice { Subtrees = subtrees, Version = newVersion });
    }

    // ------------------------------------------------------------ stack__iter

    /// <summary>stack__iter: walks every path back from the head of the version and calls the callback on
    /// each iterator. The popped subtrees of the paths the callback chooses are returned as slices.
    /// The slices belong to the stack: the next call overwrites them.</summary>
    private TsArray<StackSlice> Iterate(uint version, StackCallback callback, bool includeSubtrees)
    {
        slices.Clear();
        iterators.Clear();

        StackNode headNode = heads.Get(version).Node;
        iterators.Push(new StackIterator
        {
            Node = headNode,
            Subtrees = default,
            IsPending = true,
        });

        while (iterators.Size > 0)
        {
            for (uint i = 0, size = iterators.Size; i < size; i++)
            {
                StackIterator iterator = iterators.Get(i);
                StackNode node = iterator.Node;
                StackAction action = callback(iterator);
                bool shouldPop = (action & StackAction.Pop) != 0;
                bool shouldStop = (action & StackAction.Stop) != 0 || node.LinkCount == 0;
                if (shouldPop)
                {
                    TsArray<Subtree> subtrees = iterator.Subtrees;
                    // Every callback of this file that pops also stops, so this copy is not reached
                    // (kept as in C).
                    if (!shouldStop)
                    {
                        SubtreeOps.ArrayCopy(iterator.Subtrees, ref subtrees);
                    }

                    SubtreeOps.ArrayReverse(ref subtrees);
                    AddSlice(version, node, subtrees);
                }

                if (shouldStop)
                {
                    if (!shouldPop)
                    {
                        SubtreeOps.ArrayDelete(subtreePool, ref iterators.Get(i).Subtrees);
                    }

                    iterators.Erase(i);
                    i--;
                    size--;
                    continue;
                }

                for (uint j = 1; j <= node.LinkCount; j++)
                {
                    StackLink link;
                    uint nextIndex;
                    if (j == node.LinkCount)
                    {
                        link = node.Links[0];
                        nextIndex = i;
                    }
                    else
                    {
                        if (iterators.Size >= MaxIteratorCount)
                        {
                            continue;
                        }

                        link = node.Links[(int)j];
                        StackIterator current = iterators.Get(i);
                        iterators.Push(current);
                        nextIndex = iterators.Size - 1;
                        SubtreeOps.ArrayCopy(current.Subtrees, ref iterators.Get(nextIndex).Subtrees);
                    }

                    ref StackIterator next = ref iterators.Get(nextIndex);
                    next.Node = link.Node;
                    if (!link.Tree.IsNull)
                    {
                        if (includeSubtrees)
                        {
                            next.Subtrees.Push(link.Tree);
                            SubtreeOps.Retain(link.Tree);
                        }

                        if (!link.Tree.Extra)
                        {
                            next.SubtreeCount++;
                            if (!link.IsPending)
                            {
                                next.IsPending = false;
                            }
                        }
                    }
                    else
                    {
                        next.SubtreeCount++;
                        next.IsPending = false;
                    }
                }
            }
        }

        return slices;
    }

    // ------------------------------------------------------------ callbacks of stack__iter

    /// <summary>pop_count_callback</summary>
    private StackAction PopCountCallback(StackIterator iterator) =>
        iterator.SubtreeCount == goalSubtreeCount ? StackAction.Pop | StackAction.Stop : StackAction.None;

    /// <summary>pop_pending_callback</summary>
    private static StackAction PopPendingCallback(StackIterator iterator)
    {
        if (iterator.SubtreeCount >= 1)
        {
            return iterator.IsPending ? StackAction.Pop | StackAction.Stop : StackAction.Stop;
        }

        return StackAction.None;
    }

    /// <summary>pop_error_callback</summary>
    private StackAction PopErrorCallback(StackIterator iterator)
    {
        if (iterator.Subtrees.Size > 0)
        {
            if (!foundError && iterator.Subtrees.Get(0).IsError)
            {
                foundError = true;
                return StackAction.Pop | StackAction.Stop;
            }

            return StackAction.Stop;
        }

        return StackAction.None;
    }

    /// <summary>pop_all_callback</summary>
    private static StackAction PopAllCallback(StackIterator iterator) =>
        iterator.Node.LinkCount == 0 ? StackAction.Pop : StackAction.None;

    /// <summary>summarize_stack_callback</summary>
    private StackAction SummarizeCallback(StackIterator iterator)
    {
        ushort state = iterator.Node.State;
        uint depth = iterator.SubtreeCount;
        if (depth > maxDepth)
        {
            return StackAction.Stop;
        }

        for (uint i = summary.Size; i > 0; i--)
        {
            StackSummaryEntry entry = summary.Get(i - 1);
            if (entry.Depth < depth)
            {
                break;
            }

            if (entry.Depth == depth && entry.State == state)
            {
                return StackAction.None;
            }
        }

        summary.Push(new StackSummaryEntry(iterator.Node.Position, depth, state));
        return StackAction.None;
    }

    // ------------------------------------------------------------ public operations

    /// <summary>ts_stack_delete</summary>
    internal void Delete()
    {
        slices.Delete();
        iterators.Delete();
        NodeRelease(baseNode);
        for (uint i = 0; i < heads.Size; i++)
        {
            HeadDelete(ref heads.Get(i));
        }

        heads.Clear();
        nodePool.Delete();
        heads.Delete();
    }

    /// <summary>ts_stack_version_count</summary>
    internal uint VersionCount => heads.Size;

    /// <summary>ts_stack_halted_version_count</summary>
    internal uint HaltedVersionCount()
    {
        uint count = 0;
        for (uint i = 0; i < heads.Size; i++)
        {
            if (IsHalted(i))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>ts_stack_state</summary>
    internal ushort State(uint version) => heads.Get(version).Node.State;

    /// <summary>ts_stack_last_external_token</summary>
    internal Subtree LastExternalToken(uint version) => heads.Get(version).LastExternalToken;

    /// <summary>ts_stack_set_last_external_token: the stack retains the token.</summary>
    internal void SetLastExternalToken(uint version, Subtree token)
    {
        ref StackHead head = ref heads.Get(version);
        if (!token.IsNull)
        {
            SubtreeOps.Retain(token);
        }

        if (!head.LastExternalToken.IsNull)
        {
            SubtreeOps.Release(subtreePool, head.LastExternalToken);
        }

        head.LastExternalToken = token;
    }

    /// <summary>ts_stack_position</summary>
    internal Length Position(uint version) => heads.Get(version).Node.Position;

    /// <summary>ts_stack_push</summary>
    internal void Push(uint version, Subtree subtree, bool pending, ushort state)
    {
        ref StackHead head = ref heads.Get(version);
        StackNode newNode = NodeNew(head.Node, subtree, pending, state);
        if (subtree.IsNull)
        {
            head.NodeCountAtLastError = newNode.NodeCount;
        }

        head.Node = newNode;
    }

    /// <summary>ts_stack_pop_count</summary>
    internal TsArray<StackSlice> PopCount(uint version, uint count)
    {
        goalSubtreeCount = count;
        return Iterate(version, PopCountCallback, includeSubtrees: true);
    }

    /// <summary>ts_stack_pop_error: the subtrees of the error on the top of the version, or none.</summary>
    internal TsArray<Subtree> PopError(uint version)
    {
        StackNode node = heads.Get(version).Node;
        for (int i = 0; i < node.LinkCount; i++)
        {
            if (!node.Links[i].Tree.IsNull && node.Links[i].Tree.IsError)
            {
                foundError = false;
                TsArray<StackSlice> pop = Iterate(version, PopErrorCallback, includeSubtrees: true);
                if (pop.Size > 0)
                {
                    Debug.Assert(pop.Size == 1, "pop_error gives one slice");
                    RenumberVersion(pop.Get(0).Version, version);
                    return pop.Get(0).Subtrees;
                }

                break;
            }
        }

        return default;
    }

    /// <summary>ts_stack_pop_pending: the slice is renamed to the version.</summary>
    internal TsArray<StackSlice> PopPending(uint version)
    {
        TsArray<StackSlice> pop = Iterate(version, PopPendingCallback, includeSubtrees: true);
        if (pop.Size > 0)
        {
            RenumberVersion(pop.Get(0).Version, version);
            pop.Get(0).Version = version;
        }

        return pop;
    }

    /// <summary>ts_stack_pop_all</summary>
    internal TsArray<StackSlice> PopAll(uint version) =>
        Iterate(version, PopAllCallback, includeSubtrees: true);

    /// <summary>ts_stack_node_count_since_error: updates the count at the last error of the head.</summary>
    internal uint NodeCountSinceError(uint version)
    {
        ref StackHead head = ref heads.Get(version);
        if (head.Node.NodeCount < head.NodeCountAtLastError)
        {
            head.NodeCountAtLastError = head.Node.NodeCount;
        }

        return head.Node.NodeCount - head.NodeCountAtLastError;
    }

    /// <summary>ts_stack_dynamic_precedence</summary>
    internal int DynamicPrecedence(uint version) => heads.Get(version).Node.DynamicPrecedence;

    /// <summary>ts_stack_has_advanced_since_error</summary>
    internal bool HasAdvancedSinceError(uint version)
    {
        StackHead head = heads.Get(version);
        StackNode node = head.Node;
        if (node.ErrorCost == 0)
        {
            return true;
        }

        while (true)
        {
            if (node.LinkCount > 0)
            {
                Subtree subtree = node.Links[0].Tree;
                if (!subtree.IsNull)
                {
                    if (subtree.TotalBytes > 0)
                    {
                        return true;
                    }

                    if (node.NodeCount > head.NodeCountAtLastError && subtree.ErrorCost == 0)
                    {
                        node = node.Links[0].Node;
                        continue;
                    }
                }
            }

            break;
        }

        return false;
    }

    /// <summary>ts_stack_record_summary</summary>
    internal void RecordSummary(uint version, uint depthLimit)
    {
        maxDepth = depthLimit;
        summary = default;
        Iterate(version, SummarizeCallback, includeSubtrees: false);
        heads.Get(version).Summary = summary;
        summary = default;
    }

    /// <summary>ts_stack_get_summary (null when no summary was recorded)</summary>
    internal TsArray<StackSummaryEntry>? GetSummary(uint version) => heads.Get(version).Summary;

    /// <summary>ts_stack_error_cost</summary>
    internal uint ErrorCost(uint version)
    {
        StackHead head = heads.Get(version);
        uint result = head.Node.ErrorCost;
        if (
            head.Status == StackStatus.Paused ||
            (head.Node.State == ErrorCosts.ErrorState && head.Node.Links[0].Tree.IsNull))
        {
            result += ErrorCosts.ErrorCostPerRecovery;
        }

        return result;
    }

    /// <summary>ts_stack_merge</summary>
    internal bool Merge(uint version1, uint version2)
    {
        if (!CanMerge(version1, version2))
        {
            return false;
        }

        StackNode node1 = heads.Get(version1).Node;
        StackNode node2 = heads.Get(version2).Node;
        for (int i = 0; i < node2.LinkCount; i++)
        {
            NodeAddLink(node1, node2.Links[i]);
        }

        if (node1.State == ErrorCosts.ErrorState)
        {
            heads.Get(version1).NodeCountAtLastError = node1.NodeCount;
        }

        RemoveVersion(version2);
        return true;
    }

    /// <summary>ts_stack_can_merge</summary>
    internal bool CanMerge(uint version1, uint version2)
    {
        StackHead head1 = heads.Get(version1);
        StackHead head2 = heads.Get(version2);
        return
            head1.Status == StackStatus.Active &&
            head2.Status == StackStatus.Active &&
            head1.Node.State == head2.Node.State &&
            head1.Node.Position.Bytes == head2.Node.Position.Bytes &&
            head1.Node.ErrorCost == head2.Node.ErrorCost &&
            ExternalScannerStateEqual(head1.LastExternalToken, head2.LastExternalToken);
    }

    /// <summary>ts_stack_resume: the version becomes active again and the lookahead is returned.</summary>
    internal Subtree Resume(uint version)
    {
        ref StackHead head = ref heads.Get(version);
        Debug.Assert(head.Status == StackStatus.Paused, "resume of a version that is not paused");
        Subtree result = head.LookaheadWhenPaused;
        head.Status = StackStatus.Active;
        head.LookaheadWhenPaused = Subtree.Null;
        return result;
    }

    /// <summary>ts_stack_pause: the version keeps the lookahead until it is resumed.</summary>
    internal void Pause(uint version, Subtree lookahead)
    {
        ref StackHead head = ref heads.Get(version);
        head.Status = StackStatus.Paused;
        head.LookaheadWhenPaused = lookahead;
        head.NodeCountAtLastError = head.Node.NodeCount;
    }

    /// <summary>ts_stack_halt</summary>
    internal void Halt(uint version) => heads.Get(version).Status = StackStatus.Halted;

    /// <summary>ts_stack_is_active</summary>
    internal bool IsActive(uint version) => heads.Get(version).Status == StackStatus.Active;

    /// <summary>ts_stack_is_paused</summary>
    internal bool IsPaused(uint version) => heads.Get(version).Status == StackStatus.Paused;

    /// <summary>ts_stack_is_halted</summary>
    internal bool IsHalted(uint version) => heads.Get(version).Status == StackStatus.Halted;

    /// <summary>ts_stack_renumber_version: the version v1 takes the place of v2, which must be lower.</summary>
    internal void RenumberVersion(uint version1, uint version2)
    {
        if (version1 == version2)
        {
            return;
        }

        Debug.Assert(version2 < version1, "the target version must be lower");
        Debug.Assert(version1 < heads.Size, "invalid version");
        ref StackHead source = ref heads.Get(version1);
        ref StackHead target = ref heads.Get(version2);
        if (target.Summary is not null && source.Summary is null)
        {
            source.Summary = target.Summary;
            target.Summary = null;
        }

        HeadDelete(ref target);
        target = source;
        heads.Erase(version1);
    }

    /// <summary>ts_stack_swap_versions</summary>
    internal void SwapVersions(uint version1, uint version2)
    {
        StackHead temporary = heads.Get(version1);
        heads.Get(version1) = heads.Get(version2);
        heads.Get(version2) = temporary;
    }

    /// <summary>ts_stack_copy_version: the copy is appended at the end and returned.</summary>
    internal uint CopyVersion(uint version)
    {
        Debug.Assert(version < heads.Size, "invalid version");
        heads.Push(heads.Get(version));
        ref StackHead head = ref heads.Get(heads.Size - 1);
        NodeRetain(head.Node);
        if (!head.LastExternalToken.IsNull)
        {
            SubtreeOps.Retain(head.LastExternalToken);
        }

        head.Summary = null;
        return heads.Size - 1;
    }

    /// <summary>ts_stack_remove_version</summary>
    internal void RemoveVersion(uint version)
    {
        HeadDelete(ref heads.Get(version));
        heads.Erase(version);
    }

    /// <summary>ts_stack_clear: one version, on the base node.</summary>
    internal void Clear()
    {
        NodeRetain(baseNode);
        for (uint i = 0; i < heads.Size; i++)
        {
            HeadDelete(ref heads.Get(i));
        }

        heads.Clear();
        heads.Push(new StackHead
        {
            Node = baseNode,
            Status = StackStatus.Active,
            LastExternalToken = Subtree.Null,
            LookaheadWhenPaused = Subtree.Null,
        });
    }

    /// <summary>ts_stack_print_dot_graph. A node is printed as node_N, N in the order of its first appearance.</summary>
    internal bool PrintDotGraph(Language language, TextWriter writer)
    {
        Dictionary<StackNode, uint> ids = new(ReferenceEqualityComparer.Instance);
        uint NodeId(StackNode node)
        {
            if (!ids.TryGetValue(node, out uint id))
            {
                id = (uint)ids.Count;
                ids.Add(node, id);
            }

            return id;
        }

        iterators.Clear();
        writer.Write("digraph stack {\n");
        writer.Write("rankdir=\"RL\";\n");
        writer.Write("edge [arrowhead=none]\n");
        HashSet<StackNode> visited = new(ReferenceEqualityComparer.Instance);
        for (uint i = 0; i < heads.Size; i++)
        {
            StackHead head = heads.Get(i);
            if (head.Status == StackStatus.Halted)
            {
                continue;
            }

            writer.Write("node_head_" + Unsigned(i) + " [shape=none, label=\"\"]\n");
            writer.Write("node_head_" + Unsigned(i) + " -> node_" + Unsigned(NodeId(head.Node)) + " [");
            if (head.Status == StackStatus.Paused)
            {
                writer.Write("color=red ");
            }

            writer.Write(
                "label=" + Unsigned(i) + ", fontcolor=blue, weight=10000, labeltooltip=\"node_count: " +
                Unsigned(NodeCountSinceError(i)) + "\nerror_cost: " + Unsigned(ErrorCost(i)));
            if (head.Summary is not null)
            {
                writer.Write("\nsummary:");
                foreach (StackSummaryEntry entry in head.Summary.Value.AsSpan())
                {
                    writer.Write(" " + Unsigned(entry.State));
                }
            }

            if (!head.LastExternalToken.IsNull)
            {
                writer.Write("\nexternal_scanner_state:");
                ExternalScannerState state = SubtreeOps.GetExternalScannerState(head.LastExternalToken);
                foreach (byte value in state.Data)
                {
                    // The C code prints a signed char with %2X: a negative value is printed as a 32-bit unsigned int.
                    string hex = ((uint)(int)(sbyte)value).ToString("X", CultureInfo.InvariantCulture);
                    writer.Write(" " + hex.PadLeft(2));
                }
            }

            writer.Write("\"]\n");
            iterators.Push(new StackIterator { Node = head.Node });
        }

        bool allIteratorsDone = false;
        while (!allIteratorsDone)
        {
            allIteratorsDone = true;
            for (uint i = 0; i < iterators.Size; i++)
            {
                StackIterator iterator = iterators.Get(i);
                StackNode node = iterator.Node;
                if (visited.Contains(node))
                {
                    continue;
                }

                allIteratorsDone = false;
                writer.Write("node_" + Unsigned(NodeId(node)) + " [");
                if (node.State == ErrorCosts.ErrorState)
                {
                    writer.Write("label=\"?\"");
                }
                else if (node.LinkCount == 1 && !node.Links[0].Tree.IsNull && node.Links[0].Tree.Extra)
                {
                    writer.Write("shape=point margin=0 label=\"\"");
                }
                else
                {
                    writer.Write("label=\"" + Unsigned(node.State) + "\"");
                }

                writer.Write(
                    " tooltip=\"position: " + Unsigned(node.Position.Extent.Row + 1) + "," +
                    Unsigned(node.Position.Extent.Column) + "\nnode_count:" + Unsigned(node.NodeCount) +
                    "\nerror_cost: " + Unsigned(node.ErrorCost) + "\ndynamic_precedence: " +
                    Signed(node.DynamicPrecedence) + "\"];\n");
                for (int j = 0; j < node.LinkCount; j++)
                {
                    StackLink link = node.Links[j];
                    writer.Write("node_" + Unsigned(NodeId(node)) + " -> node_" + Unsigned(NodeId(link.Node)) + " [");
                    if (link.IsPending)
                    {
                        writer.Write("style=dashed ");
                    }

                    if (!link.Tree.IsNull && link.Tree.Extra)
                    {
                        writer.Write("fontcolor=gray ");
                    }

                    if (link.Tree.IsNull)
                    {
                        writer.Write("color=red");
                    }
                    else
                    {
                        writer.Write("label=\"");
                        bool quoted = link.Tree.Visible && !link.Tree.Named;
                        if (quoted)
                        {
                            writer.Write('\'');
                        }

                        WriteSymbolAsDotString(language, writer, link.Tree.Symbol);
                        if (quoted)
                        {
                            writer.Write('\'');
                        }

                        writer.Write("\"");
                        writer.Write(
                            "labeltooltip=\"error_cost: " + Unsigned(link.Tree.ErrorCost) +
                            "\ndynamic_precedence: " + Signed(link.Tree.DynamicPrecedence) + "\"");
                    }

                    writer.Write("];\n");
                    if (j == 0)
                    {
                        iterators.Get(i).Node = link.Node;
                    }
                    else
                    {
                        iterators.Push(iterator);
                        iterators.Get(iterators.Size - 1).Node = link.Node;
                    }
                }

                visited.Add(node);
            }
        }

        writer.Write("}\n");
        return true;
    }

    /// <summary>ts_language_write_symbol_as_dot_string: the name of a symbol, escaped for a dot label.</summary>
    private static void WriteSymbolAsDotString(Language language, TextWriter writer, ushort symbol)
    {
        string name = language.SymbolName(symbol) ?? string.Empty;
        foreach (char c in name)
        {
            switch (c)
            {
                case '"':
                case '\\':
                    writer.Write('\\');
                    writer.Write(c);
                    break;
                case '\n':
                    writer.Write("\\n");
                    break;
                case '\t':
                    writer.Write("\\t");
                    break;
                default:
                    writer.Write(c);
                    break;
            }
        }
    }

    /// <summary>The text of an unsigned number, as printf %u.</summary>
    private static string Unsigned(uint value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The text of a signed number, as printf %d.</summary>
    private static string Signed(int value) => value.ToString(CultureInfo.InvariantCulture);
}
