// Port of src/scanner.c of tree-sitter-toml (commit 64b56832c2cffe41758f28e05c756a3a98d16f41,
// grammars/Toml/upstream/src/scanner.c), the external scanner of the TOML grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - The scanner keeps no state: serialize writes nothing and deserialize ignores its buffer,
//   as in C.

namespace TreeSitter.Grammars.Toml;

/// <summary>The external scanner of the TOML grammar (multiline strings and line endings).</summary>
internal sealed class TomlScanner : IExternalScanner
{
    // enum TokenType
    private const int LineEndingOrEof = 0;
    private const int MultilineBasicStringContent = 1;
    private const int MultilineBasicStringEnd = 2;
    private const int MultilineLiteralStringContent = 3;
    private const int MultilineLiteralStringEnd = 4;

    /// <summary>tree_sitter_toml_external_scanner_create</summary>
    public TomlScanner()
    {
    }

    /// <summary>tree_sitter_toml_external_scanner_scan_multiline_string_end</summary>
    private static bool ScanMultilineStringEnd(
        Lexer lexer,
        ReadOnlySpan<bool> validSymbols,
        int delimiter,
        int contentSymbol,
        int endSymbol)
    {
        if (!validSymbols[endSymbol] || lexer.Lookahead != delimiter)
        {
            return false;
        }

        lexer.Advance(false);
        lexer.MarkEnd();

        if (lexer.Lookahead != delimiter)
        {
            lexer.ResultSymbol = (ushort)contentSymbol;
            return true;
        }

        lexer.Advance(false);

        if (lexer.Lookahead != delimiter)
        {
            lexer.MarkEnd();
            lexer.ResultSymbol = (ushort)contentSymbol;
            return true;
        }

        lexer.Advance(false);

        if (lexer.Lookahead != delimiter)
        {
            lexer.MarkEnd();
            lexer.ResultSymbol = (ushort)endSymbol;
            return true;
        }

        lexer.ResultSymbol = (ushort)contentSymbol;
        return true;
    }

    /// <summary>tree_sitter_toml_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (ScanMultilineStringEnd(
                lexer, validSymbols, '"', MultilineBasicStringContent, MultilineBasicStringEnd) ||
            ScanMultilineStringEnd(
                lexer, validSymbols, '\'', MultilineLiteralStringContent, MultilineLiteralStringEnd))
        {
            return true;
        }

        if (validSymbols[LineEndingOrEof])
        {
            lexer.ResultSymbol = LineEndingOrEof;

            while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                lexer.Advance(true);
            }

            if (lexer.Lookahead == 0 || lexer.Lookahead == '\n')
            {
                return true;
            }

            if (lexer.Lookahead == '\r')
            {
                lexer.Advance(true);
                if (lexer.Lookahead == '\n')
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>tree_sitter_toml_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer) => 0;

    /// <summary>tree_sitter_toml_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
    }
}
