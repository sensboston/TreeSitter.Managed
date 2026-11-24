// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/length.h.

namespace TreeSitter;

/// <summary>
/// A length of text: its size in bytes and the same size as a point (rows and columns).
/// </summary>
internal readonly struct Length
{
    internal readonly uint Bytes;
    internal readonly Point Extent;

    internal Length(uint bytes, Point extent)
    {
        Bytes = bytes;
        Extent = extent;
    }

    // LENGTH_UNDEFINED
    internal static readonly Length Undefined = new(0, new Point(0, 1));

    // LENGTH_MAX
    internal static readonly Length Max = new(uint.MaxValue, new Point(uint.MaxValue, uint.MaxValue));

    // length_zero
    internal static Length Zero => new(0, new Point(0, 0));

    // length_is_undefined
    internal bool IsUndefined => Bytes == 0 && Extent.Column != 0;

    // length_min: returns len2 when both lengths have the same number of bytes, as in C.
    internal static Length Min(Length len1, Length len2) => len1.Bytes < len2.Bytes ? len1 : len2;

    // length_add
    internal static Length Add(Length len1, Length len2) =>
        new(len1.Bytes + len2.Bytes, Point.Add(len1.Extent, len2.Extent));

    // length_sub: the byte count is floored at 0. The extent follows point_sub.
    internal static Length Sub(Length len1, Length len2) =>
        new(len1.Bytes >= len2.Bytes ? len1.Bytes - len2.Bytes : 0, Point.Sub(len1.Extent, len2.Extent));

    // length_saturating_sub: a zero length when len1 does not have more bytes than len2.
    internal static Length SaturatingSub(Length len1, Length len2) =>
        len1.Bytes > len2.Bytes ? Sub(len1, len2) : Zero;
}
