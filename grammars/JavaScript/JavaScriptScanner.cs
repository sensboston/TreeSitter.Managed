// Port of src/scanner.c of tree-sitter-javascript (commit 58404d8cf191d69f2674a8fd507bd5776f46cb11,
// grammars/JavaScript/upstream/src/scanner.c), the external scanner of the JavaScript grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - The scanner keeps no state: serialize writes 0 bytes, deserialize does nothing.
// - iswspace/iswdigit/iswalpha of the C locale: the six ASCII space characters, ASCII digits
//   and ASCII letters (as in Query.cs).


namespace TreeSitter.Grammars.JavaScript;

/// <summary>The external scanner of the JavaScript grammar (semicolons, templates, regexes, JSX text).</summary>
internal sealed class JavaScriptScanner : IExternalScanner
{
    // enum TokenType
    private const int AutomaticSemicolon = 0;
    private const int TemplateChars = 1;
    private const int TernaryQmark = 2;
    private const int HtmlComment = 3;
    private const int LogicalOr = 4;
    private const int EscapeSequence = 5;
    private const int RegexPattern = 6;
    private const int JsxText = 7;

    /// <summary>tree_sitter_javascript_external_scanner_create</summary>
    public JavaScriptScanner()
    {
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    // iswspace of the C locale.
    private static bool IsWSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // iswdigit of the C locale.
    private static bool IsWDigit(int c) => c >= '0' && c <= '9';

    // iswalpha of the C locale.
    private static bool IsWAlpha(int c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    // scan_template_chars
    private static bool ScanTemplateChars(Lexer lexer)
    {
        lexer.ResultSymbol = TemplateChars;
        for (bool hasContent = false;; hasContent = true)
        {
            lexer.MarkEnd();
            switch (lexer.Lookahead)
            {
                case '`':
                    return hasContent;
                case '\0':
                    return false;
                case '$':
                    Advance(lexer);
                    if (lexer.Lookahead == '{')
                    {
                        return hasContent;
                    }

                    break;
                case '\\':
                    return hasContent;
                default:
                    Advance(lexer);
                    break;
            }
        }
    }

    private enum WhitespaceResult
    {
        Reject,     // Semicolon is illegal, ie a syntax error occurred
        NoNewline,  // Unclear if semicolon will be legal, continue
        Accept,     // Semicolon is legal, assuming a comment was encountered
    }

    /**
     * @param consume If false, only consume enough to check if comment indicates semicolon-legality
     */
    // scan_whitespace_and_comments
    private static WhitespaceResult ScanWhitespaceAndComments(Lexer lexer, ref bool scannedComment, bool consume)
    {
        bool sawBlockNewline = false;

        for (;;)
        {
            while (IsWSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '/')
            {
                Skip(lexer);

                if (lexer.Lookahead == '/')
                {
                    Skip(lexer);
                    while (lexer.Lookahead != 0 && lexer.Lookahead != '\n' && lexer.Lookahead != 0x2028 &&
                           lexer.Lookahead != 0x2029)
                    {
                        Skip(lexer);
                    }

                    scannedComment = true;
                }
                else if (lexer.Lookahead == '*')
                {
                    Skip(lexer);
                    while (lexer.Lookahead != 0)
                    {
                        if (lexer.Lookahead == '*')
                        {
                            Skip(lexer);
                            if (lexer.Lookahead == '/')
                            {
                                Skip(lexer);
                                scannedComment = true;

                                if (lexer.Lookahead != '/' && !consume)
                                {
                                    return sawBlockNewline ? WhitespaceResult.Accept : WhitespaceResult.NoNewline;
                                }

                                break;
                            }
                        }
                        else if (lexer.Lookahead == '\n' || lexer.Lookahead == 0x2028 || lexer.Lookahead == 0x2029)
                        {
                            sawBlockNewline = true;
                            Skip(lexer);
                        }
                        else
                        {
                            Skip(lexer);
                        }
                    }
                }
                else
                {
                    return WhitespaceResult.Reject;
                }
            }
            else
            {
                return WhitespaceResult.Accept;
            }
        }
    }

    // scan_automatic_semicolon
    private static bool ScanAutomaticSemicolon(Lexer lexer, bool commentCondition, ref bool scannedComment)
    {
        lexer.ResultSymbol = AutomaticSemicolon;
        lexer.MarkEnd();

        for (;;)
        {
            if (lexer.Lookahead == 0)
            {
                return true;
            }

            if (lexer.Lookahead == '/')
            {
                WhitespaceResult result = ScanWhitespaceAndComments(lexer, ref scannedComment, false);
                if (result == WhitespaceResult.Reject)
                {
                    return false;
                }

                if (result == WhitespaceResult.Accept && commentCondition && lexer.Lookahead != ',' && lexer.Lookahead != '=')
                {
                    return true;
                }
            }

            if (lexer.Lookahead == '}')
            {
                return true;
            }

            if (lexer.IsAtIncludedRangeStart())
            {
                return true;
            }

            if (lexer.Lookahead == '\n' || lexer.Lookahead == 0x2028 || lexer.Lookahead == 0x2029)
            {
                break;
            }

            if (!IsWSpace(lexer.Lookahead))
            {
                return false;
            }

            Skip(lexer);
        }

        Skip(lexer);

        if (ScanWhitespaceAndComments(lexer, ref scannedComment, true) == WhitespaceResult.Reject)
        {
            return false;
        }

        switch (lexer.Lookahead)
        {
            case '`':
            case ',':
            case ':':
            case ';':
            case '*':
            case '%':
            case '>':
            case '<':
            case '=':
            case '[':
            case '(':
            case '?':
            case '^':
            case '|':
            case '&':
            case '/':
                return false;

            // Insert a semicolon before decimals literals but not otherwise.
            case '.':
                Skip(lexer);
                return IsWDigit(lexer.Lookahead);

            // Insert a semicolon before `--` and `++`, but not before binary `+` or `-`.
            case '+':
                Skip(lexer);
                return lexer.Lookahead == '+';
            case '-':
                Skip(lexer);
                return lexer.Lookahead == '-';

            // Don't insert a semicolon before `!=`, but do insert one before a unary `!`.
            case '!':
                Skip(lexer);
                return lexer.Lookahead != '=';

            // Don't insert a semicolon before `in` or `instanceof`, but do insert one
            // before an identifier.
            case 'i':
                Skip(lexer);

                if (lexer.Lookahead != 'n')
                {
                    return true;
                }

                Skip(lexer);

                if (!IsWAlpha(lexer.Lookahead))
                {
                    return false;
                }

                for (int i = 0; i < 8; i++)
                {
                    if (lexer.Lookahead != "stanceof"[i])
                    {
                        return true;
                    }

                    Skip(lexer);
                }

                if (!IsWAlpha(lexer.Lookahead))
                {
                    return false;
                }

                break;

            default:
                break;
        }

        return true;
    }

    // scan_ternary_qmark
    private static bool ScanTernaryQmark(Lexer lexer)
    {
        for (;;)
        {
            if (!IsWSpace(lexer.Lookahead))
            {
                break;
            }

            Skip(lexer);
        }

        if (lexer.Lookahead == '?')
        {
            Advance(lexer);

            if (lexer.Lookahead == '?')
            {
                return false;
            }

            lexer.MarkEnd();
            lexer.ResultSymbol = TernaryQmark;

            if (lexer.Lookahead == '.')
            {
                Advance(lexer);
                if (IsWDigit(lexer.Lookahead))
                {
                    return true;
                }

                return false;
            }

            return true;
        }

        return false;
    }

    // scan_html_comment
    private static bool ScanHtmlComment(Lexer lexer)
    {
        while (IsWSpace(lexer.Lookahead) || lexer.Lookahead == 0x2028 || lexer.Lookahead == 0x2029)
        {
            Skip(lexer);
        }

        const string CommentStart = "<!--";
        const string CommentEnd = "-->";

        if (lexer.Lookahead == '<')
        {
            for (int i = 0; i < 4; i++)
            {
                if (lexer.Lookahead != CommentStart[i])
                {
                    return false;
                }

                Advance(lexer);
            }
        }
        else if (lexer.Lookahead == '-')
        {
            for (int i = 0; i < 3; i++)
            {
                if (lexer.Lookahead != CommentEnd[i])
                {
                    return false;
                }

                Advance(lexer);
            }
        }
        else
        {
            return false;
        }

        while (lexer.Lookahead != 0 && lexer.Lookahead != '\n' && lexer.Lookahead != 0x2028 &&
               lexer.Lookahead != 0x2029)
        {
            Advance(lexer);
        }

        lexer.ResultSymbol = HtmlComment;
        lexer.MarkEnd();

        return true;
    }

    // scan_jsx_text
    private static bool ScanJsxText(Lexer lexer)
    {
        // saw_text will be true if we see any non-whitespace content, or any whitespace content that is not a newline and
        // does not immediately follow a newline.
        bool sawText = false;
        // at_newline will be true if we are currently at a newline, or if we are at whitespace that is not a newline but
        // immediately follows a newline.
        bool atNewline = false;

        while (lexer.Lookahead != 0 && lexer.Lookahead != '<' && lexer.Lookahead != '>' && lexer.Lookahead != '{' &&
               lexer.Lookahead != '}' && lexer.Lookahead != '&')
        {
            bool isWSpace = IsWSpace(lexer.Lookahead);
            if (lexer.Lookahead == '\n')
            {
                atNewline = true;
            }
            else
            {
                // If at_newline is already true, and we see some whitespace, then it must stay true.
                // Otherwise, it should be false.
                //
                // See the table below to determine the logic for computing `saw_text`.
                //
                // |------------------------------------|
                // | at_newline | is_wspace | saw_text  |
                // |------------|-----------|-----------|
                // | false (0)  | false (0) | true  (1) |
                // | false (0)  | true  (1) | true  (1) |
                // | true  (1)  | false (0) | true  (1) |
                // | true  (1)  | true  (1) | false (0) |
                // |------------------------------------|

                atNewline &= isWSpace;
                if (!atNewline)
                {
                    sawText = true;
                }
            }

            Advance(lexer);
        }

        lexer.ResultSymbol = JsxText;
        return sawText;
    }

    /// <summary>tree_sitter_javascript_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[TemplateChars])
        {
            if (validSymbols[AutomaticSemicolon])
            {
                return false;
            }

            return ScanTemplateChars(lexer);
        }

        if (validSymbols[JsxText] && ScanJsxText(lexer))
        {
            return true;
        }

        if (validSymbols[AutomaticSemicolon])
        {
            bool scannedComment = false;
            bool ret = ScanAutomaticSemicolon(lexer, !validSymbols[LogicalOr], ref scannedComment);
            if (!ret && !scannedComment && validSymbols[TernaryQmark] && lexer.Lookahead == '?')
            {
                return ScanTernaryQmark(lexer);
            }

            return ret;
        }

        if (validSymbols[TernaryQmark])
        {
            return ScanTernaryQmark(lexer);
        }

        if (validSymbols[HtmlComment] && !validSymbols[LogicalOr] && !validSymbols[EscapeSequence] &&
            !validSymbols[RegexPattern])
        {
            return ScanHtmlComment(lexer);
        }

        return false;
    }

    /// <summary>tree_sitter_javascript_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer) => 0;

    /// <summary>tree_sitter_javascript_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
    }
}
