// Port of src/scanner.c of tree-sitter-markdown/tree-sitter-markdown-inline (commit 41ff24a6c775492c543b09228aeb98d1cd2b3a0f,
// grammars/MarkdownInline/upstream/src/scanner.c), the external scanner of the Markdown (inline content) grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - The serialized state is the same bytes as the C scanner writes.

namespace TreeSitter.Grammars.MarkdownInline;

/// <summary>The external scanner of the Markdown (inline content) grammar.</summary>
internal sealed class MarkdownInlineScanner : IExternalScanner
{
    // enum TokenType
    private const int Error = 0;
    private const int TriggerError = 1;
    private const int CodeSpanStart = 2;
    private const int CodeSpanClose = 3;
    private const int EmphasisOpenStar = 4;
    private const int EmphasisOpenUnderscore = 5;
    private const int EmphasisCloseStar = 6;
    private const int EmphasisCloseUnderscore = 7;
    private const int LastTokenWhitespace = 8;
    private const int LastTokenPunctuation = 9;
    private const int StrikethroughOpen = 10;
    private const int StrikethroughClose = 11;
    private const int LatexSpanStart = 12;
    private const int LatexSpanClose = 13;
    private const int UnclosedSpan = 14;

    // Determines if a character is punctuation as defined by the markdown spec.
    private static bool IsPunctuation(int chr) =>
        (chr >= '!' && chr <= '/') || (chr >= ':' && chr <= '@') ||
        (chr >= '[' && chr <= '`') || (chr >= '{' && chr <= '~');

    // State bitflags used with _state

    // TODO
    private const byte StateEmphasisDelimiterMod3 = 0x3;

    // Current delimiter run is opening
    private const byte StateEmphasisDelimiterIsOpen = 0x1 << 2;

    // Parser state flags
    private byte _state;
    private byte _codeSpanDelimiterLength;
    private byte _latexSpanDelimiterLength;

    // The number of characters remaining in the currrent emphasis delimiter
    // run.
    private byte _numEmphasisDelimitersLeft;

