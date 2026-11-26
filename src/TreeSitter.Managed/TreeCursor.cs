// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/tree_cursor.c, lib/src/tree_cursor.h (internal part).
// Rules: docs/ARCHITECTURE.md D2. Each method has the C name above it and the same logic.
// - TreeCursorEntry.subtree (a Subtree slot address) is stored as the Subtree value;
//   the old (Owner, Slot) form is only accepted from out-of-scope ChangedRanges.cs.
// - The internal part of tree_cursor.h (current_status, goto_*_internal, parent_node) is kept
//   for the query engine (M4).

using TreeSitter.Abi;
using TreeSitter.Subtrees;

namespace TreeSitter;

/// <summary>TreeCursorEntry: a frame on the cursor stack.</summary>
internal struct TreeCursorEntry
{
    // The subtree itself, like the C pointer: no reference field and no type test
    // on the cursor's hot path.
    internal Subtree SubtreeValue;
    internal int Slot;
    internal Length Position;
    internal uint ChildIndex;
    internal uint StructuralChildIndex;
    internal uint DescendantIndex;

    // Parsing/ChangedRanges.cs is outside this task's edit scope and still writes
    // the old Owner slot in object initializers (before Slot is assigned). Accept
    // that write and resolve it on read, without changing what those entries mean.
    private object? _legacyOwner;
    internal object? Owner
    {
        get => _legacyOwner;
        set => _legacyOwner = value;
    }

    /// <summary>The subtree in the slot (ts_tree_cursor_current_subtree).</summary>
    internal Subtree Subtree => _legacyOwner is null
        ? SubtreeValue
        : _legacyOwner switch
        {
            Tree t => t.Root,
            Subtree[] children => children[Slot],
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
    // Owner of the node the cursor was initialized with (a tree for the real root,
    // a children array when the cursor starts at a deeper node).
    private object? _rootOwner;

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
        _rootOwner = source._rootOwner;
        RootAliasSymbol = source.RootAliasSymbol;
        Stack.Clear();
        Stack.PushAll(source.Stack);
    }

