using Xunit;

namespace TreeSitter.Tests.Runtime;

// Expected values follow lib/src/length.h and lib/src/error_costs.h of tree-sitter v0.27.2
// (3fb7be4).
public class LengthTests
{
    private static void AssertLength(uint bytes, uint row, uint column, Length actual)
    {
        Assert.Equal(bytes, actual.Bytes);
        Assert.Equal(new Point(row, column), actual.Extent);
    }

    [Fact]
    public void UndefinedMatchesLengthUndefinedConstant()
    {
        // LENGTH_UNDEFINED = {0, {0, 1}}
        AssertLength(0, 0, 1, Length.Undefined);
        Assert.True(Length.Undefined.IsUndefined);
    }

    [Fact]
    public void MaxMatchesLengthMaxConstant()
    {
        // LENGTH_MAX = {UINT32_MAX, {UINT32_MAX, UINT32_MAX}}
        AssertLength(uint.MaxValue, uint.MaxValue, uint.MaxValue, Length.Max);
        Assert.False(Length.Max.IsUndefined);
    }

    [Fact]
    public void IsUndefinedNeedsZeroBytesAndNonZeroColumn()
    {
        // length_is_undefined: bytes == 0 && extent.column != 0
        Assert.True(new Length(0, new Point(0, 1)).IsUndefined);
        Assert.True(new Length(0, new Point(3, 2)).IsUndefined);
        Assert.False(new Length(0, new Point(0, 0)).IsUndefined);
        Assert.False(new Length(0, new Point(1, 0)).IsUndefined);
        Assert.False(new Length(1, new Point(0, 1)).IsUndefined);
    }

    [Fact]
    public void ZeroIsAllZero()
    {
        // length_zero
        AssertLength(0, 0, 0, Length.Zero);
    }

    [Fact]
    public void MinReturnsLengthWithFewerBytes()
    {
        // length_min: (len1.bytes < len2.bytes) ? len1 : len2
        Length small = new(2, new Point(0, 2));
        Length big = new(5, new Point(0, 5));
        AssertLength(2, 0, 2, Length.Min(small, big));
        AssertLength(2, 0, 2, Length.Min(big, small));
    }

    [Fact]
    public void MinReturnsSecondOperandWhenBytesAreEqual()
    {
        // The C code takes len2 when the bytes are equal. The extents tell the two apart.
        Length first = new(4, new Point(1, 0));
        Length second = new(4, new Point(0, 4));
        AssertLength(4, 0, 4, Length.Min(first, second));
        AssertLength(4, 1, 0, Length.Min(second, first));
    }

    [Fact]
    public void AddAddsBytesAndExtents()
    {
        // length_add: bytes add, extent uses point_add.
        Length a = new(3, new Point(0, 3));
        Length b = new(4, new Point(1, 2));
        AssertLength(7, 1, 2, Length.Add(a, b));
        AssertLength(7, 1, 5, Length.Add(b, a));
    }

    [Fact]
    public void AddWrapsBytesLikeUnsignedCArithmetic()
    {
        AssertLength(0, 0, 0, Length.Add(new Length(uint.MaxValue, new Point(0, 0)), new Length(1, new Point(0, 0))));
    }

    [Fact]
    public void SubFloorsBytesAtZeroWhenFirstIsSmaller()
    {
        // length_sub: bytes1 < bytes2 -> 0 bytes, and the extent still follows point_sub.
        Length small = new(2, new Point(0, 2));
        Length big = new(5, new Point(1, 5));
        AssertLength(0, 0, 0, Length.Sub(small, big));
    }

    [Fact]
    public void SubSubtractsBytesAndUsesPointSubForExtent()
    {
        Length big = new(9, new Point(3, 7));
        Length small = new(4, new Point(1, 9));
        // rows differ and a is later: the extent row is 2 and the column is kept (7)
        AssertLength(5, 2, 7, Length.Sub(big, small));
        // same bytes: the bytes become 0; the extent of a is not later than b in row, so the
        // column difference applies (5 - 3 = 2)
        AssertLength(0, 0, 2, Length.Sub(new Length(4, new Point(0, 5)), new Length(4, new Point(0, 3))));
    }

    [Fact]
    public void SaturatingSubReturnsZeroWhenBytesAreNotGreater()
    {
        // length_saturating_sub: bytes1 > bytes2 -> length_sub, otherwise zero.
        Length a = new(5, new Point(1, 1));
        AssertLength(0, 0, 0, Length.SaturatingSub(a, new Length(5, new Point(0, 0))));
        AssertLength(0, 0, 0, Length.SaturatingSub(a, new Length(6, new Point(0, 0))));
    }

    [Fact]
    public void SaturatingSubUsesSubWhenBytesAreGreater()
    {
        Length a = new(6, new Point(2, 4));
        Length b = new(4, new Point(1, 9));
        AssertLength(2, 1, 4, Length.SaturatingSub(a, b));
    }
}

public class ErrorCostsTests
{
    [Fact]
    public void ConstantsMatchErrorCostsHeader()
    {
        // lib/src/error_costs.h, tree-sitter v0.27.2 (3fb7be4)
        Assert.Equal((ushort)0, ErrorCosts.ErrorState);
        Assert.Equal(500u, ErrorCosts.ErrorCostPerRecovery);
        Assert.Equal(110u, ErrorCosts.ErrorCostPerMissingTree);
        Assert.Equal(100u, ErrorCosts.ErrorCostPerSkippedTree);
        Assert.Equal(30u, ErrorCosts.ErrorCostPerSkippedLine);
        Assert.Equal(1u, ErrorCosts.ErrorCostPerSkippedChar);
    }
}