    /// <summary>tree_sitter_markdown_inline_external_scanner_create</summary>
    public MarkdownInlineScanner()
    {
        Deserialize(ReadOnlySpan<byte>.Empty);
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    // Convenience function to emit the error token. This is done to stop invalid
    // parse branches. Specifically:
    // 1. When encountering a newline after a line break that ended a paragraph, and
    // no new block
    //    has been opened.
    // 2. When encountering a new block after a soft line break.
    // 3. When a `$._trigger_error` token is valid, which is used to stop parse
    // branches through
    //    normal tree-sitter grammar rules.
    //
    // See also the `$._soft_line_break` and `$._paragraph_end_newline` tokens in
    // grammar.js
    private static bool EmitError(Lexer lexer)
    {
        lexer.ResultSymbol = Error;
        return true;
    }

    private static bool ParseLeafDelimiter(
        Lexer lexer,
        ref byte delimiterLength,
        ReadOnlySpan<bool> validSymbols,
        char delimiter,
        int openToken,
        int closeToken)
    {
        byte level = 0;
        while (lexer.Lookahead == delimiter)
        {
            Advance(lexer);
            level++;
        }

        lexer.MarkEnd();
        if (level == delimiterLength && validSymbols[closeToken])
        {
            delimiterLength = 0;
            lexer.ResultSymbol = (ushort)closeToken;
            return true;
        }

        if (validSymbols[openToken])
        {
            // Parse ahead to check if there is a closing delimiter
            int closeLevel = 0;
            while (!lexer.Eof())
            {
                if (lexer.Lookahead == delimiter)
                {
                    closeLevel++;
                }
                else
                {
                    if (closeLevel == level)
                    {
                        // Found a matching delimiter
                        break;
                    }

                    closeLevel = 0;
                }

                Advance(lexer);
            }

            if (closeLevel == level)
            {
                delimiterLength = level;
                lexer.ResultSymbol = (ushort)openToken;
                return true;
            }

            if (validSymbols[UnclosedSpan])
            {
                lexer.ResultSymbol = (ushort)UnclosedSpan;
                return true;
            }
        }

        return false;
    }

    private bool ParseBacktick(Lexer lexer, ReadOnlySpan<bool> validSymbols) =>
        ParseLeafDelimiter(lexer, ref _codeSpanDelimiterLength, validSymbols, '`', CodeSpanStart, CodeSpanClose);

    private bool ParseDollar(Lexer lexer, ReadOnlySpan<bool> validSymbols) =>
        ParseLeafDelimiter(lexer, ref _latexSpanDelimiterLength, validSymbols, '$', LatexSpanStart, LatexSpanClose);

    private bool ParseStar(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        Advance(lexer);

        // If `_numEmphasisDelimitersLeft` is not zero then we already decided
        // that this should be part of an emphasis delimiter run, so interpret it as
        // such.
        if (_numEmphasisDelimitersLeft > 0)
        {
            // The `StateEmphasisDelimiterIsOpen` state flag tells us wether it
            // should be open or close.
            if ((_state & StateEmphasisDelimiterIsOpen) != 0 && validSymbols[EmphasisOpenStar])
            {
                _state = (byte)(_state & ~StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)EmphasisOpenStar;
                _numEmphasisDelimitersLeft--;
                return true;
            }

            if (validSymbols[EmphasisCloseStar])
            {
                lexer.ResultSymbol = (ushort)EmphasisCloseStar;
                _numEmphasisDelimitersLeft--;
                return true;
            }
        }

        lexer.MarkEnd();

        // Otherwise count the number of stars
        byte starCount = 1;
        while (lexer.Lookahead == '*')
        {
            starCount++;
            Advance(lexer);
        }

        bool lineEnd = lexer.Lookahead == '\n' || lexer.Lookahead == '\r' ||
                       lexer.Eof();
        if (validSymbols[EmphasisOpenStar] ||
            validSymbols[EmphasisCloseStar])
        {
            // The desicion made for the first star also counts for all the
            // following stars in the delimiter run. Rembemer how many there are.
            _numEmphasisDelimitersLeft = (byte)(starCount - 1);

            // Look ahead to the next symbol (after the last star) to find out if it
            // is whitespace punctuation or other.
            bool nextSymbolWhitespace =
                lineEnd || lexer.Lookahead == ' ' || lexer.Lookahead == '\t';
            bool nextSymbolPunctuation = IsPunctuation(lexer.Lookahead);

            // Information about the last token is in valid_symbols. See grammar.js
            // for these tokens for how this is done.
            if (validSymbols[EmphasisCloseStar] &&
                !validSymbols[LastTokenWhitespace] &&
                (!validSymbols[LastTokenPunctuation] ||
                 nextSymbolPunctuation || nextSymbolWhitespace))
            {
                // Closing delimiters take precedence
                _state = (byte)(_state & ~StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)EmphasisCloseStar;
                return true;
            }

            if (!nextSymbolWhitespace && (!nextSymbolPunctuation ||
                                         validSymbols[LastTokenPunctuation] ||
                                         validSymbols[LastTokenWhitespace]))
            {
                _state = (byte)(_state | StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)EmphasisOpenStar;
                return true;
            }
        }

        return false;
    }

    private bool ParseTilde(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        Advance(lexer);

        // If `_numEmphasisDelimitersLeft` is not zero then we already decided
        // that this should be part of an emphasis delimiter run, so interpret it as
        // such.
        if (_numEmphasisDelimitersLeft > 0)
        {
            // The `StateEmphasisDelimiterIsOpen` state flag tells us wether it
            // should be open or close.
            if ((_state & StateEmphasisDelimiterIsOpen) != 0 && validSymbols[StrikethroughOpen])
            {
                _state = (byte)(_state & ~StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)StrikethroughOpen;
                _numEmphasisDelimitersLeft--;
                return true;
            }

            if (validSymbols[StrikethroughClose])
            {
                lexer.ResultSymbol = (ushort)StrikethroughClose;
                _numEmphasisDelimitersLeft--;
                return true;
            }
        }

        lexer.MarkEnd();

        // Otherwise count the number of tildes
        byte starCount = 1;
        while (lexer.Lookahead == '~')
        {
            starCount++;
            Advance(lexer);
        }

        bool lineEnd = lexer.Lookahead == '\n' || lexer.Lookahead == '\r' ||
                       lexer.Eof();
        if (validSymbols[StrikethroughOpen] ||
            validSymbols[StrikethroughClose])
        {
            // The desicion made for the first star also counts for all the
            // following stars in the delimiter run. Rembemer how many there are.
            _numEmphasisDelimitersLeft = (byte)(starCount - 1);

            // Look ahead to the next symbol (after the last star) to find out if it
            // is whitespace punctuation or other.
            bool nextSymbolWhitespace =
                lineEnd || lexer.Lookahead == ' ' || lexer.Lookahead == '\t';
            bool nextSymbolPunctuation = IsPunctuation(lexer.Lookahead);

            // Information about the last token is in valid_symbols. See grammar.js
            // for these tokens for how this is done.
            if (validSymbols[StrikethroughClose] &&
                !validSymbols[LastTokenWhitespace] &&
                (!validSymbols[LastTokenPunctuation] ||
                 nextSymbolPunctuation || nextSymbolWhitespace))
            {
                // Closing delimiters take precedence
                _state = (byte)(_state & ~StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)StrikethroughClose;
                return true;
            }

            if (!nextSymbolWhitespace && (!nextSymbolPunctuation ||
                                         validSymbols[LastTokenPunctuation] ||
                                         validSymbols[LastTokenWhitespace]))
            {
                _state = (byte)(_state | StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)StrikethroughOpen;
                return true;
            }
        }

        return false;
    }

    private bool ParseUnderscore(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        Advance(lexer);

        // If `_numEmphasisDelimitersLeft` is not zero then we already decided
        // that this should be part of an emphasis delimiter run, so interpret it as
        // such.
        if (_numEmphasisDelimitersLeft > 0)
        {
            // The `StateEmphasisDelimiterIsOpen` state flag tells us wether it
            // should be open or close.
            if ((_state & StateEmphasisDelimiterIsOpen) != 0 && validSymbols[EmphasisOpenUnderscore])
            {
                _state = (byte)(_state & ~StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)EmphasisOpenUnderscore;
                _numEmphasisDelimitersLeft--;
                return true;
            }

            if (validSymbols[EmphasisCloseUnderscore])
            {
                lexer.ResultSymbol = (ushort)EmphasisCloseUnderscore;
                _numEmphasisDelimitersLeft--;
                return true;
            }
        }

        lexer.MarkEnd();

        // Otherwise count the number of stars
        byte underscoreCount = 1;
        while (lexer.Lookahead == '_')
        {
            underscoreCount++;
            Advance(lexer);
        }

        bool lineEnd = lexer.Lookahead == '\n' || lexer.Lookahead == '\r' ||
                       lexer.Eof();
        if (validSymbols[EmphasisOpenUnderscore] ||
            validSymbols[EmphasisCloseUnderscore])
        {
            // The desicion made for the first underscore also counts for all the
            // following underscores in the delimiter run. Rembemer how many there are.
            _numEmphasisDelimitersLeft = (byte)(underscoreCount - 1);

            // Look ahead to the next symbol (after the last underscore) to find out if it
            // is whitespace punctuation or other.
            bool nextSymbolWhitespace =
                lineEnd || lexer.Lookahead == ' ' || lexer.Lookahead == '\t';
            bool nextSymbolPunctuation = IsPunctuation(lexer.Lookahead);

            // Information about the last token is in valid_symbols. See grammar.js
            // for these tokens for how this is done.
            if (validSymbols[EmphasisCloseUnderscore] &&
                !validSymbols[LastTokenWhitespace] &&
                (!validSymbols[LastTokenPunctuation] ||
                 nextSymbolPunctuation || nextSymbolWhitespace))
            {
                // Closing delimiters take precedence
                _state = (byte)(_state & ~StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)EmphasisCloseUnderscore;
                return true;
            }

            if (!nextSymbolWhitespace && (!nextSymbolPunctuation ||
                                         validSymbols[LastTokenPunctuation] ||
                                         validSymbols[LastTokenWhitespace]))
            {
                _state = (byte)(_state | StateEmphasisDelimiterIsOpen);
                lexer.ResultSymbol = (ushort)EmphasisOpenUnderscore;
                return true;
            }
        }

        return false;
    }

    /// <summary>tree_sitter_markdown_inline_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // A normal tree-sitter rule decided that the current branch is invalid and
        // now "requests" an error to stop the branch
        if (validSymbols[TriggerError])
        {
            return EmitError(lexer);
        }

        // Decide which tokens to consider based on the first non-whitespace
        // character
        switch (lexer.Lookahead)
        {
            case '`':
                // A backtick could mark the beginning or ending of a code span or a
                // fenced code block.
                return ParseBacktick(lexer, validSymbols);
            case '$':
                return ParseDollar(lexer, validSymbols);
            case '*':
                // A star could either mark the beginning or ending of emphasis, a
                // list item or thematic break. This code is similar to the code for
                // '_' and '+'.
                return ParseStar(lexer, validSymbols);
            case '_':
                return ParseUnderscore(lexer, validSymbols);
            case '~':
                return ParseTilde(lexer, validSymbols);
        }

        return false;
    }

    /// <summary>tree_sitter_markdown_inline_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        int size = 0;
        buffer[size++] = _state;
        buffer[size++] = _codeSpanDelimiterLength;
        buffer[size++] = _latexSpanDelimiterLength;
        buffer[size++] = _numEmphasisDelimitersLeft;
        return (uint)size;
    }

    /// <summary>tree_sitter_markdown_inline_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        _state = 0;
        _codeSpanDelimiterLength = 0;
        _latexSpanDelimiterLength = 0;
        _numEmphasisDelimitersLeft = 0;
        if (buffer.Length > 0)
        {
            int size = 0;
            _state = buffer[size++];
            _codeSpanDelimiterLength = buffer[size++];
            _latexSpanDelimiterLength = buffer[size++];
            _numEmphasisDelimitersLeft = buffer[size++];
        }
    }
}
