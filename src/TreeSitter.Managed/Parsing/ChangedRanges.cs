// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/get_changed_ranges.c (ts_range_edit,
// Iterator, ts_subtree_get_changed_ranges) and ts_point_edit of lib/src/point.c.
// The TSRangeArray functions of the same file are in RangeArray (ReusableNode.cs).
// - The iterator walks TreeCursorEntry like the C code: a `const Subtree *` is the
//   (Owner, Slot) pair of the entry, Owner is the Tree for the root.

using TreeSitter.Subtrees;

namespace TreeSitter.Parsing;

/// <summary>get_changed_ranges.c</summary>
internal static class ChangedRanges
{
    /// <summary>POINT_MAX</summary>
    private static readonly Point PointMax = new Point(uint.MaxValue, uint.MaxValue);

    /// <summary>ts_point_edit</summary>
    internal static void PointEdit(ref Point point, ref uint startByteRef, in InputEdit edit)
    {
        uint startByte = startByteRef;
        Point startPoint = point;

        if (startByte >= edit.OldEndByte)
        {
            startByte = edit.NewEndByte + (startByte - edit.OldEndByte);
            startPoint = Point.Add(edit.NewEndPoint, Point.Sub(startPoint, edit.OldEndPoint));
        }
        else if (startByte > edit.StartByte)
        {
            startByte = edit.NewEndByte;
            startPoint = edit.NewEndPoint;
        }

        point = startPoint;
        startByteRef = startByte;
    }

    /// <summary>ts_range_edit</summary>
    internal static Range RangeEdit(Range range, in InputEdit edit)
    {
        uint startByte = range.StartByte;
        Point startPoint = range.StartPoint;
        uint endByte = range.EndByte;
        Point endPoint = range.EndPoint;

        if (endByte >= edit.OldEndByte)
        {
            if (endByte != uint.MaxValue)
            {
                endByte = edit.NewEndByte + (endByte - edit.OldEndByte);
                endPoint = Point.Add(
                    edit.NewEndPoint,
                    Point.Sub(endPoint, edit.OldEndPoint));
                if (endByte < edit.NewEndByte)
                {
                    endByte = uint.MaxValue;
                    endPoint = PointMax;
                }
            }
        }
        else if (endByte > edit.StartByte)
        {
            endByte = edit.StartByte;
            endPoint = edit.StartPoint;
        }

        if (startByte >= edit.OldEndByte)
        {
            startByte = edit.NewEndByte + (startByte - edit.OldEndByte);
            startPoint = Point.Add(
                edit.NewEndPoint,
                Point.Sub(startPoint, edit.OldEndPoint));
            if (startByte < edit.NewEndByte)
            {
                startByte = uint.MaxValue;
                startPoint = PointMax;
            }
        }
        else if (startByte > edit.StartByte)
        {
            startByte = edit.StartByte;
            startPoint = edit.StartPoint;
        }

        return new Range(startPoint, endPoint, startByte, endByte);
    }

    /// <summary>Iterator</summary>
    private struct Iterator
    {
        internal TsArray<TreeCursorEntry> Stack;
        internal Language Language;
        internal uint VisibleDepth;
        internal bool InPadding;
        internal Subtree PrevExternalToken;
    }

    /// <summary>IteratorComparison</summary>
    private enum IteratorComparison
    {
        Differs,
        MayDiffer,
        Matches,
    }

    /// <summary>iterator_new</summary>
    private static Iterator IteratorNew(Tree tree, Language language)
    {
        TsArray<TreeCursorEntry> stack = new TsArray<TreeCursorEntry>();
        stack.Push(new TreeCursorEntry
        {
            Owner = tree,
            Slot = 0,
            Position = Length.Zero,
            ChildIndex = 0,
            StructuralChildIndex = 0,
        });
        return new Iterator
        {
            Stack = stack,
            Language = language,
            VisibleDepth = 1,
            InPadding = false,
            PrevExternalToken = Subtree.Null,
        };
    }

    /// <summary>iterator_done</summary>
    private static bool IteratorDone(in Iterator self) => self.Stack.Size == 0;

    /// <summary>iterator_start_position</summary>
    private static Length IteratorStartPosition(in Iterator self)
    {
        TreeCursorEntry entry = self.Stack.Contents![self.Stack.Size - 1];
        if (self.InPadding)
        {
            return entry.Position;
        }
        else
        {
            return Length.Add(entry.Position, entry.Subtree.Padding);
        }
    }

