// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/node.c.
// Rules: docs/ARCHITECTURE.md D2. Each method has the C name above it and the same logic.
// - TSNode.id (address of a Subtree slot) is (Owner, Slot): Owner is the Tree for the root
//   slot (Slot 0) or the children array (Subtree[]) whose [Slot] holds the subtree.
// - TSNode.context[0..3] is StartByte, StartRow, StartColumn, Alias.
// - The null node (ts_node_is_null: id == NULL) is default(Node).
// - ToString() is ts_node_string, byte for byte.

using TreeSitter.Abi;
using TreeSitter.Subtrees;

namespace TreeSitter;

/// <summary>A node of a syntax tree (TSNode). Valid while its tree is alive.</summary>
public readonly struct Node : IEquatable<Node>
{
    internal readonly Tree? TreeOrNull;
    internal readonly object? Owner;
    internal readonly int Slot;
    internal readonly uint StartByteValue;
    internal readonly uint StartRow;
    internal readonly uint StartColumn;
    internal readonly ushort Alias;

    // ts_node_new
    internal Node(Tree tree, object owner, int slot, Length position, ushort alias)
    {
        TreeOrNull = tree;
        Owner = owner;
        Slot = slot;
        StartByteValue = position.Bytes;
        StartRow = position.Extent.Row;
        StartColumn = position.Extent.Column;
        Alias = alias;
    }

    /// <summary>The subtree in the slot (ts_node__subtree).</summary>
    internal Subtree Subtree => Owner switch
    {
        Tree t => t.Root,
        Subtree[] children => children[Slot],
        _ => Subtree.Null
    };

    /// <summary>The tree of the node (TSNode.tree).</summary>
    public Tree Tree => TreeOrNull ?? throw new InvalidOperationException("The node is null.");

    /// <summary>ts_node_type</summary>
    public string Type
    {
        get
        {
            Subtree subtree = Subtree;
            ushort symbol = Alias != 0 ? Alias : subtree.Symbol;
            return Tree.Language.SymbolName(symbol) ?? string.Empty;
        }
    }

    /// <summary>ts_node_symbol</summary>
    public ushort Symbol
    {
        get
        {
            Subtree subtree = Subtree;
            ushort symbol = Alias != 0 ? Alias : subtree.Symbol;
            return Tree.Language.PublicSymbol(symbol);
        }
    }

    /// <summary>ts_node_language</summary>
    public Language Language => Tree.Language;

    /// <summary>ts_node_grammar_type</summary>
    public string GrammarType
    {
        get
        {
            ushort symbol = Subtree.Symbol;
            return Tree.Language.SymbolName(symbol) ?? string.Empty;
        }
    }

    /// <summary>ts_node_grammar_symbol</summary>
    public ushort GrammarSymbol => Subtree.Symbol;

    /// <summary>ts_node_child_count</summary>
    public uint ChildCount => Subtree.ChildCount > 0 ? Subtree.VisibleChildCount : 0;

    /// <summary>ts_node_named_child_count</summary>
    public uint NamedChildCount => Subtree.ChildCount > 0 ? Subtree.NamedChildCount : 0;

    /// <summary>ts_node_start_byte</summary>
    public uint StartByte => StartByteValue;

    /// <summary>ts_node_start_point</summary>
    public Point StartPoint => new(StartRow, StartColumn);

    /// <summary>ts_node_end_byte</summary>
    public uint EndByte => StartByte + Subtree.Size.Bytes;

    /// <summary>ts_node_end_point</summary>
    public Point EndPoint => Point.Add(StartPoint, Subtree.Size.Extent);

    /// <summary>ts_node_string</summary>
    public override string ToString()
    {
        ushort aliasSymbol = Alias;
        return SubtreeOps.ToSExpression(
            Subtree,
            aliasSymbol,
            Tree.Language.GetSymbolMetadata(aliasSymbol).Visible,
            Tree.Language,
            false);
    }

    /// <summary>ts_node_is_null</summary>
    public bool IsNull => Owner is null;

    /// <summary>ts_node_is_named</summary>
    public bool IsNamed
    {
        get
        {
            if (Alias != 0)
            {
                return Tree.Language.GetSymbolMetadata(Alias).Named;
            }

            return Subtree.Named;
        }
    }

    /// <summary>ts_node_is_missing</summary>
    public bool IsMissing => Subtree.IsMissing;

    /// <summary>ts_node_is_extra</summary>
    public bool IsExtra => Subtree.Extra;

    /// <summary>ts_node_has_changes</summary>
    public bool HasChanges => Subtree.HasChanges;

    /// <summary>ts_node_has_error</summary>
    public bool HasError => IsError || Subtree.ErrorCost > 0;

    /// <summary>ts_node_is_error</summary>
    public bool IsError => Symbol == BuiltinSymbols.Error;

    /// <summary>ts_node_parse_state</summary>
    public ushort ParseState => Subtree.ParseState;

    /// <summary>ts_node_next_parse_state</summary>
    public ushort NextParseState
    {
        get
        {
            ushort state = ParseState;
            if (state == ushort.MaxValue) // TS_TREE_STATE_NONE
            {
                return ushort.MaxValue;
            }

            ushort symbol = GrammarSymbol;
            return Tree.Language.NextState(state, symbol);
        }
    }

    /// <summary>ts_node_parent</summary>
    public Node Parent
    {
        get
        {
            Node node = Tree.RootNode;
            if (node.IdEquals(this))
            {
                return default;
            }

            while (true)
            {
                Node nextNode = node.ChildWithDescendant(this);
                if (nextNode.IdEquals(this) || nextNode.IsNull)
                {
                    break;
                }

                node = nextNode;
            }

            return node;
        }
    }

    /// <summary>ts_node_child_with_descendant</summary>
    public Node ChildWithDescendant(Node descendant)
    {
        uint startByte = descendant.StartByte;
        uint endByte = descendant.EndByte;
        bool isEmpty = startByte == endByte;

        Node self = this;
        do
        {
            ChildIterator iter = new(self);
            do
            {
                if (!iter.Next(out self) || self.StartByte > startByte)
                {
                    return default;
                }

                if (self.IdEquals(descendant))
                {
                    return self;
                }

                // If the descendant is empty, and the end byte is within `self`,
                // we check whether `self` contains it or not.
                if (isEmpty && iter.Position.Bytes >= endByte && self.ChildCount > 0)
                {
                    Node child = self.ChildWithDescendant(descendant);

                    // If the child is not null, return self if it's relevant, else return the child
                    if (!child.IsNull)
                    {
                        return IsRelevant(self, true) ? self : child;
                    }
                }
            }
            while ((isEmpty ? iter.Position.Bytes <= endByte : iter.Position.Bytes < endByte) || self.ChildCount == 0);
        }
        while (!IsRelevant(self, true));

        return self;
    }

    /// <summary>ts_node_child</summary>
    public Node Child(uint index) => ChildCore(index, true);

    /// <summary>ts_node_named_child</summary>
    public Node NamedChild(uint index) => ChildCore(index, false);

    // ts_node__child
    private Node ChildCore(uint childIndex, bool includeAnonymous)
    {
        Node result = this;
        bool didDescend = true;

        while (didDescend)
        {
            didDescend = false;

            uint index = 0;
            ChildIterator iterator = new(result);
            while (iterator.Next(out Node child))
            {
                if (IsRelevant(child, includeAnonymous))
                {
                    if (index == childIndex)
                    {
                        return child;
                    }

                    index++;
                }
                else
                {
                    uint grandchildIndex = childIndex - index;
                    uint grandchildCount = RelevantChildCount(child, includeAnonymous);
                    if (grandchildIndex < grandchildCount)
                    {
                        didDescend = true;
                        result = child;
                        childIndex = grandchildIndex;
                        break;
                    }

                    index += grandchildCount;
                }
            }
        }

        return default;
    }

    /// <summary>ts_node_field_name_for_child</summary>
    public string? FieldNameForChild(uint index) => FieldNameForChild(index, true);

    /// <summary>ts_node_field_name_for_named_child</summary>
    public string? FieldNameForNamedChild(uint index) => FieldNameForChild(index, false);

    private string? FieldNameForChild(uint childIndex, bool includeAnonymous)
    {
        Node result = this;
        bool didDescend = true;
        string? inheritedFieldName = null;

        while (didDescend)
        {
            didDescend = false;
            uint index2 = 0;
            Node child;
            ChildIterator iterator = new(result);
            while (iterator.Next(out child))
            {
                if (IsRelevant(child, includeAnonymous))
                {
                    if (index2 == childIndex)
                    {
                        if (child.IsExtra)
                        {
                            return null;
                        }

                        string? fieldName = FieldNameFromLanguage(result, (uint)(iterator.StructuralChildIndex - 1));
                        if (fieldName != null)
                        {
                            return fieldName;
                        }

                        return inheritedFieldName;
                    }

                    index2++;
                }
                else
                {
                    uint grandchildIndex = childIndex - index2;
                    uint grandchildCount = RelevantChildCount(child, includeAnonymous);
                    if (grandchildIndex < grandchildCount)
                    {
                        string? fieldName = FieldNameFromLanguage(result, (uint)(iterator.StructuralChildIndex - 1));
                        if (fieldName != null)
                        {
                            inheritedFieldName = fieldName;
                        }

                        didDescend = true;
                        result = child;
                        childIndex = grandchildIndex;
                        break;
                    }

                    index2 += grandchildCount;
                }
            }
        }

        return null;
    }

    /// <summary>ts_node_child_by_field_name</summary>
    public Node ChildByFieldName(string name)
    {
        ushort fieldId = Tree.Language.FieldIdForName(name);
        return ChildByFieldId(fieldId);
    }

    /// <summary>ts_node_child_by_field_id</summary>
    public Node ChildByFieldId(ushort fieldId)
    {
        Node self = this;
        for (;;)
        {
            if (fieldId == 0 || self.ChildCount == 0)
            {
                return default;
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(self.Subtree);
            ReadOnlySpan<FieldMapEntry> fieldMap = self.Tree.Language.FieldMap(heap.ProductionId);
            if (fieldMap.IsEmpty)
            {
                return default;
            }

            // Narrow to the entries whose field id matches.
            int lo = 0;
            while (lo < fieldMap.Length && fieldMap[lo].FieldId < fieldId)
            {
                lo++;
            }

            if (lo == fieldMap.Length)
            {
                return default;
            }

            int hi = fieldMap.Length;
            while (hi > lo && fieldMap[hi - 1].FieldId > fieldId)
            {
                hi--;
            }

            if (lo == hi)
            {
                return default;
            }

            Node child;
            ChildIterator iterator = new(self);
            int mapIndex = lo;
            bool recursed = false;
            while (iterator.Next(out child))
            {
                if (!child.IsExtra)
                {
                    uint index = (uint)(iterator.StructuralChildIndex - 1);
                    if (index < fieldMap[mapIndex].ChildIndex)
                    {
                        continue;
                    }

                    if (fieldMap[mapIndex].Inherited)
                    {
                        if (mapIndex + 1 == hi)
                        {
                            // Tail call: re-enter with self = child.
                            self = child;
                            recursed = true;
                            break;
                        }

                        Node result = child.ChildByFieldId(fieldId);
                        if (!result.IsNull)
                        {
                            return result;
                        }

                        mapIndex++;
                        if (mapIndex == hi)
                        {
                            return default;
                        }
                    }
                    else if (IsRelevant(child, true))
                    {
                        return child;
                    }
                    else if (child.ChildCount > 0)
                    {
                        return child.Child(0);
                    }
                    else
                    {
                        mapIndex++;
                        if (mapIndex == hi)
                        {
                            return default;
                        }
                    }
                }
            }

            if (recursed)
            {
                continue;
            }

            return default;
        }
    }

    /// <summary>ts_node_next_sibling</summary>
    public Node NextSibling => NextSiblingCore(true);

    /// <summary>ts_node_next_named_sibling</summary>
    public Node NextNamedSibling => NextSiblingCore(false);

    // ts_node__next_sibling
    private Node NextSiblingCore(bool includeAnonymous)
    {
        uint targetEndByte = EndByte;

        Node node = Parent;
        Node laterNode = default;
        bool laterNodeIsRelevant = false;

        while (!node.IsNull)
        {
            Node laterChild = default;
            bool laterChildIsRelevant = false;
            Node childContainingTarget = default;

            Node child;
            ChildIterator iterator = new(node);
            while (iterator.Next(out child))
            {
                if ((uint)iterator.Position.Bytes <= targetEndByte)
                {
                    continue;
                }

                uint startByte = StartByte;
                uint childStartByte = child.StartByte;

                bool isEmpty = startByte == targetEndByte;
                bool containsTarget = isEmpty
                    ? childStartByte < startByte
                    : childStartByte <= startByte;

                if (containsTarget)
                {
                    if (!child.Subtree.Equals(Subtree))
                    {
                        childContainingTarget = child;
                    }
                }
                else
                {
                    if (IsRelevant(child, includeAnonymous))
                    {
                        laterChild = child;
                        laterChildIsRelevant = true;
                        break;
                    }
                    else if (RelevantChildCount(child, includeAnonymous) > 0)
                    {
                        laterChild = child;
                        laterChildIsRelevant = false;
                        break;
                    }
                }
            }

            if (!childContainingTarget.IsNull)
            {
                if (!laterChild.IsNull)
                {
                    laterNode = laterChild;
                    laterNodeIsRelevant = laterChildIsRelevant;
                }

                node = childContainingTarget;
            }
            else if (laterChildIsRelevant)
            {
                return laterChild;
            }
            else if (!laterChild.IsNull)
            {
                node = laterChild;
            }
            else if (laterNodeIsRelevant)
            {
                return laterNode;
            }
            else
            {
                node = laterNode;
            }
        }

        return default;
    }

    /// <summary>ts_node_prev_sibling</summary>
    public Node PrevSibling => PrevSiblingCore(true);

    /// <summary>ts_node_prev_named_sibling</summary>
    public Node PrevNamedSibling => PrevSiblingCore(false);

    // ts_node__prev_sibling
    private Node PrevSiblingCore(bool includeAnonymous)
    {
        Subtree selfSubtree = Subtree;
        bool selfIsEmpty = selfSubtree.TotalBytes == 0;
        uint targetEndByte = EndByte;

        Node node = Parent;
        Node earlierNode = default;
        bool earlierNodeIsRelevant = false;

        while (!node.IsNull)
        {
            Node earlierChild = default;
            bool earlierChildIsRelevant = false;
            bool foundChildContainingTarget = false;

            Node child;
            ChildIterator iterator = new(node);
            while (iterator.Next(out child))
            {
                if (child.IdEquals(this))
                {
                    break;
                }

                if ((uint)iterator.Position.Bytes > targetEndByte)
                {
                    foundChildContainingTarget = true;
                    break;
                }

                if ((uint)iterator.Position.Bytes == targetEndByte &&
                    (!selfIsEmpty || HasTrailingEmptyDescendant(child.Subtree, selfSubtree)))
                {
                    foundChildContainingTarget = true;
                    break;
                }

                if (IsRelevant(child, includeAnonymous))
                {
                    earlierChild = child;
                    earlierChildIsRelevant = true;
                }
                else if (RelevantChildCount(child, includeAnonymous) > 0)
                {
                    earlierChild = child;
                    earlierChildIsRelevant = false;
                }
            }

            if (foundChildContainingTarget)
            {
                if (!earlierChild.IsNull)
                {
                    earlierNode = earlierChild;
                    earlierNodeIsRelevant = earlierChildIsRelevant;
                }

                node = child;
            }
            else if (earlierChildIsRelevant)
            {
                return earlierChild;
            }
            else if (!earlierChild.IsNull)
            {
                node = earlierChild;
            }
            else if (earlierNodeIsRelevant)
            {
                return earlierNode;
            }
            else
            {
                node = earlierNode;
                earlierNode = default;
                earlierNodeIsRelevant = false;
            }
        }

        return default;
    }

    /// <summary>ts_node_first_child_for_byte</summary>
    public Node FirstChildForByte(uint startByte) => FirstChildForByte(startByte, true);

    /// <summary>ts_node_first_named_child_for_byte</summary>
    public Node FirstNamedChildForByte(uint startByte) => FirstChildForByte(startByte, false);

    private Node FirstChildForByte(uint goal, bool includeAnonymous)
    {
        Node node = this;
        bool didDescend = true;

        ChildIterator lastIterator = default;
        bool hasLastIterator = false;

        while (didDescend)
        {
            didDescend = false;

            Node child;
            ChildIterator iterator = new(node);
        loop:
            while (iterator.Next(out child))
            {
                if (child.EndByte > goal)
                {
                    if (IsRelevant(child, includeAnonymous))
                    {
                        return child;
                    }

                    if (child.ChildCount > 0)
                    {
                        if (!iterator.Done)
                        {
                            lastIterator = iterator;
                            hasLastIterator = true;
                        }

                        didDescend = true;
                        node = child;
                        break;
                    }
                }
            }

            if (!didDescend && hasLastIterator)
            {
                iterator = lastIterator;
                hasLastIterator = false;
                goto loop;
            }
        }

        return default;
    }

    /// <summary>ts_node_descendant_count</summary>
    public uint DescendantCount => Subtree.VisibleDescendantCount + 1;

    /// <summary>ts_node_descendant_for_byte_range</summary>
    public Node DescendantForByteRange(uint start, uint end) => DescendantForByteRange(start, end, true);

    /// <summary>ts_node_named_descendant_for_byte_range</summary>
    public Node NamedDescendantForByteRange(uint start, uint end) => DescendantForByteRange(start, end, false);

    private Node DescendantForByteRange(uint rangeStart, uint rangeEnd, bool includeAnonymous)
    {
        if (rangeStart > rangeEnd)
        {
            return default;
        }

        Node node = this;
        Node lastVisibleNode = this;

        bool didDescend = true;
        while (didDescend)
        {
            didDescend = false;

            Node child;
            ChildIterator iterator = new(node);
            while (iterator.Next(out child))
            {
                uint nodeEnd = (uint)iterator.Position.Bytes;

                if (nodeEnd < rangeEnd)
                {
                    continue;
                }

                bool isEmpty = child.StartByte == nodeEnd;
                if (isEmpty ? nodeEnd < rangeStart : nodeEnd <= rangeStart)
                {
                    continue;
                }

                if (rangeStart < child.StartByte)
                {
                    break;
                }

                node = child;
                if (IsRelevant(node, includeAnonymous))
                {
                    lastVisibleNode = node;
                }

                didDescend = true;
                break;
            }
        }

        return lastVisibleNode;
    }

    /// <summary>ts_node_descendant_for_point_range</summary>
    public Node DescendantForPointRange(Point start, Point end) => DescendantForPointRange(start, end, true);

    /// <summary>ts_node_named_descendant_for_point_range</summary>
    public Node NamedDescendantForPointRange(Point start, Point end) => DescendantForPointRange(start, end, false);

    private Node DescendantForPointRange(Point rangeStart, Point rangeEnd, bool includeAnonymous)
    {
        if (Point.Gt(rangeStart, rangeEnd))
        {
            return default;
        }

        Node node = this;
        Node lastVisibleNode = this;

        bool didDescend = true;
        while (didDescend)
        {
            didDescend = false;

            Node child;
            ChildIterator iterator = new(node);
            while (iterator.Next(out child))
            {
                Point nodeEnd = iterator.Position.Extent;

                if (Point.Lt(nodeEnd, rangeEnd))
                {
                    continue;
                }

                bool isEmpty = child.StartPoint == nodeEnd;
                if (isEmpty ? Point.Lt(nodeEnd, rangeStart) : Point.Lte(nodeEnd, rangeStart))
                {
                    continue;
                }

                if (Point.Lt(rangeStart, child.StartPoint))
                {
                    break;
                }

                node = child;
                if (IsRelevant(node, includeAnonymous))
                {
                    lastVisibleNode = node;
                }

                didDescend = true;
                break;
            }
        }

        return lastVisibleNode;
    }

    /// <summary>
    /// ts_node_edit: the node with its start moved by the edit. C changes the TSNode in
    /// place; the managed node is immutable, so the edited node is returned.
    /// </summary>
    public Node Edit(InputEdit edit)
    {
        uint startByte = StartByte;
        Point startPoint = StartPoint;

        TreeSitter.Parsing.ChangedRanges.PointEdit(ref startPoint, ref startByte, edit);

        return new Node(TreeOrNull!, Owner!, Slot, new Length(startByte, startPoint), Alias);
    }

    /// <summary>ts_node_eq: same tree and same subtree slot.</summary>
    public bool Equals(Node other) =>
        ReferenceEquals(TreeOrNull, other.TreeOrNull) && ReferenceEquals(Owner, other.Owner) && Slot == other.Slot;

    // self.id == other.id
    internal bool IdEquals(Node other) => ReferenceEquals(Owner, other.Owner) && Slot == other.Slot;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Node other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(TreeOrNull, Owner, Slot);

    /// <summary>ts_node_eq</summary>
    public static bool operator ==(Node left, Node right) => left.Equals(right);

    /// <summary>!ts_node_eq</summary>
    public static bool operator !=(Node left, Node right) => !left.Equals(right);

    // ------------------------------------------------------------------
    // Private helpers (ports of the static helpers in node.c)

    private static bool IsRelevant(Node self, bool includeAnonymous)
    {
        Subtree subtree = self.Subtree;
        if (includeAnonymous)
        {
            return subtree.Visible || self.Alias != 0;
        }

        if (self.Alias != 0)
        {
            return self.Tree.Language.GetSymbolMetadata(self.Alias).Named;
        }

        return subtree.Visible && subtree.Named;
    }

    private static uint RelevantChildCount(Node self, bool includeAnonymous)
    {
        Subtree subtree = self.Subtree;
        if (subtree.ChildCount > 0)
        {
            return includeAnonymous ? subtree.VisibleChildCount : subtree.NamedChildCount;
        }

        return 0;
    }

    // ts_subtree_has_trailing_empty_descendant
    private static bool HasTrailingEmptyDescendant(Subtree self, Subtree other)
    {
        for (uint i = self.ChildCount - 1; i + 1 > 0; i--)
        {
            Subtree child = self.Children![(int)i];
            if (child.TotalBytes > 0)
            {
                break;
            }

            if (child.Equals(other) || HasTrailingEmptyDescendant(child, other))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FieldNameFromLanguage(Node self, uint structuralChildIndex)
    {
        ref SubtreeHeapData heap = ref SubtreeArena.Get(self.Subtree);
        ReadOnlySpan<FieldMapEntry> fieldMap = self.Tree.Language.FieldMap(heap.ProductionId);

        foreach (ref readonly FieldMapEntry map in fieldMap)
        {
            if (!map.Inherited && map.ChildIndex == structuralChildIndex)
            {
                return self.Tree.Language.FieldNameForId(map.FieldId);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Child iterator (ports NodeChildIterator from node.c)

    private struct ChildIterator
    {
        internal readonly Node Parent;
        internal readonly Subtree ParentSubtree;
        internal readonly Language Language;
        internal Length Position;
        internal uint ChildIndex;
        internal uint StructuralChildIndex;
        internal readonly ushort[]? AliasSequence;

        internal ChildIterator(Node parent)
        {
            Parent = parent;
            ParentSubtree = parent.Subtree;
            Language = parent.TreeOrNull!.Language;
            Position = new Length(parent.StartByteValue, new Point(parent.StartRow, parent.StartColumn));
            ChildIndex = 0;
            StructuralChildIndex = 0;
            AliasSequence = null;

            if (ParentSubtree.IsNull || ParentSubtree.ChildCount == 0)
            {
                return;
            }

            ref SubtreeHeapData heap = ref SubtreeArena.Get(ParentSubtree);
            AliasSequence = Language.AliasSequenceArray(heap.ProductionId);
        }

        internal bool Done => ChildIndex >= ParentSubtree.ChildCount;

        internal bool Next(out Node child)
        {
            if (ParentSubtree.IsNull || Done)
            {
                child = default;
                return false;
            }

            Subtree childSubtree = ParentSubtree.Children![(int)ChildIndex];
            ushort aliasSymbol = 0;

            if (!childSubtree.Extra)
            {
                if (AliasSequence is not null && StructuralChildIndex < AliasSequence.Length)
                {
                    aliasSymbol = AliasSequence[StructuralChildIndex];
                }

                StructuralChildIndex++;
            }

            if (ChildIndex > 0)
            {
                Position = Length.Add(Position, childSubtree.Padding);
            }

            child = new Node(
                Parent.TreeOrNull!,
                SubtreeArena.GetChildren(SubtreeArena.Get(ParentSubtree).ChildrenHandle),
                (int)ChildIndex,
                Position,
                aliasSymbol);

            Position = Length.Add(Position, childSubtree.Size);
            ChildIndex++;
            return true;
        }
    }
}
