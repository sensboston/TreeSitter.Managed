// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/tree_cursor.c, lib/src/tree_cursor.h (internal part).
// Rules: docs/ARCHITECTURE.md D2. Each method has the C name above it and the same logic.
// - TreeCursorEntry.subtree (a Subtree slot address) is (Owner, Slot) as in Node.
// - The internal part of tree_cursor.h (current_status, goto_*_internal, parent_node) is kept
//   for the query engine (M4).

using TreeSitter.Abi;
using TreeSitter.Subtrees;

namespace TreeSitter;

/// <summary>TreeCursorEntry: a frame on the cursor stack.</summary>
internal struct TreeCursorEntry
{
    internal object? Owner;
    internal int Slot;
    internal Length Position;
    internal uint ChildIndex;
    internal uint StructuralChildIndex;
    internal uint DescendantIndex;

    /// <summary>The subtree in the slot (ts_tree_cursor_current_subtree).</summary>
    internal Subtree Subtree => Owner switch
    {
        Tree t => t.Root,
        SubtreeHeap h => h.Children![Slot],
        _ => Subtree.Null
    };
}

/// <summary>TreeCursorStep.</summary>
internal enum TreeCursorStep
{
    None,
    Hidden,
    Visible,
}

/// <summary>A cursor for walking a syntax tree (TSTreeCursor).</summary>
public sealed class TreeCursor
{
    private Tree? _tree;

    internal TsArray<TreeCursorEntry> Stack;
    internal ushort RootAliasSymbol;

    /// <summary>ts_tree_cursor_new</summary>
    public TreeCursor(Node node)
    {
        Init(node);
    }

    internal TreeCursor()
    {
    }

    /// <summary>The tree of the cursor (TreeCursor.tree).</summary>
    public Tree Tree => _tree ?? throw new InvalidOperationException("The cursor is null.");

    /// <summary>ts_tree_cursor_reset</summary>
    public void Reset(Node node)
    {
        Init(node);
    }

    /// <summary>ts_tree_cursor_reset_to</summary>
    public void ResetTo(TreeCursor source)
    {
        _tree = source._tree;
        RootAliasSymbol = source.RootAliasSymbol;
        Stack.Clear();
        Stack.PushAll(source.Stack);
    }

    /// <summary>ts_tree_cursor_copy</summary>
    public TreeCursor Copy()
    {
        TreeCursor copy = new();
        copy._tree = _tree;
        copy.RootAliasSymbol = RootAliasSymbol;
        copy.Stack.Init();
        copy.Stack.PushAll(Stack);
        return copy;
    }

    /// <summary>ts_tree_cursor_current_node</summary>
    public Node CurrentNode
    {
        get
        {
            ref TreeCursorEntry lastEntry = ref Stack.Back();
            Subtree subtree = lastEntry.Subtree;
            bool isExtra = subtree.Extra;
            ushort aliasSymbol = isExtra ? (ushort)0 : RootAliasSymbol;

            if (Stack.Size > 1 && !isExtra)
            {
                ref TreeCursorEntry parentEntry = ref Stack.Get(Stack.Size - 2);
                SubtreeHeap parentHeap = parentEntry.Subtree.Heap!;
                aliasSymbol = _tree!.Language.AliasAt((uint)parentHeap.ProductionId, lastEntry.StructuralChildIndex);
            }

            return new Node(_tree!, lastEntry.Owner!, lastEntry.Slot, lastEntry.Position, aliasSymbol);
        }
    }

    /// <summary>ts_tree_cursor_current_field_name</summary>
    public string? CurrentFieldName
    {
        get
        {
            ushort id = CurrentFieldId;
            if (id != 0)
            {
                return _tree!.Language.FieldNameForId(id);
            }

            return null;
        }
    }

