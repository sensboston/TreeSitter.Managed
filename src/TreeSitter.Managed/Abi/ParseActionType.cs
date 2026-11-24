namespace TreeSitter.Abi;

using System.ComponentModel;

/// <summary>
/// Part of the grammar ABI, used by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum ParseActionType : byte
{
    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Shift = 0,

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Reduce = 1,

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Accept = 2,

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Recover = 3,
}
