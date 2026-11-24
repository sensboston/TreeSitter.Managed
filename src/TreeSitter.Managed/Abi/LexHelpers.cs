// Port of the lexer helpers of lib/src/parser.h (tree-sitter v0.27.2, commit 3fb7be4):
// set_contains and the loop of the ADVANCE_MAP macro.

namespace TreeSitter.Abi;

using System.ComponentModel;

/// <summary>
/// Part of the grammar ABI, used by generated lex functions.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LexHelpers
{
    /// <summary>
    /// set_contains of parser.h. <paramref name="ranges"/> holds start, end pairs (a C
    /// TSCharacterRange array), <paramref name="length"/> is the number of pairs.
    /// </summary>
    public static bool SetContains(ReadOnlySpan<int> ranges, uint length, int lookahead)
    {
        uint index = 0;
        uint size = length - index;
        while (size > 1)
        {
            uint halfSize = size / 2;
            uint midIndex = index + halfSize;
            int start = ranges[(int)(midIndex * 2)];
            int end = ranges[(int)(midIndex * 2) + 1];
            if (lookahead >= start && lookahead <= end)
            {
                return true;
            }
            else if (lookahead > end)
            {
                index = midIndex;
            }
            size -= halfSize;
        }
        return lookahead >= ranges[(int)(index * 2)] && lookahead <= ranges[(int)(index * 2) + 1];
    }

    /// <summary>
    /// The loop of ADVANCE_MAP: <paramref name="map"/> holds key, state pairs. On the first key
    /// equal to <paramref name="lookahead"/> sets <paramref name="state"/> and returns true.
    /// </summary>
    public static bool AdvanceMap(ReadOnlySpan<ushort> map, int lookahead, ref ushort state)
    {
        for (int i = 0; i < map.Length; i += 2)
        {
            if (map[i] == lookahead)
            {
                state = map[i + 1];
                return true;
            }
        }
        return false;
    }
}
