using TreeSitter.Grammars.Json;
using TreeSitter.Subtrees;
using Xunit;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// Unit tests of the parts of the subtree port that the scripts do not reach:
/// the external scanner state, the subtree arrays and the accessors of the
/// subtree handle. The expected values follow lib/src/subtree.c and
/// lib/src/subtree.h of tree-sitter v0.27.2 (3fb7be4).
/// </summary>
public class SubtreeUnitTests
{
    private static Language JsonLanguage => JsonGrammar.Language;

    /// <summary>A leaf that fits into the inline data.</summary>
    private static Subtree InlineLeaf(SubtreePool pool, ushort symbol = 10) =>
        SubtreeOps.NewLeaf(
            pool, symbol, Length.Zero, new Length(1, new Point(0, 1)), 1, 3, false, false, false, JsonLanguage);

    /// <summary>A leaf that needs a heap node (the padding is too long).</summary>
    private static Subtree HeapLeaf(SubtreePool pool, ushort symbol = 10, bool hasExternalTokens = false) =>
        SubtreeOps.NewLeaf(
            pool, symbol, new Length(255, new Point(0, 255)), new Length(1, new Point(0, 1)), 1, 3,
            hasExternalTokens, false, false, JsonLanguage);

    private static Subtree Node(SubtreePool pool, ushort symbol, params Subtree[] children)
    {
        TsArray<Subtree> array = new TsArray<Subtree>();
        foreach (Subtree child in children)
        {
            array.Push(child);
        }

        return SubtreeOps.NewNode(symbol, ref array, 0, JsonLanguage);
    }

    // ------------------------------------------------------------ external scanner state

    [Fact]
    public void ShortExternalStateKeepsItsBytes()
    {
        byte[] data = { 1, 2, 3, 4 };
        ExternalScannerState state = ExternalScannerState.Create(data);
        Assert.Equal(4u, state.Length);
        Assert.True(state.Data.SequenceEqual(data));
        Assert.True(state.Eq(data));
        Assert.False(state.Eq(new byte[] { 1, 2 }));
        Assert.False(state.Eq(new byte[] { 1, 2, 3, 5 }));
    }

    [Fact]
    public void EmptyExternalStateHasNoBytes()
    {
        ExternalScannerState state = ExternalScannerState.Create(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0u, state.Length);
        Assert.True(state.Data.IsEmpty);
        Assert.True(state.Eq(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void LongExternalStateKeepsItsBytes()
    {
        byte[] data = new byte[64];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        ExternalScannerState state = ExternalScannerState.Create(data);
        Assert.Equal(64u, state.Length);
        Assert.True(state.Data.SequenceEqual(data));
        Assert.True(state.Eq(data));
    }

    [Fact]
    public void CopiedExternalStateHasTheSameBytes()
    {
        byte[] data = new byte[40];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(255 - i);
        }

        ExternalScannerState state = ExternalScannerState.Create(data);
        ExternalScannerState copy = state.Copy();
        Assert.Equal(state.Length, copy.Length);
        Assert.True(copy.Data.SequenceEqual(data));
        Assert.True(copy.Eq(data));
    }

    [Fact]
    public void DeletedExternalStateIsEmpty()
    {
        byte[] data = new byte[40];
        ExternalScannerState state = ExternalScannerState.Create(data);
        state.Delete();
        Assert.Equal(0u, state.Length);
        Assert.True(state.Data.IsEmpty);
    }

    [Fact]
    public void SubtreeExternalScannerStateIsEmptyWithoutExternalTokens()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = HeapLeaf(pool);
        Assert.Equal(0u, SubtreeOps.GetExternalScannerState(leaf).Length);
    }

    [Fact]
    public void SubtreeExternalScannerStateIsTheStateOfTheToken()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = HeapLeaf(pool, 10, hasExternalTokens: true);
        {
            ExternalScannerState _st = ExternalScannerState.Create(new byte[] { 7, 8, 9 });
            ref SubtreeHeapData _hd = ref SubtreeArena.Get(leaf);
            _st.WriteTo(ref _hd.ExternalScannerState);
        }
        ExternalScannerState state = SubtreeOps.GetExternalScannerState(leaf);
        Assert.Equal(3u, state.Length);
        Assert.True(state.Data.SequenceEqual(new byte[] { 7, 8, 9 }));
    }

