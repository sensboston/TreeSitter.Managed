// Port of src/scanner.c of tree-sitter-rust (commit 77a3747266f4d621d0757825e6b11edcbf991ca5,
// grammars/Rust/upstream/src/scanner.c), the external scanner of the Rust grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - uint8_t opening_hash_count is the byte field _openingHashCount; the serialized state is
//   the same 1 byte as the C scanner writes.
// - iswspace/iswdigit/iswalpha of the C locale: the six ASCII space characters, ASCII digits
//   and ASCII letters (as in Query.cs).


namespace TreeSitter.Grammars.Rust;

/// <summary>The external scanner of the Rust grammar (strings, floats and doc comments).</summary>
internal sealed class RustScanner : IExternalScanner
{
    // enum TokenType
    private const int StringContent = 0;
    private const int StringClose = 1;
    private const int RawStringLiteralStart = 2;
    private const int RawStringLiteralContent = 3;
    private const int RawStringLiteralEnd = 4;
    private const int FloatLiteral = 5;
    private const int BlockOuterDocMarker = 6;
    private const int BlockInnerDocMarker = 7;
    private const int BlockCommentContent = 8;
    private const int LineDocContent = 9;
    private const int ErrorSentinel = 10;

    private byte _openingHashCount;

    /// <summary>tree_sitter_rust_external_scanner_create</summary>
    public RustScanner()
    {
    }

    // is_num_char
    private static bool IsNumChar(int c) => c == '_' || IsWDigit(c);

    // iswspace of the C locale.
    private static bool IsWSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // iswdigit of the C locale.
    private static bool IsWDigit(int c) => c >= '0' && c <= '9';

    // iswalpha of the C locale.
    private static bool IsWAlpha(int c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    /// <summary>tree_sitter_rust_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        buffer[0] = _openingHashCount;
        return 1;
    }