    /// <summary>iterator_end_position</summary>
    private static Length IteratorEndPosition(in Iterator self)
    {
        TreeCursorEntry entry = self.Stack.Contents![self.Stack.Size - 1];
        Length result = Length.Add(entry.Position, entry.Subtree.Padding);
        if (self.InPadding)
        {
            return result;
        }
        else
        {
            return Length.Add(result, entry.Subtree.Size);
        }
    }

    /// <summary>iterator_tree_is_visible</summary>
    private static bool IteratorTreeIsVisible(in Iterator self)
    {
        TreeCursorEntry entry = self.Stack.Contents![self.Stack.Size - 1];
        if (entry.Subtree.Visible)
        {
            return true;
        }

        if (self.Stack.Size > 1)
        {
            Subtree parent = self.Stack.Contents[self.Stack.Size - 2].Subtree;
            return self.Language.AliasAt(
                parent.Heap!.ProductionId,
                entry.StructuralChildIndex) != 0;
        }

        return false;
    }

    /// <summary>iterator_get_visible_state</summary>
    private static void IteratorGetVisibleState(
        in Iterator self,
        ref Subtree tree,
        ref ushort aliasSymbol,
        ref uint startByte)
    {
        uint i = self.Stack.Size - 1;

        if (self.InPadding)
        {
            if (i == 0)
            {
                return;
            }

            i--;
        }

        for (; i + 1 > 0; i--)
        {
            TreeCursorEntry entry = self.Stack.Contents![i];

            if (i > 0)
            {
                Subtree parent = self.Stack.Contents[i - 1].Subtree;
                aliasSymbol = self.Language.AliasAt(
                    parent.Heap!.ProductionId,
                    entry.StructuralChildIndex);
            }

            if (entry.Subtree.Visible || aliasSymbol != 0)
            {
                tree = entry.Subtree;
                startByte = entry.Position.Bytes;
                break;
            }
        }
    }

    /// <summary>iterator_ascend</summary>
    private static void IteratorAscend(ref Iterator self)
    {
        if (IteratorDone(self))
        {
            return;
        }

        if (IteratorTreeIsVisible(self) && !self.InPadding)
        {
            self.VisibleDepth--;
        }

        if (self.Stack.Back().ChildIndex > 0)
        {
            self.InPadding = false;
        }

        self.Stack.Size--;
    }

    /// <summary>iterator_descend</summary>
    private static bool IteratorDescend(ref Iterator self, uint goalPosition)
    {
        if (self.InPadding)
        {
            return false;
        }

        bool didDescend;
        do
        {
            didDescend = false;
            TreeCursorEntry entry = self.Stack.Back();
            Subtree entrySubtree = entry.Subtree;
            Length position = entry.Position;
            uint structuralChildIndex = 0;
            for (uint i = 0, n = entrySubtree.ChildCount; i < n; i++)
            {
                Subtree child = entrySubtree.Children[(int)i];
                Length childLeft = Length.Add(position, child.Padding);
                Length childRight = Length.Add(childLeft, child.Size);

                if (childRight.Bytes > goalPosition)
                {
                    self.Stack.Push(new TreeCursorEntry
                    {
                        Owner = entrySubtree.Heap,
                        Slot = (int)i,
                        Position = position,
                        ChildIndex = i,
                        StructuralChildIndex = structuralChildIndex,
                    });

                    if (IteratorTreeIsVisible(self))
                    {
                        if (childLeft.Bytes > goalPosition)
                        {
                            self.InPadding = true;
                        }
                        else
                        {
                            self.VisibleDepth++;
                        }

                        return true;
                    }

                    didDescend = true;
                    break;
                }

                position = childRight;
                if (!child.Extra)
                {
                    structuralChildIndex++;
                }

                Subtree lastExternalToken = SubtreeOps.LastExternalToken(child);
                if (!lastExternalToken.IsNull)
                {
                    self.PrevExternalToken = lastExternalToken;
                }
            }
        }
        while (didDescend);

        return false;
    }