    /// <summary>ts_tree_cursor_current_field_id</summary>
    public ushort CurrentFieldId
    {
        get
        {
            for (int i = (int)Stack.Size - 1; i > 0; i--)
            {
                ref TreeCursorEntry entry = ref Stack.Get((uint)i);
                ref TreeCursorEntry parentEntry = ref Stack.Get((uint)(i - 1));

                if (i != (int)Stack.Size - 1 && IsEntryVisible((uint)i))
                {
                    break;
                }

                if (entry.Subtree.Extra)
                {
                    break;
                }

                SubtreeHeap parentHeap = parentEntry.Subtree.Heap!;
                ReadOnlySpan<FieldMapEntry> fieldMap = _tree!.Language.FieldMap((uint)parentHeap.ProductionId);

                foreach (ref readonly FieldMapEntry map in fieldMap)
                {
                    if (!map.Inherited && map.ChildIndex == entry.StructuralChildIndex)
                    {
                        return map.FieldId;
                    }
                }
            }

            return 0;
        }
    }

    /// <summary>ts_tree_cursor_goto_parent</summary>
    public bool GotoParent()
    {
        for (int i = (int)Stack.Size - 2; i + 1 > 0; i--)
        {
            if (IsEntryVisible((uint)i))
            {
                Stack.Size = (uint)(i + 1);
                return true;
            }
        }

        return false;
    }

    /// <summary>ts_tree_cursor_goto_next_sibling</summary>
    public bool GotoNextSibling()
    {
        switch (GotoNextSiblingInternal())
        {
            case TreeCursorStep.Hidden:
                GotoFirstChild();
                return true;
            case TreeCursorStep.Visible:
                return true;
            default:
                return false;
        }
    }

    /// <summary>ts_tree_cursor_goto_previous_sibling</summary>
    public bool GotoPreviousSibling()
    {
        switch (GotoPreviousSiblingInternal())
        {
            case TreeCursorStep.Hidden:
                GotoLastChild();
                return true;
            case TreeCursorStep.Visible:
                return true;
            default:
                return false;
        }
    }

