namespace TreeSitter;

using System.ComponentModel;

/// <summary>
/// Part of the grammar ABI, used by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate bool LexFunction(Lexer lexer, ushort state);
