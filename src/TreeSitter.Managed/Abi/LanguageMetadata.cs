namespace TreeSitter.Abi;

using System.ComponentModel;

/// <summary>
/// Part of the grammar ABI, used by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly record struct LanguageMetadata(byte MajorVersion, byte MinorVersion, byte PatchVersion);