    /// <summary>ts_tree_cursor_goto_first_child</summary>
    public bool GotoFirstChild()
    {
        for (;;)
        {
            switch (GotoFirstChildInternal())
            {
                case TreeCursorStep.Hidden:
                    continue;
                case TreeCursorStep.Visible:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>ts_tree_cursor_goto_last_child</summary>
    public bool GotoLastChild()
    {
        for (;;)
        {
            switch (GotoLastChildInternal())
            {
                case TreeCursorStep.Hidden:
                    continue;
                case TreeCursorStep.Visible:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>ts_tree_cursor_goto_descendant</summary>
    public void GotoDescendant(uint goalDescendantIndex)
    {
        while (true)
        {
            uint i = Stack.Size - 1;
            ref TreeCursorEntry entry = ref Stack.Get(i);
            uint nextDescendantIndex =
                entry.DescendantIndex +
                (IsEntryVisible(i) ? 1u : 0u) +
                entry.Subtree.VisibleDescendantCount;

            if (entry.DescendantIndex <= goalDescendantIndex && nextDescendantIndex > goalDescendantIndex)
            {
                break;
            }

            if (Stack.Size <= 1)
            {
                return;
            }

            Stack.Size--;
        }

        bool didDescend = true;
        do
        {
            didDescend = false;
            CursorChildIterator iterator = CreateChildIterator();

            if (iterator.DescendantIndex > goalDescendantIndex)
            {
                return;
            }

            TreeCursorEntry entry;
            bool visible;
            while (CursorChildIterator.Next(ref iterator, out entry, out visible))
            {
                if (iterator.DescendantIndex > goalDescendantIndex)
                {
                    Stack.Push(entry);
                    if (visible && entry.DescendantIndex == goalDescendantIndex)
                    {
                        return;
                    }

                    didDescend = true;
                    break;
                }
            }
        } while (didDescend);
    }

    /// <summary>ts_tree_cursor_current_descendant_index</summary>
    public uint CurrentDescendantIndex => Stack.Back().DescendantIndex;

    /// <summary>ts_tree_cursor_current_depth</summary>
    public uint CurrentDepth
    {
        get
        {
            uint depth = 0;
            for (uint i = 1; i < Stack.Size; i++)
            {
                if (IsEntryVisible(i))
                {
                    depth++;
                }
            }

            return depth;
        }
    }

    /// <summary>ts_tree_cursor_goto_first_child_for_byte (-1 when there is none)</summary>
    public long GotoFirstChildForByte(uint goalByte) => GotoFirstChildForByteAndPoint(goalByte, new Point(0, 0));

    /// <summary>ts_tree_cursor_goto_first_child_for_point (-1 when there is none)</summary>
    public long GotoFirstChildForPoint(Point goalPoint) => GotoFirstChildForByteAndPoint(0, goalPoint);

    // ts_tree_cursor_init
    internal void Init(Node node)
    {
        _tree = node.Tree;
        RootAliasSymbol = node.Alias;
        Stack.Clear();
        Stack.Push(new TreeCursorEntry
        {
            Owner = node.Owner,
            Slot = node.Slot,
            Position = new Length(node.StartByteValue, new Point(node.StartRow, node.StartColumn)),
            ChildIndex = 0,
            StructuralChildIndex = 0,
            DescendantIndex = 0,
        });
    }

    // ts_tree_cursor_current_subtree
    internal Subtree CurrentSubtree => Stack.Back().Subtree;

    // ts_tree_cursor_goto_first_child_internal
    internal TreeCursorStep GotoFirstChildInternal()
    {
        CursorChildIterator iterator = CreateChildIterator();
        TreeCursorEntry entry;
        bool visible;

        while (CursorChildIterator.Next(ref iterator, out entry, out visible))
        {
            if (visible)
            {
                Stack.Push(entry);
                return TreeCursorStep.Visible;
            }

            if (entry.Subtree.VisibleChildCount > 0)
            {
                Stack.Push(entry);
                return TreeCursorStep.Hidden;
            }
        }

        return TreeCursorStep.None;
    }

    // ts_tree_cursor_goto_last_child_internal
    internal TreeCursorStep GotoLastChildInternal()
    {
        CursorChildIterator iterator = CreateChildIterator();
        TreeCursorEntry lastEntry = default;
        bool hasLastEntry = false;
        TreeCursorStep lastStep = TreeCursorStep.None;

        while (CursorChildIterator.Next(ref iterator, out TreeCursorEntry entry, out bool visible))
        {
            if (visible)
            {
                lastEntry = entry;
                lastStep = TreeCursorStep.Visible;
                hasLastEntry = true;
            }
            else if (entry.Subtree.VisibleChildCount > 0)
            {
                lastEntry = entry;
                lastStep = TreeCursorStep.Hidden;
                hasLastEntry = true;
            }
        }

        if (hasLastEntry)
        {
            Stack.Push(lastEntry);
            return lastStep;
        }

        return TreeCursorStep.None;
    }

    // ts_tree_cursor_goto_next_sibling_internal
    internal TreeCursorStep GotoNextSiblingInternal() => GotoSiblingInternal(CursorChildIterator.Next);

    // ts_tree_cursor_goto_previous_sibling_internal
    internal TreeCursorStep GotoPreviousSiblingInternal()
    {
        TreeCursorStep step = GotoSiblingInternal(CursorChildIterator.Previous);
        if (step == TreeCursorStep.None)
        {
            return step;
        }

        if (!Stack.Back().Position.IsUndefined)
        {
            return step;
        }

        ref TreeCursorEntry parent = ref Stack.Get(Stack.Size - 2);
        Length position = parent.Position;
        uint childIndex = Stack.Back().ChildIndex;
        ReadOnlySpan<Subtree> children = parent.Subtree.Children;

        if (childIndex > 0)
        {
            position = Length.Add(position, children[0].Size);
            for (uint i = 1; i < childIndex; i++)
            {
                position = Length.Add(position, children[(int)i].TotalSize);
            }

            position = Length.Add(position, children[(int)childIndex].Padding);
        }

        ref TreeCursorEntry back = ref Stack.Back();
        back.Position = position;

        return step;
    }

    // ts_tree_cursor_parent_node
    internal Node ParentNode()
    {
        for (int i = (int)Stack.Size - 2; i >= 0; i--)
        {
            ref TreeCursorEntry entry = ref Stack.Get((uint)i);
            bool isVisible = true;
            ushort aliasSymbol = 0;

            if (i > 0)
            {
                ref TreeCursorEntry parentEntry = ref Stack.Get((uint)(i - 1));
                SubtreeHeap parentHeap = parentEntry.Subtree.Heap!;
                aliasSymbol = _tree!.Language.AliasAt((uint)parentHeap.ProductionId, entry.StructuralChildIndex);
                isVisible = aliasSymbol != 0 || entry.Subtree.Visible;
            }

            if (isVisible)
            {
                return new Node(_tree!, entry.Owner!, entry.Slot, entry.Position, aliasSymbol);
            }
        }

        return default;
    }

    // ts_tree_cursor_current_status
    internal void CurrentStatus(
        out ushort fieldId,
        out bool hasLaterSiblings,
        out bool hasLaterNamedSiblings,
        out bool canHaveLaterSiblingsWithThisField,
        Span<ushort> supertypes,
        out uint supertypeCount)
    {
        uint maxSupertypes = (uint)supertypes.Length;
        fieldId = 0;
        supertypeCount = 0;
        hasLaterSiblings = false;
        hasLaterNamedSiblings = false;
        canHaveLaterSiblingsWithThisField = false;

        for (int i = (int)Stack.Size - 1; i > 0; i--)
        {
            ref TreeCursorEntry entry = ref Stack.Get((uint)i);
            ref TreeCursorEntry parentEntry = ref Stack.Get((uint)(i - 1));

            SubtreeHeap parentHeap = parentEntry.Subtree.Heap!;
            ReadOnlySpan<ushort> aliasSequenceSpan = default;
            bool hasAliasSequence = _tree!.Language.TryGetAliasSequence(
                (uint)parentHeap.ProductionId, out aliasSequenceSpan);
            ushort[]? aliasSequence = hasAliasSequence ? aliasSequenceSpan.ToArray() : null;

            ushort entrySymbol = SubtreeSymbol(entry.Subtree, aliasSequence, entry.StructuralChildIndex);
            SymbolMetadata entryMetadata = _tree!.Language.GetSymbolMetadata(entrySymbol);
            if (i != (int)Stack.Size - 1 && entryMetadata.Visible)
            {
                break;
            }

            if (entryMetadata.Supertype && supertypeCount < maxSupertypes)
            {
                supertypes[(int)supertypeCount] = entrySymbol;
                supertypeCount++;
            }

            if (!hasLaterNamedSiblings)
            {
                uint siblingCount = parentEntry.Subtree.ChildCount;
                uint structuralChildIndex = entry.StructuralChildIndex;
                if (!entry.Subtree.Extra)
                {
                    structuralChildIndex++;
                }

                for (uint j = entry.ChildIndex + 1; j < siblingCount; j++)
                {
                    Subtree sibling = parentEntry.Subtree.Children[(int)j];
                    ushort siblingSymbol = SubtreeSymbol(sibling, aliasSequence, structuralChildIndex);
                    SymbolMetadata siblingMetadata = _tree!.Language.GetSymbolMetadata(siblingSymbol);

                    if (siblingMetadata.Visible)
                    {
                        hasLaterSiblings = true;
                        if (siblingMetadata.Named)
                        {
                            hasLaterNamedSiblings = true;
                            break;
                        }
                    }
                    else if (sibling.VisibleChildCount > 0)
                    {
                        hasLaterSiblings = true;
                        if (sibling.NamedChildCount > 0)
                        {
                            hasLaterNamedSiblings = true;
                            break;
                        }
                    }

                    if (!sibling.Extra)
                    {
                        structuralChildIndex++;
                    }
                }
            }

            if (!entry.Subtree.Extra)
            {
                ReadOnlySpan<FieldMapEntry> fieldMap = _tree!.Language.FieldMap((uint)parentHeap.ProductionId);

                if (fieldId == 0)
                {
                    foreach (FieldMapEntry map in fieldMap)
                    {
                        if (!map.Inherited && map.ChildIndex == entry.StructuralChildIndex)
                        {
                            fieldId = map.FieldId;
                            break;
                        }
                    }
                }

                if (fieldId != 0)
                {
                    foreach (FieldMapEntry map in fieldMap)
                    {
                        if (map.FieldId == fieldId && map.ChildIndex > entry.StructuralChildIndex)
                        {
                            canHaveLaterSiblingsWithThisField = true;
                            break;
                        }
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Private helpers

    private delegate bool CursorAdvance(ref CursorChildIterator iterator, out TreeCursorEntry entry, out bool visible);

    private TreeCursorStep GotoSiblingInternal(CursorAdvance advance)
    {
        uint initialSize = Stack.Size;

        while (Stack.Size > 1)
        {
            TreeCursorEntry entry = Stack.Pop();
            CursorChildIterator iterator = CreateChildIterator();
            iterator.ChildIndex = entry.ChildIndex;
            iterator.StructuralChildIndex = entry.StructuralChildIndex;
            iterator.Position = entry.Position;
            iterator.DescendantIndex = entry.DescendantIndex;

            bool visible = false;
            if (advance(ref iterator, out entry, out visible) && visible && Stack.Size + 1 < initialSize)
            {
                break;
            }

            while (advance(ref iterator, out entry, out visible))
            {
                if (visible)
                {
                    Stack.Push(entry);
                    return TreeCursorStep.Visible;
                }

                if (entry.Subtree.VisibleChildCount > 0)
                {
                    Stack.Push(entry);
                    return TreeCursorStep.Hidden;
                }
            }
        }

        Stack.Size = initialSize;
        return TreeCursorStep.None;
    }

    private CursorChildIterator CreateChildIterator()
    {
        ref TreeCursorEntry lastEntry = ref Stack.Back();
        Subtree parentSubtree = lastEntry.Subtree;

        if (parentSubtree.IsNull || parentSubtree.ChildCount == 0)
        {
            return new CursorChildIterator
            {
                Tree = _tree!,
                ParentSubtree = Subtree.Null,
                Position = Length.Zero,
                ChildIndex = 0,
                StructuralChildIndex = 0,
                DescendantIndex = 0,
                AliasSequence = null,
            };
        }

        ushort[]? aliasSequence = null;
        SubtreeHeap heap = parentSubtree.Heap!;
        if (_tree!.Language.TryGetAliasSequence((uint)heap.ProductionId, out ReadOnlySpan<ushort> sequence))
        {
            aliasSequence = sequence.ToArray();
        }

        uint descendantIndex = lastEntry.DescendantIndex;
        if (IsEntryVisible(Stack.Size - 1))
        {
            descendantIndex++;
        }

        return new CursorChildIterator
        {
            Tree = _tree!,
            ParentSubtree = parentSubtree,
            Position = lastEntry.Position,
            ChildIndex = 0,
            StructuralChildIndex = 0,
            DescendantIndex = descendantIndex,
            AliasSequence = aliasSequence,
        };
    }

    private bool IsEntryVisible(uint index)
    {
        ref TreeCursorEntry entry = ref Stack.Get(index);
        if (index == 0 || entry.Subtree.Visible)
        {
            return true;
        }

        if (!entry.Subtree.Extra)
        {
            ref TreeCursorEntry parentEntry = ref Stack.Get(index - 1);
            SubtreeHeap parentHeap = parentEntry.Subtree.Heap!;
            return _tree!.Language.AliasAt((uint)parentHeap.ProductionId, entry.StructuralChildIndex) != 0;
        }

        return false;
    }

    private static ushort SubtreeSymbol(Subtree st, ushort[]? aliasSequence, uint structuralChildIndex)
    {
        if (!st.Extra && aliasSequence is not null && structuralChildIndex < (uint)aliasSequence.Length && aliasSequence[structuralChildIndex] != 0)
        {
            return aliasSequence[structuralChildIndex];
        }

        return st.Symbol;
    }

    private long GotoFirstChildForByteAndPoint(uint goalByte, Point goalPoint)
    {
        uint initialSize = Stack.Size;
        uint visibleChildIndex = 0;

        bool didDescend;
        do
        {
            didDescend = false;
            CursorChildIterator iterator = CreateChildIterator();
            TreeCursorEntry entry;
            bool visible;

            while (CursorChildIterator.Next(ref iterator, out entry, out visible))
            {
                Length entryEnd = Length.Add(entry.Position, entry.Subtree.Size);
                bool atGoal = (uint)entryEnd.Bytes > goalByte && Point.Gt(entryEnd.Extent, goalPoint);
                uint visibleChildCount = entry.Subtree.VisibleChildCount;

                if (atGoal)
                {
                    if (visible)
                    {
                        Stack.Push(entry);
                        return (long)visibleChildIndex;
                    }

                    if (visibleChildCount > 0)
                    {
                        Stack.Push(entry);
                        didDescend = true;
                        break;
                    }
                }
                else if (visible)
                {
                    visibleChildIndex++;
                }
                else
                {
                    visibleChildIndex += visibleChildCount;
                }
            }
        } while (didDescend);

        Stack.Size = initialSize;
        return -1;
    }
}

/// <summary>CursorChildIterator: iterates over the children of the cursor's current node.</summary>
internal struct CursorChildIterator
{
    internal Tree Tree;
    internal Subtree ParentSubtree;
    internal Length Position;
    internal uint ChildIndex;
    internal uint StructuralChildIndex;
    internal uint DescendantIndex;
    internal ushort[]? AliasSequence;

    internal bool Done => ChildIndex >= ParentSubtree.ChildCount;

    internal static bool Next(ref CursorChildIterator self, out TreeCursorEntry entry, out bool visible)
    {
        if (self.ParentSubtree.IsNull || self.Done)
        {
            entry = default;
            visible = false;
            return false;
        }

        Subtree child = self.ParentSubtree.Children![(int)self.ChildIndex];
        entry = new TreeCursorEntry
        {
            Owner = self.ParentSubtree.Heap!,
            Slot = (int)self.ChildIndex,
            Position = self.Position,
            ChildIndex = self.ChildIndex,
            StructuralChildIndex = self.StructuralChildIndex,
            DescendantIndex = self.DescendantIndex,
        };
        visible = child.Visible;
        bool extra = child.Extra;

        if (!extra)
        {
            if (self.AliasSequence is not null && self.StructuralChildIndex < self.AliasSequence.Length)
            {
                visible |= self.AliasSequence[self.StructuralChildIndex] != 0;
            }

            self.StructuralChildIndex++;
        }

        self.DescendantIndex += child.VisibleDescendantCount;
        if (visible)
        {
            self.DescendantIndex++;
        }

        self.Position = Length.Add(self.Position, child.Size);
        self.ChildIndex++;

        if (self.ChildIndex < self.ParentSubtree.ChildCount)
        {
            Subtree nextChild = self.ParentSubtree.Children![(int)self.ChildIndex];
            self.Position = Length.Add(self.Position, nextChild.Padding);
        }

        return true;
    }

    internal static bool Previous(ref CursorChildIterator self, out TreeCursorEntry entry, out bool visible)
    {
        if (self.ParentSubtree.IsNull || self.ChildIndex == uint.MaxValue)
        {
            entry = default;
            visible = false;
            return false;
        }

        Subtree child = self.ParentSubtree.Children![(int)self.ChildIndex];
        entry = new TreeCursorEntry
        {
            Owner = self.ParentSubtree.Heap!,
            Slot = (int)self.ChildIndex,
            Position = self.Position,
            ChildIndex = self.ChildIndex,
            StructuralChildIndex = self.StructuralChildIndex,
            DescendantIndex = self.DescendantIndex,
        };
        visible = child.Visible;
        bool extra = child.Extra;

        self.Position = LengthBacktrack(self.Position, child.Padding);
        self.ChildIndex--;

        if (!extra && self.AliasSequence is not null && self.StructuralChildIndex < self.AliasSequence.Length)
        {
            visible |= self.AliasSequence[self.StructuralChildIndex] != 0;
            if (self.StructuralChildIndex > 0)
            {
                self.StructuralChildIndex--;
            }
        }

        if (self.ChildIndex < self.ParentSubtree.ChildCount)
        {
            Subtree previousChild = self.ParentSubtree.Children![(int)self.ChildIndex];
            bool previousVisible = previousChild.Visible;
            if (!previousChild.Extra && self.AliasSequence is not null)
            {
                previousVisible |= self.AliasSequence[self.StructuralChildIndex] != 0;
            }

            self.DescendantIndex -= previousChild.VisibleDescendantCount;
            if (previousVisible)
            {
                self.DescendantIndex--;
            }

            Length size = previousChild.Size;
            self.Position = LengthBacktrack(self.Position, size);
        }

        return true;
    }

    private static Length LengthBacktrack(Length a, Length b)
    {
        if (a.IsUndefined || b.Extent.Row != 0)
        {
            return Length.Undefined;
        }

        return new Length(a.Bytes - b.Bytes, new Point(a.Extent.Row, a.Extent.Column - b.Extent.Column));
    }
}

