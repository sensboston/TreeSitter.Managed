namespace TreeSitter.Abi;

using System.ComponentModel;

/// <summary>
/// Part of the grammar ABI, used by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LanguageTables
{
    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint AbiVersion { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint SymbolCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint AliasCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint TokenCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint ExternalTokenCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint StateCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint LargeStateCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint ProductionIdCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint FieldCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort MaxAliasSequenceLength { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort KeywordCaptureToken { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? ParseTable { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? SmallParseTable { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint[]? SmallParseTableMap { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ParseActionEntry[]? ParseActions { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string?[]? SymbolNames { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string?[]? FieldNames { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public MapSlice[]? FieldMapSlices { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public FieldMapEntry[]? FieldMapEntries { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public SymbolMetadata[]? SymbolMetadata { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? PublicSymbolMap { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? AliasMap { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? AliasSequences { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public LexerMode[]? LexModes { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TreeSitter.LexFunction? LexFn { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TreeSitter.LexFunction? KeywordLexFn { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? PrimaryStateIds { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? Name { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? ReservedWords { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort MaxReservedWordSetSize { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public uint SupertypeCount { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? SupertypeSymbols { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public MapSlice[]? SupertypeMapSlices { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? SupertypeMapEntries { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public LanguageMetadata Metadata { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool[]? ExternalScannerStates { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ushort[]? ExternalScannerSymbolMap { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ExternalScannerCreateIsSet { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ExternalScannerDestroyIsSet { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ExternalScannerScanIsSet { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ExternalScannerSerializeIsSet { get; set; }

    /// <summary>Part of the grammar ABI, used by generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ExternalScannerDeserializeIsSet { get; set; }

    /// <summary>
    /// external_scanner.create: makes the scanner object (the payload of the C code). Null
    /// for a grammar without an external scanner.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Func<IExternalScanner>? ExternalScannerCreate { get; set; }
}
