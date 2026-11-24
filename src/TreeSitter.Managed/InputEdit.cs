namespace TreeSitter;

/// <summary>An edit of the source text (api.h TSInputEdit). Byte offsets and points describe the same positions.</summary>
public readonly record struct InputEdit(
    uint StartByte,
    uint OldEndByte,
    uint NewEndByte,
    Point StartPoint,
    Point OldEndPoint,
    Point NewEndPoint);
