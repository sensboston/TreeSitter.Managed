// Port of src/scanner.c of tree-sitter-css (commit dda5cfc5722c429eaba1c910ca32c2c0c5bb1a3f,
// grammars/Css/upstream/src/scanner.c), the external scanner of the CSS grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - The scanner keeps no state: serialize writes 0 bytes, deserialize does nothing.
// - iswspace/iswalnum of the C locale: the six ASCII space characters, ASCII letters and
//   digits (as in Query.cs).


namespace TreeSitter.Grammars.Css;

/// <summary>The external scanner of the CSS grammar (descendant combinators and pseudo classes).</summary>
internal sealed class CssScanner : IExternalScanner
{
    // enum TokenType
    private const int DescendantOp = 0;
    private const int PseudoClassSelectorColon = 1;
    private const int ErrorRecovery = 2;

    /// <summary>tree_sitter_css_external_scanner_create</summary>
    public CssScanner()
    {
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    // iswspace of the C locale.
    private static bool IsWSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // iswalnum of the C locale.
    private static bool IsWAlNum(int c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    /// <summary>tree_sitter_css_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[ErrorRecovery])
        {
            return false;
        }

        if (IsWSpace(lexer.Lookahead) && validSymbols[DescendantOp])
        {
            lexer.ResultSymbol = DescendantOp;

            Skip(lexer);
            while (IsWSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            lexer.MarkEnd();

            if (lexer.Lookahead == '#' || lexer.Lookahead == '.' || lexer.Lookahead == '[' || lexer.Lookahead == '-' ||
                lexer.Lookahead == '*' || IsWAlNum(lexer.Lookahead))
            {
                return true;
            }

            if (lexer.Lookahead == ':')
            {
                Advance(lexer);
                if (IsWSpace(lexer.Lookahead))
                {
                    return false;
                }

                for (;;)
                {
                    if (lexer.Lookahead == ';' || lexer.Lookahead == '}' || lexer.Eof())
                    {
                        return false;
                    }

                    if (lexer.Lookahead == '{')
                    {
                        return true;
                    }

                    Advance(lexer);
                }
            }
        }

        if (validSymbols[PseudoClassSelectorColon])
        {
            while (IsWSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == ':')
            {
                Advance(lexer);
                if (lexer.Lookahead == ':')
                {
                    return false;
                }

                lexer.MarkEnd();
                lexer.ResultSymbol = PseudoClassSelectorColon;

                // We need a `{` to be a pseudo class selector, a `;` indicates a property.
                // This does not apply if we're in a comment, however.
                bool inComment = false;
                while (lexer.Lookahead != ';' && lexer.Lookahead != '}' && !lexer.Eof())
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '{' && !inComment)
                    {
                        return true;
                    }

                    if (lexer.Lookahead == '/' && !inComment)
                    {
                        Advance(lexer);
                        if (lexer.Lookahead == '*')
                        {
                            inComment = true;
                        }
                    }
                    else if (lexer.Lookahead == '*' && inComment)
                    {
                        Advance(lexer);
                        if (lexer.Lookahead == '/')
                        {
                            inComment = false;
                        }
                    }
                }

                // If we're at eof, and we happened to *not* find an opening brace to indicate we have a pseudo class
                // selector, we should *still* return one at EOF. This will improve error recovery, and the malformed code
                // can be parsed as an erroneous pseudo-class selector, rather than an erroneous property.
                return lexer.Eof();
            }
        }

        return false;
    }

    /// <summary>tree_sitter_css_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer) => 0;

    /// <summary>tree_sitter_css_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
    }
}
