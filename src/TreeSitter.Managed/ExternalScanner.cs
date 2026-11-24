// The external scanner contract of the grammar ABI (parser.h: TSLanguage.external_scanner)
// and the parse options of the public API (api.h: TSParseOptions, TSParseState).
// - create is LanguageTables.ExternalScannerCreate, destroy is IDisposable.Dispose when the
//   scanner implements it, the other functions are the methods of IExternalScanner.
// - The serialization buffer has TREE_SITTER_SERIALIZATION_BUFFER_SIZE (1024) bytes.

namespace TreeSitter;

/// <summary>
/// An external scanner (TSLanguage.external_scanner): the hand-written lexer of a grammar.
/// The state written by <see cref="Serialize"/> must be the same bytes as the C scanner writes.
/// </summary>
public interface IExternalScanner
{
    /// <summary>external_scanner.scan</summary>
    /// <param name="lexer">The lexer, positioned at the start of the token.</param>
    /// <param name="validSymbols">One entry per external token: true when it is valid here.</param>
    /// <returns>True when a token was found; its symbol is in <see cref="Lexer.ResultSymbol"/>.</returns>
    bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols);

    /// <summary>external_scanner.serialize</summary>
    /// <param name="buffer">The buffer of 1024 bytes (TREE_SITTER_SERIALIZATION_BUFFER_SIZE).</param>
    /// <returns>The number of bytes written.</returns>
    uint Serialize(Span<byte> buffer);

    /// <summary>external_scanner.deserialize</summary>
    /// <param name="buffer">The state written by <see cref="Serialize"/>, or empty.</param>
    void Deserialize(ReadOnlySpan<byte> buffer);
}

/// <summary>TSParseState: what a progress callback sees.</summary>
public sealed class ParseState
{
    internal ParseState()
    {
    }

    /// <summary>TSParseState.current_byte_offset</summary>
    public uint CurrentByteOffset { get; internal set; }

    /// <summary>TSParseState.has_error</summary>
    public bool HasError { get; internal set; }
}

/// <summary>TSParseOptions</summary>
public sealed class ParseOptions
{
    /// <summary>
    /// TSParseOptions.progress_callback: called every 100 parse operations; returning true
    /// stops the parse (Parse returns null and the parse can be resumed).
    /// </summary>
    public Func<ParseState, bool>? ProgressCallback { get; init; }
}