    [Fact]
    public void ExternalScannerStatesAreEqualWhenTheirBytesAre()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree left = HeapLeaf(pool, 10, hasExternalTokens: true);
        Subtree right = HeapLeaf(pool, 10, hasExternalTokens: true);
        {
            ExternalScannerState _st = ExternalScannerState.Create(new byte[] { 7, 8, 9 });
            ref SubtreeHeapData _hd = ref SubtreeArena.Get(left);
            _st.WriteTo(ref _hd.ExternalScannerState);
        }
        {
            ExternalScannerState _st = ExternalScannerState.Create(new byte[] { 7, 8, 9 });
            ref SubtreeHeapData _hd = ref SubtreeArena.Get(right);
            _st.WriteTo(ref _hd.ExternalScannerState);
        }
        Assert.True(SubtreeOps.ExternalScannerStateEq(left, right));

        {
            ExternalScannerState _st = ExternalScannerState.Create(new byte[] { 7, 8 });
            ref SubtreeHeapData _hd = ref SubtreeArena.Get(right);
            _st.WriteTo(ref _hd.ExternalScannerState);
        }
        Assert.False(SubtreeOps.ExternalScannerStateEq(left, right));
    }

    // ------------------------------------------------------------ arrays

    [Fact]
    public void ArrayCopyRetainsEveryElementAndArrayClearReleasesIt()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = HeapLeaf(pool);
        TsArray<Subtree> source = new TsArray<Subtree>();
        source.Push(leaf);
        Assert.Equal(1, SubtreeArena.Get(leaf).RefCount);

        TsArray<Subtree> destination = new TsArray<Subtree>();
        SubtreeOps.ArrayCopy(source, ref destination);
        Assert.Equal(1u, destination.Size);
        Assert.Equal(2, SubtreeArena.Get(leaf).RefCount);

        SubtreeOps.ArrayClear(pool, ref destination);
        Assert.Equal(0u, destination.Size);
        Assert.Equal(1, SubtreeArena.Get(leaf).RefCount);

        SubtreeOps.ArrayDelete(pool, ref source);
        Assert.Equal(0u, source.Size);
        pool.Delete();
    }

    [Fact]
    public void ArrayReverseSwapsTheElements()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree first = InlineLeaf(pool, 1);
        Subtree second = InlineLeaf(pool, 2);
        TsArray<Subtree> array = new TsArray<Subtree>();
        array.Push(first);
        array.Push(second);
        array.Push(InlineLeaf(pool, 3));

        SubtreeOps.ArrayReverse(ref array);
        Assert.Equal(3, array.Get(0).Symbol);
        Assert.Equal(2, array.Get(1).Symbol);
        Assert.Equal(1, array.Get(2).Symbol);
        pool.Delete();
    }

    [Fact]
    public void ArrayRemoveTrailingExtrasMovesThemInReverseOrder()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree extra = InlineLeaf(pool, 10);
        SubtreeOps.SetExtra(ref extra, true);
        Subtree otherExtra = InlineLeaf(pool, 11);
        SubtreeOps.SetExtra(ref otherExtra, true);
        Subtree last = InlineLeaf(pool, 12);

        TsArray<Subtree> array = new TsArray<Subtree>();
        array.Push(last);
        array.Push(extra);
        array.Push(otherExtra);
        TsArray<Subtree> destination = new TsArray<Subtree>();

        SubtreeOps.ArrayRemoveTrailingExtras(ref array, ref destination);
        Assert.Equal(1u, array.Size);
        Assert.Equal(12, array.Get(0).Symbol);
        Assert.Equal(2u, destination.Size);
        Assert.Equal(10, destination.Get(0).Symbol);
        Assert.Equal(11, destination.Get(1).Symbol);
        pool.Delete();
    }

    // ------------------------------------------------------------ accessors

    [Fact]
    public void NullSubtreeIsNotInline()
    {
        // The accessors of a null subtree read the heap data, like the C code does.
        Subtree nullTree = Subtree.Null;
        Assert.True(nullTree.IsNull);
        Assert.False(nullTree.IsInline);
    }

    [Fact]
    public void InlineLeafHasTheValuesOfTheInlineData()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        Assert.True(leaf.IsInline);
        Assert.False(leaf.IsNull);
        Assert.Equal(10, leaf.Symbol);
        Assert.Equal(3, leaf.ParseState);
        Assert.True(leaf.Visible);
        Assert.True(leaf.Named);
        Assert.False(leaf.Extra);
        Assert.False(leaf.HasChanges);
        Assert.False(leaf.IsMissing);
        Assert.False(leaf.IsKeyword);
        Assert.False(leaf.DependsOnColumn);
        Assert.Equal(1u, leaf.LookaheadBytes);
        Assert.Equal(0u, leaf.Padding.Bytes);
        Assert.Equal(1u, leaf.Size.Bytes);
        Assert.Equal(1u, leaf.TotalBytes);
        Assert.Equal(1u, leaf.TotalSize.Extent.Column);
        Assert.Equal(10, leaf.LeafSymbol);
        Assert.Equal(3, leaf.LeafParseState);
        Assert.Equal(0u, leaf.RepeatDepth);
        Assert.False(leaf.IsRepetition);
        Assert.False(leaf.IsFragile);
        Assert.False(leaf.IsError);
        Assert.False(leaf.IsEof);
        Assert.False(leaf.HasExternalTokens);
        Assert.False(leaf.HasExternalScannerStateChange);
        Assert.Equal(0u, leaf.VisibleChildCount);
        Assert.Equal(0u, leaf.VisibleDescendantCount);
        Assert.Equal(0, leaf.DynamicPrecedence);
        Assert.Equal(0, leaf.ProductionId);
        Assert.Equal(0u, leaf.ErrorCost);
        pool.Delete();
    }

    [Fact]
    public void AHeapLeafHasTheValuesOfTheHeapData()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = HeapLeaf(pool);
        Assert.False(leaf.IsInline);
        Assert.Equal(10, leaf.Symbol);
        Assert.Equal(3, leaf.ParseState);
        Assert.True(leaf.Visible);
        Assert.True(leaf.Named);
        Assert.False(leaf.Extra);
        Assert.False(leaf.HasChanges);
        Assert.False(leaf.IsMissing);
        Assert.False(leaf.IsKeyword);
        Assert.False(leaf.DependsOnColumn);
        Assert.Equal(1u, leaf.LookaheadBytes);
        Assert.Equal(255u, leaf.Padding.Bytes);
        Assert.Equal(0u, leaf.Padding.Extent.Row);
        Assert.Equal(255u, leaf.Padding.Extent.Column);
        Assert.Equal(1u, leaf.Size.Bytes);
        Assert.Equal(0u, leaf.Size.Extent.Row);
        Assert.Equal(1u, leaf.Size.Extent.Column);
        Assert.Equal(256u, leaf.TotalBytes);
        Assert.Equal(0u, leaf.ChildCount);
        Assert.True(leaf.Children.IsEmpty);
        Assert.Equal(10, leaf.LeafSymbol);
        Assert.Equal(3, leaf.LeafParseState);
        Assert.Equal(0u, leaf.RepeatDepth);
        Assert.False(leaf.IsRepetition);
        Assert.False(leaf.IsFragile);
        Assert.False(leaf.IsError);
        Assert.False(leaf.IsEof);
        Assert.False(leaf.HasExternalTokens);
        Assert.False(leaf.HasExternalScannerStateChange);
        Assert.Equal(0u, leaf.VisibleChildCount);
        Assert.Equal(0u, leaf.VisibleDescendantCount);
        Assert.Equal(0, leaf.DynamicPrecedence);
        Assert.Equal(0, leaf.ProductionId);
        Assert.Equal(0u, leaf.ErrorCost);
        pool.Delete();
    }

    [Fact]
    public void ReleasingAnInlineLeafDoesNothing()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        SubtreeOps.Release(pool, leaf);
        Assert.True(leaf.IsInline);
        pool.Delete();
    }

    [Fact]
    public void AnInlineLeafHasNoExternalScannerState()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        Assert.Equal(0u, SubtreeOps.GetExternalScannerState(leaf).Length);
        Assert.True(SubtreeOps.ExternalScannerStateEq(leaf, InlineLeaf(pool)));
    }

    [Fact]
    public void CloningAnExternalTokenCopiesItsState()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = HeapLeaf(pool, 10, hasExternalTokens: true);
        {
            ExternalScannerState _st = ExternalScannerState.Create(new byte[] { 7, 8, 9 });
            ref SubtreeHeapData _hd = ref SubtreeArena.Get(leaf);
            _st.WriteTo(ref _hd.ExternalScannerState);
        }
        Subtree copy = SubtreeOps.Clone(leaf);
        Assert.Equal(3u, SubtreeOps.GetExternalScannerState(copy).Length);
        Assert.True(SubtreeOps.ExternalScannerStateEq(leaf, copy));
        Assert.NotEqual(leaf.Handle, copy.Handle);
        pool.Delete();
    }

    [Fact]
    public void MakingAnInlineLeafMutableKeepsIt()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        Assert.Equal(leaf, SubtreeOps.MakeMut(pool, leaf));
        pool.Delete();
    }

    [Fact]
    public void CopyingAnEmptyArrayKeepsItEmpty()
    {
        SubtreePool pool = new SubtreePool(4);
        TsArray<Subtree> source = new TsArray<Subtree>();
        TsArray<Subtree> destination = new TsArray<Subtree>();
        SubtreeOps.ArrayCopy(source, ref destination);
        Assert.Equal(0u, destination.Size);
        Assert.Null(destination.Contents);
        pool.Delete();
    }

    [Fact]
    public void AChildWithTheErrorRepeatSymbolRefundsItsExtentCost()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree child = HeapLeaf(pool, 10);
        SubtreeOps.SetSymbol(ref child, BuiltinSymbols.ErrorRepeat, JsonLanguage);
        Subtree node = Node(pool, 19, child, InlineLeaf(pool));

        // The C code subtracts the extent cost of the child from its error cost
        // with unsigned arithmetic: the difference wraps around.
        Assert.Equal(unchecked(0u - 501u), node.ErrorCost);
        pool.Delete();
    }

    [Fact]
    public void NodeSummarizesItsChildren()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree first = InlineLeaf(pool);
        Subtree second = InlineLeaf(pool);
        Subtree node = Node(pool, 19, first, second);

        Assert.False(node.IsInline);
        Assert.Equal(19, node.Symbol);
        Assert.Equal(2u, node.ChildCount);
        Assert.Equal(2u, node.VisibleChildCount);
        Assert.Equal(2u, node.VisibleDescendantCount);
        Assert.Equal(2u, node.TotalBytes);
        Assert.Equal(10, node.LeafSymbol);
        Assert.False(node.IsRepetition);
        Assert.False(node.IsFragile);
        pool.Delete();
    }

    [Fact]
    public void RepetitionNodeIsMarkedAsOne()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree node = Node(pool, 22, InlineLeaf(pool, 22), InlineLeaf(pool, 22));
        Assert.True(node.IsRepetition);
        Assert.Equal(1u, node.RepeatDepth);
        pool.Delete();
    }

    [Fact]
    public void ErrorAndEndSymbolsAreRecognized()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree error = Node(pool, BuiltinSymbols.Error, InlineLeaf(pool));
        Assert.True(error.IsError);
        Assert.False(error.IsEof);

        Subtree end = Node(pool, BuiltinSymbols.End, InlineLeaf(pool));
        Assert.True(end.IsEof);
        Assert.False(end.IsError);
        pool.Delete();
    }

    [Fact]
    public void EqualSubtreesHaveTheSameHashCode()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree first = InlineLeaf(pool);
        Subtree second = InlineLeaf(pool);
        Assert.True(first.Equals(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first.Equals(Node(pool, 19, InlineLeaf(pool))));
        Assert.False(first.Equals((object)"not a subtree"));
        pool.Delete();
    }

    [Fact]
    public void MissingLeafHasTheErrorCostOfAMissingTree()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree missing = SubtreeOps.NewMissingLeaf(pool, 10, 3, Length.Zero, 1, JsonLanguage);
        Assert.True(missing.IsMissing);
        Assert.Equal(
            ErrorCosts.ErrorCostPerMissingTree + ErrorCosts.ErrorCostPerRecovery,
            missing.ErrorCost);
        pool.Delete();
    }

    [Fact]
    public void ErrorLeafIsFragileOnBothSides()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree error = SubtreeOps.NewError(
            pool, 64, Length.Zero, new Length(1, new Point(0, 1)), 1, 3, JsonLanguage);
        Assert.True(error.IsError);
        Assert.True(error.FragileLeft);
        Assert.True(error.FragileRight);
        Assert.True(error.IsFragile);
        pool.Delete();
    }

    [Fact]
    public void SetSymbolChangesTheNameAndTheMetadata()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        SubtreeOps.SetSymbol(ref leaf, 12, JsonLanguage);
        Assert.Equal(12, leaf.Symbol);
        Assert.True(leaf.Named);
        Assert.True(leaf.Visible);
        pool.Delete();
    }

    [Fact]
    public void SetSymbolClearsTheMetadataOfTheOldSymbol()
    {
        // A visible named leaf becomes the hidden symbol _value (16) and the hidden
        // anonymous aux_sym_document_repeat1 (22), as a keyword becomes the word token.
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool, 12);
        SubtreeOps.SetSymbol(ref leaf, 16, JsonLanguage);
        Assert.True(leaf.Named);
        Assert.False(leaf.Visible);
        SubtreeOps.SetSymbol(ref leaf, 22, JsonLanguage);
        Assert.False(leaf.Named);
        Assert.False(leaf.Visible);
        pool.Delete();
    }

    [Fact]
    public void SetExtraChangesTheExtraFlag()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        SubtreeOps.SetExtra(ref leaf, true);
        Assert.True(leaf.Extra);
        SubtreeOps.SetExtra(ref leaf, false);
        Assert.False(leaf.Extra);
        pool.Delete();
    }

    [Fact]
    public void ANullSubtreeIsPrintedAsNull()
    {
        Assert.Equal("(NULL)", SubtreeOps.ToSExpression(Subtree.Null, 0, false, JsonLanguage, false));
    }

    [Theory]
    [InlineData(-1, "(UNEXPECTED INVALID)")]
    [InlineData(0, "(UNEXPECTED '\\0')")]
    [InlineData(9, "(UNEXPECTED '\\t')")]
    [InlineData(10, "(UNEXPECTED '\\n')")]
    [InlineData(13, "(UNEXPECTED '\\r')")]
    [InlineData(65, "(UNEXPECTED 'A')")]
    [InlineData(200, "(UNEXPECTED 200)")]
    public void TheUnexpectedCharacterIsPrinted(int character, string expected)
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree error = SubtreeOps.NewError(
            pool, character, Length.Zero, new Length(1, new Point(0, 1)), 1, 3, JsonLanguage);
        Assert.Equal(expected, SubtreeOps.ToSExpression(error, 0, false, JsonLanguage, false));
        pool.Delete();
    }

    [Fact]
    public void AMissingLeafIsPrintedAsMissing()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree missing = SubtreeOps.NewMissingLeaf(pool, 6, 3, Length.Zero, 1, JsonLanguage);
        Assert.Equal("(MISSING \"]\")", SubtreeOps.ToSExpression(missing, 0, false, JsonLanguage, false));
        pool.Delete();
    }

    // ------------------------------------------------------------ the functions of M1a-T6b

    [Fact]
    public void CompressOnALeafDoesNothing()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        TsArray<Subtree> stack = new TsArray<Subtree>();
        SubtreeOps.Compress(leaf, 5, JsonLanguage, ref stack);
        Assert.Equal(0u, stack.Size);
        Assert.True(leaf.IsInline);
        pool.Delete();
    }

    [Fact]
    public void CompressRotatesARepeatChain()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree first = Node(pool, 24, InlineLeaf(pool, 10), InlineLeaf(pool, 10));
        Subtree second = Node(pool, 24, first, InlineLeaf(pool, 10));
        Subtree third = Node(pool, 24, second, InlineLeaf(pool, 10));
        Assert.Equal(2u, third.RepeatDepth);

        TsArray<Subtree> stack = new TsArray<Subtree>();
        SubtreeOps.Compress(third, 5, JsonLanguage, ref stack);
        Assert.Equal(0u, stack.Size);
        Assert.Equal((ushort)24, third.Symbol);
        Assert.Equal(2u, third.ChildCount);
        Assert.Equal(4u, third.TotalBytes);
        pool.Delete();
    }

    [Fact]
    public void CompressStopsWhenAChildIsShared()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree first = Node(pool, 24, InlineLeaf(pool, 10), InlineLeaf(pool, 10));
        Subtree second = Node(pool, 24, first, InlineLeaf(pool, 10));
        SubtreeOps.Retain(first);
        Assert.Equal(2, SubtreeArena.Get(first).RefCount);

        TsArray<Subtree> stack = new TsArray<Subtree>();
        SubtreeOps.Compress(second, 5, JsonLanguage, ref stack);
        Assert.Equal(0u, stack.Size);
        Assert.Equal(2, SubtreeArena.Get(first).RefCount);
        pool.Delete();
    }

    [Fact]
    public void CompareReturnsZeroForEqualSubtrees()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree left = Node(pool, 19, InlineLeaf(pool, 10), InlineLeaf(pool, 10));
        Subtree right = Node(pool, 19, InlineLeaf(pool, 10), InlineLeaf(pool, 10));
        Assert.Equal(0, SubtreeOps.Compare(left, right, pool));
        Assert.Equal(0, SubtreeOps.Compare(left, left, pool));
        pool.Delete();
    }

    [Fact]
    public void CompareOrdersBySymbolThenByChildCount()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree small = InlineLeaf(pool, 10);
        Subtree large = InlineLeaf(pool, 11);
        Assert.Equal(-1, SubtreeOps.Compare(small, large, pool));
        Assert.Equal(1, SubtreeOps.Compare(large, small, pool));

        Subtree one = Node(pool, 19, InlineLeaf(pool, 10));
        Subtree two = Node(pool, 19, InlineLeaf(pool, 10), InlineLeaf(pool, 10));
        Assert.Equal(-1, SubtreeOps.Compare(one, two, pool));
        Assert.Equal(1, SubtreeOps.Compare(two, one, pool));
        pool.Delete();
    }

    [Fact]
    public void CompareLooksAtChildrenRecursively()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree left = Node(pool, 19, InlineLeaf(pool, 10));
        Subtree right = Node(pool, 19, InlineLeaf(pool, 11));
        Assert.Equal(-1, SubtreeOps.Compare(left, right, pool));
        Assert.Equal(1, SubtreeOps.Compare(right, left, pool));
        pool.Delete();
    }

    [Fact]
    public void EditOnALeafSetsHasChanges()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        InputEdit edit = new InputEdit(0, 0, 2, new Point(0, 0), new Point(0, 0), new Point(0, 2));
        Subtree edited = SubtreeOps.Edit(leaf, edit, pool);
        Assert.True(edited.HasChanges);
        pool.Delete();
    }

    [Fact]
    public void EditAfterTheSubtreeDoesNothing()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        InputEdit edit = new InputEdit(100, 100, 100, new Point(0, 100), new Point(0, 100), new Point(0, 100));
        Subtree edited = SubtreeOps.Edit(leaf, edit, pool);
        Assert.False(edited.HasChanges);
        Assert.Equal(1u, edited.Size.Bytes);
        pool.Delete();
    }

    [Fact]
    public void EditBeforeTheSubtreeShiftsItsPadding()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = SubtreeOps.NewLeaf(
            pool, 10, new Length(2, new Point(0, 2)), new Length(1, new Point(0, 1)), 1, 3, false, false, false, JsonLanguage);
        InputEdit edit = new InputEdit(0, 1, 3, new Point(0, 0), new Point(0, 1), new Point(0, 3));
        Subtree edited = SubtreeOps.Edit(leaf, edit, pool);
        Assert.True(edited.HasChanges);
        Assert.Equal(4u, edited.Padding.Bytes);
        Assert.Equal(1u, edited.Size.Bytes);
        pool.Delete();
    }

    [Fact]
    public void LastExternalTokenIsNullWithoutExternalTokens()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = InlineLeaf(pool);
        Assert.True(SubtreeOps.LastExternalToken(leaf).IsNull);
        pool.Delete();
    }

    [Fact]
    public void LastExternalTokenIsTheTokenItself()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree leaf = HeapLeaf(pool, 10, hasExternalTokens: true);
        Assert.Equal(leaf, SubtreeOps.LastExternalToken(leaf));
        pool.Delete();
    }

    [Fact]
    public void LastExternalTokenFindsTheLastOneInTheTree()
    {
        SubtreePool pool = new SubtreePool(4);
        Subtree first = HeapLeaf(pool, 10, hasExternalTokens: true);
        Subtree second = HeapLeaf(pool, 11, hasExternalTokens: true);
        Subtree node = Node(pool, 19, first, second);
        Assert.Equal(second, SubtreeOps.LastExternalToken(node));
        pool.Delete();
    }
}