    /// <summary>iterator_advance</summary>
    private static void IteratorAdvance(ref Iterator self)
    {
        if (self.InPadding)
        {
            self.InPadding = false;
            if (IteratorTreeIsVisible(self))
            {
                self.VisibleDepth++;
            }
            else
            {
                IteratorDescend(ref self, 0);
            }

            return;
        }

        for (;;)
        {
            if (IteratorTreeIsVisible(self))
            {
                self.VisibleDepth--;
            }

            TreeCursorEntry entry = self.Stack.Pop();
            if (IteratorDone(self))
            {
                return;
            }

            Subtree parent = self.Stack.Back().Subtree;
            uint childIndex = entry.ChildIndex + 1;
            Subtree lastExternalToken = SubtreeOps.LastExternalToken(entry.Subtree);
            if (!lastExternalToken.IsNull)
            {
                self.PrevExternalToken = lastExternalToken;
            }

            if (parent.ChildCount > childIndex)
            {
                Length position = Length.Add(entry.Position, entry.Subtree.TotalSize);
                uint structuralChildIndex = entry.StructuralChildIndex;
                if (!entry.Subtree.Extra)
                {
                    structuralChildIndex++;
                }

                Subtree nextChild = parent.Children[(int)childIndex];

                self.Stack.Push(new TreeCursorEntry
                {
                    Owner = parent.Heap,
                    Slot = (int)childIndex,
                    Position = position,
                    ChildIndex = childIndex,
                    StructuralChildIndex = structuralChildIndex,
                });

                if (IteratorTreeIsVisible(self))
                {
                    if (nextChild.Padding.Bytes > 0)
                    {
                        self.InPadding = true;
                    }
                    else
                    {
                        self.VisibleDepth++;
                    }
                }
                else
                {
                    IteratorDescend(ref self, 0);
                }

                break;
            }
        }
    }

    /// <summary>iterator_compare</summary>
    private static IteratorComparison IteratorCompare(in Iterator oldIter, in Iterator newIter)
    {
        Subtree oldTree = Subtree.Null;
        Subtree newTree = Subtree.Null;
        uint oldStart = 0;
        uint newStart = 0;
        ushort oldAliasSymbol = 0;
        ushort newAliasSymbol = 0;
        IteratorGetVisibleState(oldIter, ref oldTree, ref oldAliasSymbol, ref oldStart);
        IteratorGetVisibleState(newIter, ref newTree, ref newAliasSymbol, ref newStart);

        // C reads the symbols before the NULL checks; a NULL subtree has no symbol here,
        // so they are read after the checks.
        if (oldTree.IsNull && newTree.IsNull)
        {
            return IteratorComparison.Matches;
        }

        if (oldTree.IsNull || newTree.IsNull)
        {
            return IteratorComparison.Differs;
        }

        ushort oldSymbol = oldTree.Symbol;
        ushort newSymbol = newTree.Symbol;
        if (oldAliasSymbol != newAliasSymbol || oldSymbol != newSymbol)
        {
            return IteratorComparison.Differs;
        }

        uint oldSize = oldTree.Size.Bytes;
        uint newSize = newTree.Size.Bytes;
        ushort oldState = oldTree.ParseState;
        ushort newState = newTree.ParseState;
        bool oldHasExternalTokens = oldTree.HasExternalTokens;
        bool newHasExternalTokens = newTree.HasExternalTokens;
        uint oldErrorCost = oldTree.ErrorCost;
        uint newErrorCost = newTree.ErrorCost;

        if (
            oldStart != newStart ||
            oldSymbol == BuiltinSymbols.Error ||
            oldSize != newSize ||
            oldState == ushort.MaxValue ||
            newState == ushort.MaxValue ||
            ((oldState == ErrorCosts.ErrorState) != (newState == ErrorCosts.ErrorState)) ||
            oldErrorCost != newErrorCost ||
            oldHasExternalTokens != newHasExternalTokens ||
            oldTree.HasChanges ||
            (
                oldHasExternalTokens &&
                !SubtreeOps.ExternalScannerStateEq(oldIter.PrevExternalToken, newIter.PrevExternalToken)))
        {
            return IteratorComparison.MayDiffer;
        }

        return IteratorComparison.Matches;
    }

    /// <summary>ts_range_array_add</summary>
    private static void RangeArrayAdd(ref TsArray<Range> self, Length start, Length end)
    {
        if (self.Size > 0)
        {
            ref Range lastRange = ref self.Back();
            if (start.Bytes <= lastRange.EndByte)
            {
                lastRange = lastRange with { EndByte = end.Bytes, EndPoint = end.Extent };
                return;
            }
        }

        if (start.Bytes < end.Bytes)
        {
            self.Push(new Range(start.Extent, end.Extent, start.Bytes, end.Bytes));
        }
    }