    /// <summary>ts_tree_cursor_copy</summary>
    public TreeCursor Copy()
    {
        TreeCursor copy = new();
        copy._tree = _tree;
        copy._rootOwner = _rootOwner;
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
            object owner = _rootOwner ?? _tree!;

            if (Stack.Size > 1)
            {
                ref TreeCursorEntry parentEntry = ref Stack.Get(Stack.Size - 2);
                ref SubtreeHeapData parentHeap = ref SubtreeArena.Get(parentEntry.Subtree);
                // The alias and the Node owner both come from this one parent read.
                if (!isExtra)
                {
                    aliasSymbol = _tree!.Language.AliasAt((uint)parentHeap.ProductionId, lastEntry.StructuralChildIndex);
                }

                owner = SubtreeArena.GetChildren(parentHeap.ChildrenHandle);
            }

            return new Node(_tree!, owner, lastEntry.Slot, lastEntry.Position, aliasSymbol);
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

                ref SubtreeHeapData parentHeap = ref SubtreeArena.Get(parentEntry.Subtree);
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
        _rootOwner = node.Owner;
        RootAliasSymbol = node.Alias;
        Stack.Clear();
        Stack.Push(new TreeCursorEntry
        {
            SubtreeValue = node.Subtree,
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

            if (iterator.ChildVisibleChildCount > 0)
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
            object owner = _rootOwner ?? _tree!;

            if (i > 0)
            {
                ref TreeCursorEntry parentEntry = ref Stack.Get((uint)(i - 1));
                ref SubtreeHeapData parentHeap = ref SubtreeArena.Get(parentEntry.Subtree);
                aliasSymbol = _tree!.Language.AliasAt((uint)parentHeap.ProductionId, entry.StructuralChildIndex);
                isVisible = aliasSymbol != 0 || entry.Subtree.Visible;
                if (isVisible)
                {
                    owner = SubtreeArena.GetChildren(parentHeap.ChildrenHandle);
                }
            }

            if (isVisible)
            {
                return new Node(_tree!, owner, entry.Slot, entry.Position, aliasSymbol);
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

        int stackTop = (int)Stack.Size - 1;
        if (stackTop <= 0)
        {
            return;
        }

        Language language = _tree!.Language;

        // Read the top stack node once; on subsequent iterations the previous parentEntry
        // becomes entry, so its fields are carried over without another arena lookup.
        Subtree entrySubtree = Stack.Get((uint)stackTop).Subtree;
        bool entryExtra;
        ushort entryRawSymbol;
        if (entrySubtree.IsInline)
        {
            entryExtra = entrySubtree.Extra;
            entryRawSymbol = entrySubtree.Symbol;
        }
        else
        {
            ref SubtreeHeapData entryHeap = ref SubtreeArena.Get(entrySubtree);
            entryExtra = entryHeap.HasFlag(SubtreeFlags.Extra);
            entryRawSymbol = entryHeap.Symbol;
        }

        for (int i = stackTop; i > 0; i--)
        {
            ref TreeCursorEntry entry = ref Stack.Get((uint)i);
            ref TreeCursorEntry parentEntry = ref Stack.Get((uint)(i - 1));

            ref SubtreeHeapData parentHeap = ref SubtreeArena.Get(parentEntry.Subtree);
            ushort parentProductionId = parentHeap.ProductionId;
            ushort[]? aliasSequence = parentProductionId != 0
                ? language.AliasSequenceArray(parentProductionId)
                : null;

            ushort entrySymbol = SubtreeSymbol(entryRawSymbol, entryExtra, aliasSequence, entry.StructuralChildIndex);
            SymbolMetadata entryMetadata = language.GetSymbolMetadata(entrySymbol);
            if (i != stackTop && entryMetadata.Visible)
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
                uint siblingCount = parentHeap.ChildCount;
                uint nextChildIndex = entry.ChildIndex + 1;
                if (nextChildIndex < siblingCount)
                {
                    Subtree[] siblings = SubtreeArena.GetChildren(parentHeap.ChildrenHandle);
                    uint structuralChildIndex = entry.StructuralChildIndex;
                    if (!entryExtra)
                    {
                        structuralChildIndex++;
                    }

                    for (uint j = nextChildIndex; j < siblingCount; j++)
                    {
                        Subtree sibling = siblings[(int)j];
                        bool siblingExtra;
                        ushort siblingRawSymbol;
                        uint siblingVisibleChildCount;
                        uint siblingNamedChildCount;
                        if (sibling.IsInline)
                        {
                            siblingExtra = sibling.Extra;
                            siblingRawSymbol = sibling.Symbol;
                            siblingVisibleChildCount = 0;
                            siblingNamedChildCount = 0;
                        }
                        else
                        {
                            ref SubtreeHeapData siblingHeap = ref SubtreeArena.Get(sibling);
                            siblingExtra = siblingHeap.HasFlag(SubtreeFlags.Extra);
                            siblingRawSymbol = siblingHeap.Symbol;
                            if (siblingHeap.ChildCount > 0)
                            {
                                siblingVisibleChildCount = siblingHeap.VisibleChildCount;
                                siblingNamedChildCount = siblingHeap.NamedChildCount;
                            }
                            else
                            {
                                siblingVisibleChildCount = 0;
                                siblingNamedChildCount = 0;
                            }
                        }

                        ushort siblingSymbol = SubtreeSymbol(siblingRawSymbol, siblingExtra, aliasSequence, structuralChildIndex);
                        SymbolMetadata siblingMetadata = language.GetSymbolMetadata(siblingSymbol);

                        if (siblingMetadata.Visible)
                        {
                            hasLaterSiblings = true;
                            if (siblingMetadata.Named)
                            {
                                hasLaterNamedSiblings = true;
                                break;
                            }
                        }
                        else if (siblingVisibleChildCount > 0)
                        {
                            hasLaterSiblings = true;
                            if (siblingNamedChildCount > 0)
                            {
                                hasLaterNamedSiblings = true;
                                break;
                            }
                        }

                        if (!siblingExtra)
                        {
                            structuralChildIndex++;
                        }
                    }
                }
            }

            if (!entryExtra)
            {
                ReadOnlySpan<FieldMapEntry> fieldMap = language.FieldMap(parentProductionId);

                if (fieldId == 0)
                {
                    foreach (ref readonly FieldMapEntry map in fieldMap)
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
                    foreach (ref readonly FieldMapEntry map in fieldMap)
                    {
                        if (map.FieldId == fieldId && map.ChildIndex > entry.StructuralChildIndex)
                        {
                            canHaveLaterSiblingsWithThisField = true;
                            break;
                        }
                    }
                }
            }

            if (i > 1)
            {
                // Next iteration's entry is this iteration's parentEntry (always a heap node).
                entryExtra = parentHeap.HasFlag(SubtreeFlags.Extra);
                entryRawSymbol = parentHeap.Symbol;
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

        if (parentSubtree.IsNull || parentSubtree.IsInline)
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

        ref SubtreeHeapData heap = ref SubtreeArena.Get(parentSubtree);
        uint childCount = heap.ChildCount;
        if (childCount == 0)
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

        ushort[]? aliasSequence = _tree!.Language.AliasSequenceArray(heap.ProductionId);

        uint descendantIndex = lastEntry.DescendantIndex;
        // IsEntryVisible(Stack.Size - 1) for this very entry: its heap data is
        // already in `heap`, so only the alias case needs another arena read.
        bool isVisible = Stack.Size == 1 ||
            heap.HasFlag(SubtreeFlags.Visible) ||
            (!heap.HasFlag(SubtreeFlags.Extra) && Stack.Size > 1 &&
             _tree!.Language.AliasAt(
                 SubtreeArena.Get(Stack.Get(Stack.Size - 2).Subtree).ProductionId,
                 lastEntry.StructuralChildIndex) != 0);
        if (isVisible)
        {
            descendantIndex++;
        }

        return new CursorChildIterator
        {
            Tree = _tree!,
            ParentSubtree = parentSubtree,
            ParentChildren = SubtreeArena.GetChildren(heap.ChildrenHandle),
            ParentChildCount = childCount,
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
            ref SubtreeHeapData parentHeap = ref SubtreeArena.Get(parentEntry.Subtree);
            return _tree!.Language.AliasAt((uint)parentHeap.ProductionId, entry.StructuralChildIndex) != 0;
        }

        return false;
    }

    private static ushort SubtreeSymbol(ushort rawSymbol, bool extra, ushort[]? aliasSequence, uint structuralChildIndex)
    {
        if (!extra && aliasSequence is not null && structuralChildIndex < (uint)aliasSequence.Length && aliasSequence[structuralChildIndex] != 0)
        {
            return aliasSequence[structuralChildIndex];
        }

        return rawSymbol;
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

    // The children of ParentSubtree and their count, read once when the iterator is made.
    internal Subtree[]? ParentChildren;
    internal uint ParentChildCount;
    // VisibleChildCount of the child returned by the last Next step, read with
    // that child's other fields instead of looking the child up again.
    internal uint ChildVisibleChildCount;

    internal bool Done => ChildIndex >= ParentChildCount;

    internal static bool Next(ref CursorChildIterator self, out TreeCursorEntry entry, out bool visible)
    {
        if (self.ParentSubtree.IsNull || self.Done)
        {
            entry = default;
            visible = false;
            return false;
        }

        Subtree[] parentChildren = self.ParentChildren!;
        Subtree child = parentChildren[(int)self.ChildIndex];
        entry = new TreeCursorEntry
        {
            SubtreeValue = child,
            Slot = (int)self.ChildIndex,
            Position = self.Position,
            ChildIndex = self.ChildIndex,
            StructuralChildIndex = self.StructuralChildIndex,
            DescendantIndex = self.DescendantIndex,
        };

        // The fields of the child, read once (each accessor looks the node up again).
        bool extra;
        uint childVisibleDescendantCount;
        uint childVisibleChildCount;
        Length childSize;
        if (child.IsInline)
        {
            visible = child.Visible;
            extra = child.Extra;
            childVisibleDescendantCount = 0;
            childVisibleChildCount = 0;
            childSize = child.Size;
        }
        else
        {
            ref SubtreeHeapData c = ref SubtreeArena.Get(child);
            visible = c.HasFlag(SubtreeFlags.Visible);
            extra = c.HasFlag(SubtreeFlags.Extra);
            childVisibleDescendantCount = c.ChildCount == 0 ? 0 : c.VisibleDescendantCount;
            childVisibleChildCount = c.ChildCount == 0 ? 0 : c.VisibleChildCount;
            childSize = c.Size;
        }

        self.ChildVisibleChildCount = childVisibleChildCount;

        if (!extra)
        {
            if (self.AliasSequence is not null && self.StructuralChildIndex < self.AliasSequence.Length)
            {
                visible |= self.AliasSequence[self.StructuralChildIndex] != 0;
            }

            self.StructuralChildIndex++;
        }

        self.DescendantIndex += childVisibleDescendantCount;
        if (visible)
        {
            self.DescendantIndex++;
        }

        self.Position = Length.Add(self.Position, childSize);
        self.ChildIndex++;

        if (self.ChildIndex < self.ParentChildCount)
        {
            Subtree nextChild = parentChildren[(int)self.ChildIndex];
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

        Subtree parentSubtree = self.ParentSubtree;
        ReadOnlySpan<Subtree> children = parentSubtree.Children;
        Subtree child = children[(int)self.ChildIndex];
        entry = new TreeCursorEntry
        {
            SubtreeValue = child,
            Slot = (int)self.ChildIndex,
            Position = self.Position,
            ChildIndex = self.ChildIndex,
            StructuralChildIndex = self.StructuralChildIndex,
            DescendantIndex = self.DescendantIndex,
        };

        // One heap lookup for the child's fields, as in Next.
        bool extra;
        uint childVisibleChildCount;
        Length childPadding;
        if (child.IsInline)
        {
            visible = child.Visible;
            extra = child.Extra;
            childVisibleChildCount = 0;
            childPadding = child.Padding;
        }
        else
        {
            ref SubtreeHeapData c = ref SubtreeArena.Get(child);
            visible = c.HasFlag(SubtreeFlags.Visible);
            extra = c.HasFlag(SubtreeFlags.Extra);
            childVisibleChildCount = c.ChildCount == 0 ? 0 : c.VisibleChildCount;
            childPadding = c.Padding;
        }

        self.ChildVisibleChildCount = childVisibleChildCount;
        self.Position = LengthBacktrack(self.Position, childPadding);
        self.ChildIndex--;

        if (!extra && self.AliasSequence is not null && self.StructuralChildIndex < self.AliasSequence.Length)
        {
            visible |= self.AliasSequence[self.StructuralChildIndex] != 0;
            if (self.StructuralChildIndex > 0)
            {
                self.StructuralChildIndex--;
            }
        }

        if (self.ChildIndex < parentSubtree.ChildCount)
        {
            Subtree previousChild = children[(int)self.ChildIndex];
            bool previousVisible;
            bool previousExtra;
            uint previousVisibleDescendantCount;
            Length previousSize;
            if (previousChild.IsInline)
            {
                previousVisible = previousChild.Visible;
                previousExtra = previousChild.Extra;
                previousVisibleDescendantCount = 0;
                previousSize = previousChild.Size;
            }
            else
            {
                ref SubtreeHeapData previousHeap = ref SubtreeArena.Get(previousChild);
                previousVisible = previousHeap.HasFlag(SubtreeFlags.Visible);
                previousExtra = previousHeap.HasFlag(SubtreeFlags.Extra);
                previousVisibleDescendantCount = previousHeap.ChildCount == 0 ? 0 : previousHeap.VisibleDescendantCount;
                previousSize = previousHeap.Size;
            }

            if (!previousExtra && self.AliasSequence is not null)
            {
                previousVisible |= self.AliasSequence[self.StructuralChildIndex] != 0;
            }

            self.DescendantIndex -= previousVisibleDescendantCount;
            if (previousVisible)
            {
                self.DescendantIndex--;
            }

            self.Position = LengthBacktrack(self.Position, previousSize);
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

