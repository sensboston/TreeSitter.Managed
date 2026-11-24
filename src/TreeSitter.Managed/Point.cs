// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/point.h (point_* functions).
// point.c (ts_point_edit) is not part of this port.

namespace TreeSitter;

/// <summary>
/// A position in the text: zero-based row and zero-based column. The column counts bytes,
/// also for UTF-16 input.
/// </summary>
public readonly struct Point : IEquatable<Point>
{
    /// <summary>Creates a point at the given row and column.</summary>
    /// <param name="row">Zero-based row.</param>
    /// <param name="column">Zero-based column, in bytes.</param>
    public Point(uint row, uint column)
    {
        Row = row;
        Column = column;
    }

    /// <summary>Zero-based row.</summary>
    public uint Row { get; }

    /// <summary>Zero-based column, in bytes.</summary>
    public uint Column { get; }

    /// <summary>Returns true when both the row and the column are equal (point_eq).</summary>
    /// <param name="other">The point to compare with.</param>
    public bool Equals(Point other) => Row == other.Row && Column == other.Column;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Point other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Row, Column);

    /// <summary>Returns true when both points have the same row and column (point_eq).</summary>
    /// <param name="left">The first point.</param>
    /// <param name="right">The second point.</param>
    public static bool operator ==(Point left, Point right) => left.Equals(right);

    /// <summary>Returns true when the points differ in row or column.</summary>
    /// <param name="left">The first point.</param>
    /// <param name="right">The second point.</param>
    public static bool operator !=(Point left, Point right) => !left.Equals(right);

    // point_add: when b has rows, the rows add and the column is b's column.
    // Otherwise the row of a is kept and the columns add. uint arithmetic wraps, as in C.
    internal static Point Add(Point a, Point b)
    {
        return b.Row > 0
            ? new Point(a.Row + b.Row, b.Column)
            : new Point(a.Row, a.Column + b.Column);
    }

    // point_sub: when a is on a later row than b, the rows subtract and a's column is kept.
    // Otherwise the result is row 0 and the column difference, floored at 0.
    internal static Point Sub(Point a, Point b)
    {
        if (a.Row > b.Row)
        {
            return new Point(a.Row - b.Row, a.Column);
        }
        return new Point(0, a.Column >= b.Column ? a.Column - b.Column : 0);
    }

    // point_lte
    internal static bool Lte(Point a, Point b) =>
        a.Row < b.Row || (a.Row == b.Row && a.Column <= b.Column);

    // point_lt
    internal static bool Lt(Point a, Point b) =>
        a.Row < b.Row || (a.Row == b.Row && a.Column < b.Column);

    // point_gt
    internal static bool Gt(Point a, Point b) =>
        a.Row > b.Row || (a.Row == b.Row && a.Column > b.Column);

    // point_gte
    internal static bool Gte(Point a, Point b) =>
        a.Row > b.Row || (a.Row == b.Row && a.Column >= b.Column);
}
