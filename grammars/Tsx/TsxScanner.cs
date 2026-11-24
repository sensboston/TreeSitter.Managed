// Port of src/scanner.c of tree-sitter-typescript/tsx (commit 75b3874edb2dc714fb1fd77a32013d0f8699989f,
// grammars/Tsx/upstream/src/scanner.c), the external scanner of the TSX grammar.
// The C file only includes ../../common/scanner.h: the scan is ScannerCommon, which this project
// compiles from grammars/TypeScript/common/ScannerCommon.cs.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - The scanner keeps no state: serialize writes nothing and deserialize ignores its buffer.

using TreeSitter.Grammars.TypeScript;

namespace TreeSitter.Grammars.Tsx;

/// <summary>The external scanner of the TSX grammar.</summary>
internal sealed class TsxScanner : IExternalScanner
{
    /// <summary>tree_sitter_tsx_external_scanner_create</summary>
    public TsxScanner()
    {
    }

    /// <summary>tree_sitter_tsx_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols) =>
        ScannerCommon.ExternalScannerScan(lexer, validSymbols);

    /// <summary>tree_sitter_tsx_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer) => 0;

    /// <summary>tree_sitter_tsx_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
    }
}