    /// <summary>tree_sitter_rust_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        _openingHashCount = 0;
        if (buffer.Length == 1)
        {
            _openingHashCount = buffer[0];
        }
    }

    // process_string
    private static bool ProcessString(Lexer lexer)
    {
        bool hasContent = false;
        for (;;)
        {
            if (lexer.Lookahead == '"' || lexer.Lookahead == '\\')
            {
                break;
            }

            if (lexer.Eof())
            {
                return false;
            }

            hasContent = true;
            Advance(lexer);
        }

        lexer.ResultSymbol = StringContent;
        lexer.MarkEnd();
        return hasContent;
    }

    // scan_raw_string_start
    private bool ScanRawStringStart(Lexer lexer)
    {
        if (lexer.Lookahead == 'b' || lexer.Lookahead == 'c')
        {
            Advance(lexer);
        }

        if (lexer.Lookahead != 'r')
        {
            return false;
        }

        Advance(lexer);

        byte openingHashCount = 0;
        while (lexer.Lookahead == '#')
        {
            Advance(lexer);
            openingHashCount++;
        }

        if (lexer.Lookahead != '"')
        {
            return false;
        }

        Advance(lexer);
        _openingHashCount = openingHashCount;

        lexer.ResultSymbol = RawStringLiteralStart;
        return true;
    }

    // scan_raw_string_content
    private bool ScanRawStringContent(Lexer lexer)
    {
        for (;;)
        {
            if (lexer.Eof())
            {
                return false;
            }

            if (lexer.Lookahead == '"')
            {
                lexer.MarkEnd();
                Advance(lexer);
                uint hashCount = 0;
                while (lexer.Lookahead == '#' && hashCount < _openingHashCount)
                {
                    Advance(lexer);
                    hashCount++;
                }

                if (hashCount == _openingHashCount)
                {
                    lexer.ResultSymbol = RawStringLiteralContent;
                    return true;
                }
            }
            else
            {
                Advance(lexer);
            }
        }
    }

    // scan_raw_string_end
    private bool ScanRawStringEnd(Lexer lexer)
    {
        Advance(lexer);
        for (uint i = 0; i < _openingHashCount; i++)
        {
            Advance(lexer);
        }

        lexer.ResultSymbol = RawStringLiteralEnd;
        return true;
    }

    // process_float_literal
    private static bool ProcessFloatLiteral(Lexer lexer)
    {
        lexer.ResultSymbol = FloatLiteral;

        Advance(lexer);
        while (IsNumChar(lexer.Lookahead))
        {
            Advance(lexer);
        }

        bool hasFraction = false, hasExponent = false;

        if (lexer.Lookahead == '.')
        {
            hasFraction = true;
            Advance(lexer);
            if (IsWAlpha(lexer.Lookahead))
            {
                // The dot is followed by a letter: 1.max(2) => not a float but an integer
                return false;
            }

            if (lexer.Lookahead == '.')
            {
                return false;
            }

            while (IsNumChar(lexer.Lookahead))
            {
                Advance(lexer);
            }
        }

        lexer.MarkEnd();

        if (lexer.Lookahead == 'e' || lexer.Lookahead == 'E')
        {
            hasExponent = true;
            Advance(lexer);
            if (lexer.Lookahead == '+' || lexer.Lookahead == '-')
            {
                Advance(lexer);
            }

            if (!IsNumChar(lexer.Lookahead))
            {
                return true;
            }

            Advance(lexer);
            while (IsNumChar(lexer.Lookahead))
            {
                Advance(lexer);
            }

            lexer.MarkEnd();
        }

        if (!hasExponent && !hasFraction)
        {
            return false;
        }

        if (lexer.Lookahead != 'u' && lexer.Lookahead != 'i' && lexer.Lookahead != 'f')
        {
            return true;
        }

        Advance(lexer);
        if (!IsWDigit(lexer.Lookahead))
        {
            return true;
        }

        while (IsWDigit(lexer.Lookahead))
        {
            Advance(lexer);
        }

        lexer.MarkEnd();
        return true;
    }

    // process_line_doc_content
    private static bool ProcessLineDocContent(Lexer lexer)
    {
        lexer.ResultSymbol = LineDocContent;
        for (;;)
        {
            if (lexer.Eof())
            {
                return true;
            }

            if (lexer.Lookahead == '\n')
            {
                // Include the newline in the doc content node.
                // Line endings are useful for markdown injection.
                Advance(lexer);
                return true;
            }

            Advance(lexer);
        }
    }

    private enum BlockCommentState
    {
        LeftForwardSlash,
        LeftAsterisk,
        Continuing,
    }

    private struct BlockCommentProcessing
    {
        public BlockCommentState State;
        public uint NestingDepth;
    }

    // process_left_forward_slash. current is C's char parameter: the lookahead truncated
    // to char (signed 8-bit), sign-extended back to int.
    private static void ProcessLeftForwardSlash(ref BlockCommentProcessing processing, int current)
    {
        if (current == '*')
        {
            processing.NestingDepth += 1;
        }

        processing.State = BlockCommentState.Continuing;
    }

    // process_left_asterisk
    private static void ProcessLeftAsterisk(ref BlockCommentProcessing processing, int current, Lexer lexer)
    {
        if (current == '*')
        {
            lexer.MarkEnd();
            processing.State = BlockCommentState.LeftAsterisk;
            return;
        }

        if (current == '/')
        {
            processing.NestingDepth -= 1;
        }

        processing.State = BlockCommentState.Continuing;
    }

    // process_continuing
    private static void ProcessContinuing(ref BlockCommentProcessing processing, int current)
    {
        switch (current)
        {
            case '/':
                processing.State = BlockCommentState.LeftForwardSlash;
                break;
            case '*':
                processing.State = BlockCommentState.LeftAsterisk;
                break;
        }
    }

    // process_block_comment
    private static bool ProcessBlockComment(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // As in C, the lookahead is truncated to char (signed 8-bit) and sign-extended.
        int first = (sbyte)lexer.Lookahead;
        // The first character is stored so we can safely advance inside
        // these if blocks. However, because we only store one, we can only
        // safely advance 1 time. Since there's a chance that an advance could
        // happen in one state, we must advance in all states to ensure that
        // the program ends up in a sane state prior to processing the block
        // comment if need be.
        if (validSymbols[BlockInnerDocMarker] && first == '!')
        {
            lexer.ResultSymbol = BlockInnerDocMarker;
            Advance(lexer);
            return true;
        }

        if (validSymbols[BlockOuterDocMarker] && first == '*')
        {
            Advance(lexer);
            lexer.MarkEnd();
            // If the next token is a / that means that it's an empty block comment.
            if (lexer.Lookahead == '/')
            {
                return false;
            }

            // If the next token is a * that means that this isn't a BLOCK_OUTER_DOC_MARKER
            // as BLOCK_OUTER_DOC_MARKER's only have 2 * not 3 or more.
            if (lexer.Lookahead != '*')
            {
                lexer.ResultSymbol = BlockOuterDocMarker;
                return true;
            }
        }
        else
        {
            Advance(lexer);
        }

        if (validSymbols[BlockCommentContent])
        {
            BlockCommentProcessing processing = new BlockCommentProcessing();
            processing.State = BlockCommentState.Continuing;
            processing.NestingDepth = 1;
            // Manually set the current state based on the first character
            switch (first)
            {
                case '*':
                    processing.State = BlockCommentState.LeftAsterisk;
                    if (lexer.Lookahead == '/')
                    {
                        // This case can happen in an empty doc block comment
                        // like /*!*/. The comment has no contents, so bail.
                        return false;
                    }

                    break;
                case '/':
                    processing.State = BlockCommentState.LeftForwardSlash;
                    break;
                default:
                    processing.State = BlockCommentState.Continuing;
                    break;
            }

            // For the purposes of actually parsing rust code, this
            // is incorrect as it considers an unterminated block comment
            // to be an error. However, for the purposes of syntax highlighting
            // this should be considered successful as otherwise you are not able
            // to syntax highlight a block of code prior to closing the
            // block comment
            while (!lexer.Eof() && processing.NestingDepth != 0)
            {
                // Set first to the current lookahead as that is the second character
                // as we force an advance in the above code when we are checking if we
                // need to handle a block comment inner or outer doc comment signifier
                // node
                first = (sbyte)lexer.Lookahead;
                switch (processing.State)
                {
                    case BlockCommentState.LeftForwardSlash:
                        ProcessLeftForwardSlash(ref processing, first);
                        break;
                    case BlockCommentState.LeftAsterisk:
                        ProcessLeftAsterisk(ref processing, first, lexer);
                        break;
                    case BlockCommentState.Continuing:
                        lexer.MarkEnd();
                        ProcessContinuing(ref processing, first);
                        break;
                    default:
                        break;
                }

                Advance(lexer);
                if (first == '/' && processing.NestingDepth != 0)
                {
                    lexer.MarkEnd();
                }
            }

            lexer.ResultSymbol = BlockCommentContent;
            return true;
        }

        return false;
    }

    /// <summary>tree_sitter_rust_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // The documentation states that if the lexical analysis fails for some reason
        // they will mark every state as valid and pass it to the external scanner
        // However, we can't do anything to help them recover in that case so we
        // should just fail.
        /*
          link: https://tree-sitter.github.io/tree-sitter/creating-parsers#external-scanners
          If a syntax error is encountered during regular parsing, Tree-sitter’s
          first action during error recovery will be to call the external scanner’s
          scan function with all tokens marked valid. The scanner should detect this
          case and handle it appropriately. One simple method of detection is to add
          an unused token to the end of the externals array, for example

          externals: $ => [$.token1, $.token2, $.error_sentinel],

          then check whether that token is marked valid to determine whether
          Tree-sitter is in error correction mode.
        */
        if (validSymbols[ErrorSentinel])
        {
            return false;
        }

        RustScanner scanner = this;

        if (validSymbols[BlockCommentContent] || validSymbols[BlockInnerDocMarker] ||
            validSymbols[BlockOuterDocMarker])
        {
            return ProcessBlockComment(lexer, validSymbols);
        }

        if (validSymbols[StringContent] && !validSymbols[FloatLiteral])
        {
            if (ProcessString(lexer))
            {
                return true;
            }

            // process_string returns false when the next char is '"' or '\' (no
            // content to emit). Fall through so STRING_CLOSE can consume the '"'.
        }

        if (validSymbols[StringClose] && lexer.Lookahead == '"')
        {
            Advance(lexer);
            lexer.ResultSymbol = StringClose;
            lexer.MarkEnd();
            return true;
        }

        if (validSymbols[LineDocContent])
        {
            return ProcessLineDocContent(lexer);
        }

        while (IsWSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        if (validSymbols[RawStringLiteralStart] &&
            (lexer.Lookahead == 'r' || lexer.Lookahead == 'b' || lexer.Lookahead == 'c'))
        {
            return scanner.ScanRawStringStart(lexer);
        }

        if (validSymbols[RawStringLiteralContent])
        {
            return scanner.ScanRawStringContent(lexer);
        }

        if (validSymbols[RawStringLiteralEnd] && lexer.Lookahead == '"')
        {
            return scanner.ScanRawStringEnd(lexer);
        }

        if (validSymbols[FloatLiteral] && IsWDigit(lexer.Lookahead))
        {
            return ProcessFloatLiteral(lexer);
        }

        return false;
    }
}