    /// <summary>ts_tree_get_changed_ranges and ts_subtree_get_changed_ranges</summary>
    internal static Range[] Get(Tree oldTree, Tree newTree)
    {
        TsArray<Range> includedRangeDifferences = new TsArray<Range>();
        RangeArray.GetChangedRanges(
            oldTree.IncludedRangesSpan,
            newTree.IncludedRangesSpan,
            ref includedRangeDifferences);

        Language language = oldTree.Language;
        TsArray<Range> results = new TsArray<Range>();

        Iterator oldIter = IteratorNew(oldTree, language);
        Iterator newIter = IteratorNew(newTree, language);

        uint includedRangeDifferenceIndex = 0;

        Length position = IteratorStartPosition(oldIter);
        Length nextPosition = IteratorStartPosition(newIter);
        if (position.Bytes < nextPosition.Bytes)
        {
            RangeArrayAdd(ref results, position, nextPosition);
            position = nextPosition;
        }
        else if (position.Bytes > nextPosition.Bytes)
        {
            RangeArrayAdd(ref results, nextPosition, position);
            nextPosition = position;
        }

        do
        {
            // Compare the old and new subtrees.
            IteratorComparison comparison = IteratorCompare(oldIter, newIter);

            // Even if the two subtrees appear to be identical, they could differ
            // internally if they contain a range of text that was previously
            // excluded from the parse, and is now included, or vice-versa.
            if (comparison == IteratorComparison.Matches && RangeArray.Intersects(
                includedRangeDifferences,
                includedRangeDifferenceIndex,
                position.Bytes,
                IteratorEndPosition(oldIter).Bytes))
            {
                comparison = IteratorComparison.MayDiffer;
            }

            bool isChanged = false;
            switch (comparison)
            {
                // If the subtrees are definitely identical, move to the end
                // of both subtrees.
                case IteratorComparison.Matches:
                    nextPosition = IteratorEndPosition(oldIter);
                    break;

                // If the subtrees might differ internally, descend into both
                // subtrees, finding the first child that spans the current position.
                case IteratorComparison.MayDiffer:
                    if (IteratorDescend(ref oldIter, position.Bytes))
                    {
                        if (!IteratorDescend(ref newIter, position.Bytes))
                        {
                            isChanged = true;
                            nextPosition = IteratorEndPosition(oldIter);
                        }
                    }
                    else if (IteratorDescend(ref newIter, position.Bytes))
                    {
                        isChanged = true;
                        nextPosition = IteratorEndPosition(newIter);
                    }
                    else
                    {
                        nextPosition = Length.Min(
                            IteratorEndPosition(oldIter),
                            IteratorEndPosition(newIter));
                    }

                    break;

                // If the subtrees are different, record a change and then move
                // to the end of both subtrees.
                case IteratorComparison.Differs:
                    isChanged = true;
                    nextPosition = Length.Min(
                        IteratorEndPosition(oldIter),
                        IteratorEndPosition(newIter));
                    break;
            }

            // Ensure that both iterators are caught up to the current position.
            while (
                !IteratorDone(oldIter) &&
                IteratorEndPosition(oldIter).Bytes <= nextPosition.Bytes)
            {
                IteratorAdvance(ref oldIter);
            }

            while (
                !IteratorDone(newIter) &&
                IteratorEndPosition(newIter).Bytes <= nextPosition.Bytes)
            {
                IteratorAdvance(ref newIter);
            }

            // Ensure that both iterators are at the same depth in the tree.
            while (oldIter.VisibleDepth > newIter.VisibleDepth)
            {
                IteratorAscend(ref oldIter);
            }

            while (newIter.VisibleDepth > oldIter.VisibleDepth)
            {
                IteratorAscend(ref newIter);
            }

            if (isChanged)
            {
                RangeArrayAdd(ref results, position, nextPosition);
            }

            position = nextPosition;

            // Keep track of the current position in the included range differences
            // array in order to avoid scanning the entire array on each iteration.
            while (includedRangeDifferenceIndex < includedRangeDifferences.Size)
            {
                Range range = includedRangeDifferences.Get(includedRangeDifferenceIndex);
                if (range.EndByte <= position.Bytes)
                {
                    includedRangeDifferenceIndex++;
                }
                else
                {
                    break;
                }
            }
        }
        while (!IteratorDone(oldIter) && !IteratorDone(newIter));

        Length oldSize = oldTree.Root.TotalSize;
        Length newSize = newTree.Root.TotalSize;
        if (oldSize.Bytes < newSize.Bytes)
        {
            RangeArrayAdd(ref results, oldSize, newSize);
        }
        else if (newSize.Bytes < oldSize.Bytes)
        {
            RangeArrayAdd(ref results, newSize, oldSize);
        }

        return results.AsSpan().ToArray();
    }
}
