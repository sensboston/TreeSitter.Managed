using Xunit;

namespace TreeSitter.Tests.Runtime;

// Expected values follow lib/src/point.h of tree-sitter v0.27.2 (3fb7be4).
public class PointTests
{
    [Fact]
    public void ConstructorStoresRowAndColumn()
    {
        Point point = new(3, 7);
        Assert.Equal(3u, point.Row);
        Assert.Equal(7u, point.Column);
    }

    [Fact]
    public void AddAddsRowsAndTakesColumnOfSecondOperandWhenItHasRows()
    {
        // point_add: b.row > 0 -> (a.row + b.row, b.column)
        Assert.Equal(new Point(3, 3), Point.Add(new Point(1, 5), new Point(2, 3)));
        Assert.Equal(new Point(2, 3), Point.Add(new Point(1, 5), new Point(1, 3)));
    }

    [Fact]
    public void AddAddsColumnsAndKeepsRowWhenSecondOperandIsOnRowZero()
    {
        // point_add: b.row == 0 -> (a.row, a.column + b.column)
        Assert.Equal(new Point(0, 8), Point.Add(new Point(0, 5), new Point(0, 3)));
        Assert.Equal(new Point(2, 8), Point.Add(new Point(2, 5), new Point(0, 3)));
    }

    [Fact]
    public void AddWrapsAroundLikeUnsignedCArithmetic()
    {
        // No checked context in C: uint overflow wraps around.
        Assert.Equal(new Point(0, 0), Point.Add(new Point(uint.MaxValue, 0), new Point(1, 0)));
        Assert.Equal(new Point(0, 0), Point.Add(new Point(0, uint.MaxValue), new Point(0, 1)));
    }

    [Fact]
    public void SubSubtractsRowsAndKeepsColumnOfFirstOperandWhenRowIsLater()
    {
        // point_sub: a.row > b.row -> (a.row - b.row, a.column). The columns are not compared.
        Assert.Equal(new Point(3, 3), Point.Sub(new Point(5, 3), new Point(2, 7)));
        Assert.Equal(new Point(1, 7), Point.Sub(new Point(3, 7), new Point(2, 9)));
    }

    [Fact]
    public void SubUsesColumnDifferenceFloredAtZeroWhenRowIsNotLater()
    {
        // point_sub: otherwise (0, a.column - b.column, or 0 when a.column < b.column).
        Assert.Equal(new Point(0, 4), Point.Sub(new Point(2, 7), new Point(5, 3)));
        Assert.Equal(new Point(0, 0), Point.Sub(new Point(2, 3), new Point(2, 7)));
        Assert.Equal(new Point(0, 4), Point.Sub(new Point(2, 7), new Point(2, 3)));
        Assert.Equal(new Point(0, 0), Point.Sub(new Point(1, 5), new Point(1, 5)));
        Assert.Equal(new Point(0, 0), Point.Sub(new Point(0, 0), new Point(0, 1)));
    }

    [Fact]
    public void LessThanOrEqualComparesRowThenColumn()
    {
        // point_lte
        Assert.True(Point.Lte(new Point(1, 5), new Point(1, 5)));
        Assert.False(Point.Lte(new Point(1, 5), new Point(1, 4)));
        Assert.True(Point.Lte(new Point(0, 9), new Point(1, 0)));
        Assert.False(Point.Lte(new Point(2, 0), new Point(1, 9)));
    }

    [Fact]
    public void LessThanComparesRowThenColumn()
    {
        // point_lt
        Assert.False(Point.Lt(new Point(1, 5), new Point(1, 5)));
        Assert.True(Point.Lt(new Point(0, 9), new Point(1, 0)));
        Assert.True(Point.Lt(new Point(1, 4), new Point(1, 5)));
        Assert.False(Point.Lt(new Point(2, 0), new Point(1, 9)));
    }

    [Fact]
    public void GreaterThanComparesRowThenColumn()
    {
        // point_gt
        Assert.False(Point.Gt(new Point(1, 5), new Point(1, 5)));
        Assert.True(Point.Gt(new Point(2, 0), new Point(1, 9)));
        Assert.True(Point.Gt(new Point(1, 6), new Point(1, 5)));
        Assert.False(Point.Gt(new Point(0, 9), new Point(1, 0)));
    }

    [Fact]
    public void GreaterThanOrEqualComparesRowThenColumn()
    {
        // point_gte
        Assert.True(Point.Gte(new Point(1, 5), new Point(1, 5)));
        Assert.False(Point.Gte(new Point(1, 4), new Point(1, 5)));
        Assert.True(Point.Gte(new Point(2, 0), new Point(1, 9)));
        Assert.False(Point.Gte(new Point(0, 9), new Point(1, 0)));
    }

    [Fact]
    public void EqualityUsesRowAndColumn()
    {
        // point_eq
        Assert.True(new Point(1, 2) == new Point(1, 2));
        Assert.True(new Point(1, 2) != new Point(2, 1));
        Assert.True(new Point(1, 2).Equals(new Point(1, 2)));
        object boxed = new Point(1, 2);
        object other = "1,2";
        Assert.True(new Point(1, 2).Equals(boxed));
        Assert.False(new Point(1, 2).Equals(other));
        Assert.Equal(new Point(1, 2).GetHashCode(), new Point(1, 2).GetHashCode());
    }
}
