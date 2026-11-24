// Port of src/scanner.c of tree-sitter-markdown/tree-sitter-markdown (commit 41ff24a6c775492c543b09228aeb98d1cd2b3a0f,
// grammars/Markdown/upstream/src/scanner.c), the external scanner of the Markdown (block structure) grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - The stack of open blocks is List<Block>; the capacity bookkeeping of the C
//   Array is List's. Block is an enum of int (sizeof(Block) == 4 in C), the
//   serialized state is the same bytes as the C scanner writes (little-endian).
// - iswalpha/iswalnum/towlower of the C locale: ASCII letters and digits (as in
//   HtmlScanner); is_punctuation takes the low byte of the lookahead, as the
//   (char) cast of the C code does.

using System.Diagnostics;

namespace TreeSitter.Grammars.Markdown;

/// <summary>The external scanner of the Markdown (block structure) grammar.</summary>
internal sealed class MarkdownScanner : IExternalScanner
{
    // enum TokenType
    private const int LineEnding = 0;
    private const int SoftLineEnding = 1;
    private const int BlockClose = 2;
    private const int BlockContinuation = 3;
    private const int BlockQuoteStart = 4;
    private const int IndentedChunkStart = 5;
    private const int AtxH1Marker = 6;
    private const int AtxH2Marker = 7;
    private const int AtxH3Marker = 8;
    private const int AtxH4Marker = 9;
    private const int AtxH5Marker = 10;
    private const int AtxH6Marker = 11;
    private const int SetextH1Underline = 12;
    private const int SetextH2Underline = 13;
    private const int ThematicBreak = 14;
    private const int ListMarkerMinus = 15;
    private const int ListMarkerPlus = 16;
    private const int ListMarkerStar = 17;
    private const int ListMarkerParenthesis = 18;
    private const int ListMarkerDot = 19;
    private const int ListMarkerMinusDontInterrupt = 20;
    private const int ListMarkerPlusDontInterrupt = 21;
    private const int ListMarkerStarDontInterrupt = 22;
    private const int ListMarkerParenthesisDontInterrupt = 23;
    private const int ListMarkerDotDontInterrupt = 24;
    private const int FencedCodeBlockStartBacktick = 25;
    private const int FencedCodeBlockStartTilde = 26;
    private const int BlankLineStart = 27;
    private const int FencedCodeBlockEndBacktick = 28;
    private const int FencedCodeBlockEndTilde = 29;
    private const int HtmlBlock1Start = 30;
    private const int HtmlBlock1End = 31;
    private const int HtmlBlock2Start = 32;
    private const int HtmlBlock3Start = 33;
    private const int HtmlBlock4Start = 34;
    private const int HtmlBlock5Start = 35;
    private const int HtmlBlock6Start = 36;
    private const int HtmlBlock7Start = 37;
    private const int CloseBlock = 38;
    private const int NoIndentedChunk = 39;
    private const int Error = 40;
    private const int TriggerError = 41;
    private const int TokenEof = 42;
    private const int MinusMetadata = 43;
    private const int PlusMetadata = 44;
    private const int PipeTableStart = 45;
    private const int PipeTableLineEnding = 46;

    private const int NumTokenTypes = 47;

    // enum Block
    //
    // Description of a block on the block stack.
    //
    // ListItem is a list item with minimal indentation (content begins at indent
    // level 2) while ListItemMaxIndentation represents a list item with maximal
    // indentation without being considered a indented code block.
    //
    // Anonymous represents any block that whose close is not handled by the
    // external s.
    private enum Block
    {
        BlockQuote,
        IndentedCodeBlock,
        ListItem,
        ListItem1Indentation,
        ListItem2Indentation,
        ListItem3Indentation,
        ListItem4Indentation,
        ListItem5Indentation,
        ListItem6Indentation,
        ListItem7Indentation,
        ListItem8Indentation,
        ListItem9Indentation,
        ListItem10Indentation,
        ListItem11Indentation,
        ListItem12Indentation,
        ListItem13Indentation,
        ListItem14Indentation,
        ListItemMaxIndentation,
        FencedCodeBlock,
        Anonymous,
    }

    // Determines if a character is punctuation as defined by the markdown spec.
    private static bool IsPunctuation(int chr) =>
        (chr >= '!' && chr <= '/') || (chr >= ':' && chr <= '@') ||
        (chr >= '[' && chr <= '`') || (chr >= '{' && chr <= '~');

    // Determines if a character is an ASCII digit.
    private static bool IsAsciiDigit(int chr) => chr >= '0' && chr <= '9';

    // Returns the indentation level which lines of a list item should have at a
    // minimum. Should only be called with blocks for which `is_list_item` returns
    // true.
    private static byte ListItemIndentation(Block block) =>
        (byte)((int)block - (int)Block.ListItem + 2);

    private const int NumHtmlTagNamesRule1 = 3;

    private static readonly string[] HtmlTagNamesRule1 = ["pre", "script", "style"];

    private const int NumHtmlTagNamesRule7 = 62;

    private static readonly string[] HtmlTagNamesRule7 =
    [
        "address", "article", "aside", "base", "basefont", "blockquote",
        "body", "caption", "center", "col", "colgroup", "dd",
        "details", "dialog", "dir", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "frame",
        "frameset", "h1", "h2", "h3", "h4", "h5",
        "h6", "head", "header", "hr", "html", "iframe",
        "legend", "li", "link", "main", "menu", "menuitem",
        "nav", "noframes", "ol", "optgroup", "option", "p",
        "param", "section", "source", "summary", "table", "tbody",
        "td", "tfoot", "th", "thead", "title", "tr",
        "track", "ul",
    ];

    // For explanation of the tokens see grammar.js
    private static readonly bool[] ParagraphInterruptSymbols =
    [
        false, // LineEnding,
        false, // SoftLineEnding,
        false, // BlockClose,
        false, // BlockContinuation,
        true,  // BlockQuoteStart,
        false, // IndentedChunkStart,
        true,  // AtxH1Marker,
        true,  // AtxH2Marker,
        true,  // AtxH3Marker,
        true,  // AtxH4Marker,
        true,  // AtxH5Marker,
        true,  // AtxH6Marker,
        true,  // SetextH1Underline,
        true,  // SetextH2Underline,
        true,  // ThematicBreak,
        true,  // ListMarkerMinus,
        true,  // ListMarkerPlus,
        true,  // ListMarkerStar,
        true,  // ListMarkerParenthesis,
        true,  // ListMarkerDot,
        false, // ListMarkerMinusDontInterrupt,
        false, // ListMarkerPlusDontInterrupt,
        false, // ListMarkerStarDontInterrupt,
        false, // ListMarkerParenthesisDontInterrupt,
        false, // ListMarkerDotDontInterrupt,
        true,  // FencedCodeBlockStartBacktick,
        true,  // FencedCodeBlockStartTilde,
        true,  // BlankLineStart,
        false, // FencedCodeBlockEndBacktick,
        false, // FencedCodeBlockEndTilde,
        true,  // HtmlBlock1Start,
        false, // HtmlBlock1End,
        true,  // HtmlBlock2Start,
        true,  // HtmlBlock3Start,
        true,  // HtmlBlock4Start,
        true,  // HtmlBlock5Start,
        true,  // HtmlBlock6Start,
        false, // HtmlBlock7Start,
        false, // CloseBlock,
        false, // NoIndentedChunk,
        false, // Error,
        false, // TriggerError,
        false, // TokenEof,
        false, // MinusMetadata,
        false, // PlusMetadata,
        true,  // PipeTableStart,
        false, // PipeTableLineEnding,
    ];

    // Whether the token opens a new block, i.e. pushes onto the open blocks.
    private static bool OpensBlock(int token)
    {
        switch (token)
        {
            case BlockQuoteStart:
            case IndentedChunkStart:
            case ListMarkerMinus:
            case ListMarkerPlus:
            case ListMarkerStar:
            case ListMarkerParenthesis:
            case ListMarkerDot:
            case ListMarkerMinusDontInterrupt:
            case ListMarkerPlusDontInterrupt:
            case ListMarkerStarDontInterrupt:
            case ListMarkerParenthesisDontInterrupt:
            case ListMarkerDotDontInterrupt:
            case FencedCodeBlockStartBacktick:
            case FencedCodeBlockStartTilde:
            case HtmlBlock1Start:
            case HtmlBlock2Start:
            case HtmlBlock3Start:
            case HtmlBlock4Start:
            case HtmlBlock5Start:
            case HtmlBlock6Start:
            case HtmlBlock7Start:
                return true;
            default:
                return false;
        }
    }

    // TREE_SITTER_SERIALIZATION_BUFFER_SIZE
    private const int SerializationBufferSize = 1024;

    // sizeof(Block) in C: the enum has int as its underlying type.
    private const int BlockSize = 4;

    // The serialized state is 5 bytes followed by the open blocks. No new blocks
    // are opened once there are this many.
    private const int MaxOpenBlocks = (SerializationBufferSize - 5) / BlockSize;

    // State bitflags used with _state

    // Currently matching (at the beginning of a line)
    private const byte StateMatching = 0x1 << 0;

    // Last line break was inside a paragraph
    private const byte StateWasSoftLineBreak = 0x1 << 1;

    // Block should be closed after next line break
    private const byte StateCloseBlock = 0x1 << 4;

    // A stack of open blocks in the current parse state
    private readonly List<Block> _openBlocks = new List<Block>();

    // Parser state flags
    private byte _state;

    // Number of blocks that have been matched so far. Only changes during
    // matching and is reset after every line ending
    private byte _matched;

    // Consumed but "unused" indentation. Sometimes a tab needs to be "split" to
    // be used in multiple tokens.
    private byte _indentation;

    // The current column. Used to decide how many spaces a tab should equal
    private byte _column;

    // The delimiter length of the currently open fenced code block
    private byte _fencedCodeBlockDelimiterLength;

    private bool _simulate;

    private void PushBlock(Block block) => _openBlocks.Add(block);

    private Block PopBlock()
    {
        Block block = _openBlocks[^1];
        _openBlocks.RemoveAt(_openBlocks.Count - 1);
        return block;
    }

    private static bool NameEquals(char[] name, int nameLength, string other)
    {
        if (nameLength != other.Length)
        {
            return false;
        }

        for (int i = 0; i < nameLength; i++)
        {
            if (name[i] != other[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>tree_sitter_markdown_external_scanner_create</summary>
    public MarkdownScanner()
    {
        // _Static_assert(ATX_H6_MARKER == ATX_H1_MARKER + 5, "")
        Debug.Assert(AtxH6Marker == AtxH1Marker + 5, "");
        Deserialize(ReadOnlySpan<byte>.Empty);
    }

    private void MarkEnd(Lexer lexer)
    {
        if (!_simulate)
        {
            lexer.MarkEnd();
        }
    }

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

    // Advance the lexer one character
    // Also keeps track of the current column, counting tabs as spaces with tab stop
    // 4 See https://github.github.com/gfm/#tabs
    private byte Advance(Lexer lexer)
    {
        byte size = 1;
        if (lexer.Lookahead == '\t')
        {
            size = (byte)(4 - _column);
            _column = 0;
        }
        else
        {
            _column = (byte)((_column + 1) % 4);
        }

        lexer.Advance(false);
        return size;
    }

    // Try to match the given block, i.e. consume all tokens that belong to the
    // block. These are
    // 1. indentation for list items and indented code blocks
    // 2. '>' for block quotes
    // Returns true if the block is matched and false otherwise
    private bool Match(Lexer lexer, Block block)
    {
        switch (block)
        {
            case Block.IndentedCodeBlock:
                while (_indentation < 4)
                {
                    if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                    {
                        _indentation = (byte)(_indentation + Advance(lexer));
                    }
                    else
                    {
                        break;
                    }
                }

                if (_indentation >= 4 && lexer.Lookahead != '\n' &&
                    lexer.Lookahead != '\r')
                {
                    _indentation = (byte)(_indentation - 4);
                    return true;
                }

                break;
            case Block.ListItem:
            case Block.ListItem1Indentation:
            case Block.ListItem2Indentation:
            case Block.ListItem3Indentation:
            case Block.ListItem4Indentation:
            case Block.ListItem5Indentation:
            case Block.ListItem6Indentation:
            case Block.ListItem7Indentation:
            case Block.ListItem8Indentation:
            case Block.ListItem9Indentation:
            case Block.ListItem10Indentation:
            case Block.ListItem11Indentation:
            case Block.ListItem12Indentation:
            case Block.ListItem13Indentation:
            case Block.ListItem14Indentation:
            case Block.ListItemMaxIndentation:
                while (_indentation < ListItemIndentation(block))
                {
                    if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                    {
                        _indentation = (byte)(_indentation + Advance(lexer));
                    }
                    else
                    {
                        break;
                    }
                }

                if (_indentation >= ListItemIndentation(block))
                {
                    _indentation = (byte)(_indentation - ListItemIndentation(block));
                    return true;
                }

                if (lexer.Lookahead == '\n' || lexer.Lookahead == '\r')
                {
                    _indentation = 0;
                    return true;
                }

                break;
            case Block.BlockQuote:
                while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                {
                    _indentation = (byte)(_indentation + Advance(lexer));
                }

                if (lexer.Lookahead == '>')
                {
                    Advance(lexer);
                    _indentation = 0;
                    if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                    {
                        _indentation = (byte)(_indentation + Advance(lexer) - 1);
                    }

                    return true;
                }

                break;
            case Block.FencedCodeBlock:
            case Block.Anonymous:
                return true;
        }

        return false;
    }

    private bool ParseFencedCodeBlock(char delimiter, Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // Nothing to emit for this delimiter here; bail before counting the
        // (possibly long) run, which would otherwise be O(n^2) across a paragraph.
        if (delimiter == '`'
                ? (!validSymbols[FencedCodeBlockStartBacktick] &&
                   !validSymbols[FencedCodeBlockEndBacktick])
                : (!validSymbols[FencedCodeBlockStartTilde] &&
                   !validSymbols[FencedCodeBlockEndTilde]))
        {
            return false;
        }

        // count the number of backticks
        byte level = 0;
        while (lexer.Lookahead == delimiter)
        {
            Advance(lexer);
            level++;
        }

        MarkEnd(lexer);

        // If this is able to close a fenced code block then that is the only valid
        // interpretation. It can only close a fenced code block if the number of
        // backticks is at least the number of backticks of the opening delimiter.
        // Also it cannot be indented more than 3 spaces.
        if ((delimiter == '`' ? validSymbols[FencedCodeBlockEndBacktick]
                              : validSymbols[FencedCodeBlockEndTilde]) &&
            _indentation < 4 && level >= _fencedCodeBlockDelimiterLength)
        {
            while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                Advance(lexer);
            }

            bool isEof = lexer.Eof();
            if (isEof)
            {
                // A closing fence may end the input without a line ending. Nothing
                // follows to consume the trailing whitespace, so it joins the token.
                MarkEnd(lexer);
            }

            if (lexer.Lookahead == '\n' || lexer.Lookahead == '\r' || isEof)
            {
                _fencedCodeBlockDelimiterLength = 0;
                lexer.ResultSymbol = delimiter == '`'
                    ? (ushort)FencedCodeBlockEndBacktick
                    : (ushort)FencedCodeBlockEndTilde;
                return true;
            }
        }

        // If this could be the start of a fenced code block, check if the info
        // string contains any backticks.
        if ((delimiter == '`' ? validSymbols[FencedCodeBlockStartBacktick]
                              : validSymbols[FencedCodeBlockStartTilde]) &&
            level >= 3)
        {
            bool infoStringHasBacktick = false;
            if (delimiter == '`')
            {
                while (lexer.Lookahead != '\n' && lexer.Lookahead != '\r' &&
                       !lexer.Eof())
                {
                    if (lexer.Lookahead == '`')
                    {
                        infoStringHasBacktick = true;
                        break;
                    }

                    Advance(lexer);
                }
            }

            // If it does not then choose to interpret this as the start of a fenced
            // code block.
            if (!infoStringHasBacktick)
            {
                lexer.ResultSymbol = delimiter == '`'
                    ? (ushort)FencedCodeBlockStartBacktick
                    : (ushort)FencedCodeBlockStartTilde;
                if (!_simulate)
                {
                    PushBlock(Block.FencedCodeBlock);
                }

                // Remember the length of the delimiter for later, since we need it
                // to decide whether a sequence of backticks can close the block.
                _fencedCodeBlockDelimiterLength = level;
                _indentation = 0;
                return true;
            }
        }

        return false;
    }

    private bool ParseStar(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // Nothing to emit here; bail before scanning the (possibly long) run of
        // `*`, which would otherwise be O(n^2) across a paragraph.
        if (!validSymbols[ThematicBreak] && !validSymbols[ListMarkerStar] &&
            !validSymbols[ListMarkerStarDontInterrupt])
        {
            return false;
        }

        Advance(lexer);
        MarkEnd(lexer);

        // Otherwise count the number of stars permitting whitespaces between them.
        int starCount = 1;

        // Also remember how many stars there are before the first whitespace...
        // ...and how many spaces follow the first star.
        byte extraIndentation = 0;
        for (;;)
        {
            if (lexer.Lookahead == '*')
            {
                if (starCount == 1 && extraIndentation >= 1 &&
                    validSymbols[ListMarkerStar])
                {
                    // If we get to this point then the token has to be at least
                    // this long. We need to call `mark_end` here in case we decide
                    // later that this is a list item.
                    MarkEnd(lexer);
                }

                starCount++;
                Advance(lexer);
            }
            else if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                if (starCount == 1)
                {
                    extraIndentation = (byte)(extraIndentation + Advance(lexer));
                }
                else
                {
                    Advance(lexer);
                }
            }
            else
            {
                break;
            }
        }

        bool lineEnd = lexer.Lookahead == '\n' || lexer.Lookahead == '\r';
        bool dontInterrupt = false;
        if (starCount == 1 && lineEnd)
        {
            extraIndentation = 1;

            // line is empty so don't interrupt paragraphs if this is a list marker
            dontInterrupt = _matched == _openBlocks.Count;
        }

        // If there were at least 3 stars then this could be a thematic break
        bool thematicBreak = starCount >= 3 && lineEnd;

        // If there was a star and at least one space after that star then this
        // could be a list marker.
        bool listMarkerStar = starCount >= 1 && extraIndentation >= 1;
        if (validSymbols[ThematicBreak] && thematicBreak && _indentation < 4)
        {
            // If a thematic break is valid then it takes precedence
            lexer.ResultSymbol = (ushort)ThematicBreak;
            MarkEnd(lexer);
            _indentation = 0;
            return true;
        }

        if ((dontInterrupt ? validSymbols[ListMarkerStarDontInterrupt]
                           : validSymbols[ListMarkerStar]) &&
            listMarkerStar)
        {
            // List markers take precedence over emphasis markers
            // If starCount > 1 then we already called mark_end at the right point.
            // Otherwise the token should go until this point.
            if (starCount == 1)
            {
                MarkEnd(lexer);
            }

            // Not counting one space...
            extraIndentation--;

            // ... check if the list item begins with an indented code block
            if (extraIndentation <= 3)
            {
                // If not then calculate the indentation level of the list item
                // content as indentation of list marker + indentation after list
                // marker - 1
                extraIndentation = (byte)(extraIndentation + _indentation);
                _indentation = 0;
            }
            else
            {
                // Otherwise the indentation level is just the indentation of the
                // list marker. We keep the indentation after the list marker for
                // later blocks.
                byte temp = _indentation;
                _indentation = extraIndentation;
                extraIndentation = temp;
            }

            if (!_simulate)
            {
                PushBlock((Block)((int)Block.ListItem + extraIndentation));
            }

            lexer.ResultSymbol = dontInterrupt
                ? (ushort)ListMarkerStarDontInterrupt
                : (ushort)ListMarkerStar;
            return true;
        }

        return false;
    }

    private bool ParseThematicBreakUnderscore(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // Same as above for a run of `_` (only THEMATIC_BREAK is emittable here).
        if (!validSymbols[ThematicBreak])
        {
            return false;
        }

        Advance(lexer);
        MarkEnd(lexer);
        int underscoreCount = 1;
        for (;;)
        {
            if (lexer.Lookahead == '_')
            {
                underscoreCount++;
                Advance(lexer);
            }
            else if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                Advance(lexer);
            }
            else
            {
                break;
            }
        }

        bool lineEnd = lexer.Lookahead == '\n' || lexer.Lookahead == '\r';
        if (underscoreCount >= 3 && lineEnd && validSymbols[ThematicBreak])
        {
            lexer.ResultSymbol = (ushort)ThematicBreak;
            MarkEnd(lexer);
            _indentation = 0;
            return true;
        }

        return false;
    }

    private bool ParseBlockQuote(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[BlockQuoteStart])
        {
            Advance(lexer);
            _indentation = 0;
            if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                _indentation = (byte)(_indentation + Advance(lexer) - 1);
            }

            lexer.ResultSymbol = (ushort)BlockQuoteStart;
            if (!_simulate)
            {
                PushBlock(Block.BlockQuote);
            }

            return true;
        }

        return false;
    }

    private bool ParseAtxHeading(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[AtxH1Marker] && _indentation <= 3)
        {
            MarkEnd(lexer);
            ushort level = 0;
            while (lexer.Lookahead == '#' && level <= 6)
            {
                Advance(lexer);
                level++;
            }

            if (level <= 6 &&
                (lexer.Lookahead == ' ' || lexer.Lookahead == '\t' ||
                 lexer.Lookahead == '\n' || lexer.Lookahead == '\r'))
            {
                lexer.ResultSymbol = (ushort)(AtxH1Marker + (level - 1));
                _indentation = 0;
                MarkEnd(lexer);
                return true;
            }
        }

        return false;
    }

    private bool ParseSetextUnderline(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[SetextH1Underline] &&
            _matched == _openBlocks.Count)
        {
            MarkEnd(lexer);
            while (lexer.Lookahead == '=')
            {
                Advance(lexer);
            }

            while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                Advance(lexer);
            }

            if (lexer.Lookahead == '\n' || lexer.Lookahead == '\r')
            {
                lexer.ResultSymbol = (ushort)SetextH1Underline;
                MarkEnd(lexer);
                return true;
            }
        }

        return false;
    }

    private bool ParsePlus(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (_indentation <= 3 &&
            (validSymbols[ListMarkerPlus] ||
             validSymbols[ListMarkerPlusDontInterrupt] ||
             validSymbols[PlusMetadata]))
        {
            Advance(lexer);
            if (validSymbols[PlusMetadata] && lexer.Lookahead == '+')
            {
                Advance(lexer);
                if (lexer.Lookahead != '+')
                {
                    return false;
                }

                Advance(lexer);
                while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                {
                    Advance(lexer);
                }

                if (lexer.Lookahead != '\n' && lexer.Lookahead != '\r')
                {
                    return false;
                }

                for (;;)
                {
                    // advance over newline
                    if (lexer.Lookahead == '\r')
                    {
                        Advance(lexer);
                        if (lexer.Lookahead == '\n')
                        {
                            Advance(lexer);
                        }
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    // check for pluses
                    int plusCount = 0;
                    while (lexer.Lookahead == '+')
                    {
                        plusCount++;
                        Advance(lexer);
                    }

                    if (plusCount == 3)
                    {
                        // if exactly 3 check if next symbol (after eventual
                        // whitespace) is newline
                        while (lexer.Lookahead == ' ' ||
                               lexer.Lookahead == '\t')
                        {
                            Advance(lexer);
                        }

                        if (lexer.Lookahead == '\r' || lexer.Lookahead == '\n')
                        {
                            // if so also consume newline
                            if (lexer.Lookahead == '\r')
                            {
                                Advance(lexer);
                                if (lexer.Lookahead == '\n')
                                {
                                    Advance(lexer);
                                }
                            }
                            else
                            {
                                Advance(lexer);
                            }

                            MarkEnd(lexer);
                            lexer.ResultSymbol = (ushort)PlusMetadata;
                            return true;
                        }
                    }

                    // otherwise consume rest of line
                    while (lexer.Lookahead != '\n' && lexer.Lookahead != '\r' &&
                           !lexer.Eof())
                    {
                        Advance(lexer);
                    }

                    // if end of file is reached, then this is not metadata
                    if (lexer.Eof())
                    {
                        break;
                    }
                }
            }
            else
            {
                byte extraIndentation = 0;
                while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                {
                    extraIndentation = (byte)(extraIndentation + Advance(lexer));
                }

                bool dontInterrupt = false;
                if (lexer.Lookahead == '\r' || lexer.Lookahead == '\n')
                {
                    extraIndentation = 1;
                    dontInterrupt = true;
                }

                dontInterrupt =
                    dontInterrupt && _matched == _openBlocks.Count;
                if (extraIndentation >= 1 &&
                    (dontInterrupt ? validSymbols[ListMarkerPlusDontInterrupt]
                                   : validSymbols[ListMarkerPlus]))
                {
                    lexer.ResultSymbol = dontInterrupt
                        ? (ushort)ListMarkerPlusDontInterrupt
                        : (ushort)ListMarkerPlus;
                    extraIndentation--;
                    if (extraIndentation <= 3)
                    {
                        extraIndentation = (byte)(extraIndentation + _indentation);
                        _indentation = 0;
                    }
                    else
                    {
                        byte temp = _indentation;
                        _indentation = extraIndentation;
                        extraIndentation = temp;
                    }

                    if (!_simulate)
                    {
                        PushBlock((Block)((int)Block.ListItem + extraIndentation));
                    }

                    return true;
                }
            }
        }

        return false;
    }

    private bool ParseOrderedListMarker(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (_indentation <= 3 &&
            (validSymbols[ListMarkerParenthesis] ||
             validSymbols[ListMarkerDot] ||
             validSymbols[ListMarkerParenthesisDontInterrupt] ||
             validSymbols[ListMarkerDotDontInterrupt]))
        {
            int digits = 1;
            Advance(lexer);
            while (IsAsciiDigit(lexer.Lookahead))
            {
                digits++;
                Advance(lexer);
            }

            bool dontInterrupt = digits > 1;
            if (digits <= 9)
            {
                bool dot = false;
                bool parenthesis = false;
                if (lexer.Lookahead == '.')
                {
                    Advance(lexer);
                    dot = true;
                }
                else if (lexer.Lookahead == ')')
                {
                    Advance(lexer);
                    parenthesis = true;
                }

                if (dot || parenthesis)
                {
                    byte extraIndentation = 0;
                    while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                    {
                        extraIndentation = (byte)(extraIndentation + Advance(lexer));
                    }

                    bool lineEnd =
                        lexer.Lookahead == '\n' || lexer.Lookahead == '\r';
                    if (lineEnd)
                    {
                        extraIndentation = 1;
                        dontInterrupt = true;
                    }

                    dontInterrupt =
                        dontInterrupt && _matched == _openBlocks.Count;
                    int marker =
                        dot ? (dontInterrupt ? ListMarkerDotDontInterrupt : ListMarkerDot)
                            : (dontInterrupt ? ListMarkerParenthesisDontInterrupt : ListMarkerParenthesis);
                    if (extraIndentation >= 1 && validSymbols[marker])
                    {
                        lexer.ResultSymbol = (ushort)marker;
                        extraIndentation--;
                        if (extraIndentation <= 3)
                        {
                            extraIndentation = (byte)(extraIndentation + _indentation);
                            _indentation = 0;
                        }
                        else
                        {
                            byte temp = _indentation;
                            _indentation = extraIndentation;
                            extraIndentation = temp;
                        }

                        if (!_simulate)
                        {
                            PushBlock(
                                (Block)((int)Block.ListItem + extraIndentation + digits));
                        }

                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool ParseMinus(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (_indentation <= 3 &&
            (validSymbols[ListMarkerMinus] ||
             validSymbols[ListMarkerMinusDontInterrupt] ||
             validSymbols[SetextH2Underline] || validSymbols[ThematicBreak] ||
             validSymbols[MinusMetadata]))
        {
            MarkEnd(lexer);
            bool whitespaceAfterMinus = false;
            bool minusAfterWhitespace = false;
            int minusCount = 0;
            byte extraIndentation = 0;

            for (;;)
            {
                if (lexer.Lookahead == '-')
                {
                    if (minusCount == 1 && extraIndentation >= 1)
                    {
                        MarkEnd(lexer);
                    }

                    minusCount++;
                    Advance(lexer);
                    minusAfterWhitespace = whitespaceAfterMinus;
                }
                else if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                {
                    if (minusCount == 1)
                    {
                        extraIndentation = (byte)(extraIndentation + Advance(lexer));
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    whitespaceAfterMinus = true;
                }
                else
                {
                    break;
                }
            }

            bool lineEnd = lexer.Lookahead == '\n' || lexer.Lookahead == '\r';
            bool dontInterrupt = false;
            if (minusCount == 1 && lineEnd)
            {
                extraIndentation = 1;
                dontInterrupt = true;
            }

            dontInterrupt = dontInterrupt && _matched == _openBlocks.Count;
            bool thematicBreak = minusCount >= 3 && lineEnd;
            bool underline =
                minusCount >= 1 && !minusAfterWhitespace && lineEnd &&
                _matched ==
                    _openBlocks
                        .Count; // setext heading can not break lazy continuation
            bool listMarkerMinus = minusCount >= 1 && extraIndentation >= 1;
            bool success = false;
            if (validSymbols[SetextH2Underline] && underline)
            {
                lexer.ResultSymbol = (ushort)SetextH2Underline;
                MarkEnd(lexer);
                _indentation = 0;
                success = true;
            }
            else if (validSymbols[ThematicBreak] &&
                     thematicBreak)
            { // underline is false if list_marker_minus
              // is true
                lexer.ResultSymbol = (ushort)ThematicBreak;
                MarkEnd(lexer);
                _indentation = 0;
                success = true;
            }
            else if ((dontInterrupt
                        ? validSymbols[ListMarkerMinusDontInterrupt]
                        : validSymbols[ListMarkerMinus]) &&
                     listMarkerMinus)
            {
                if (minusCount == 1)
                {
                    MarkEnd(lexer);
                }

                extraIndentation--;
                if (extraIndentation <= 3)
                {
                    extraIndentation = (byte)(extraIndentation + _indentation);
                    _indentation = 0;
                }
                else
                {
                    byte temp = _indentation;
                    _indentation = extraIndentation;
                    extraIndentation = temp;
                }

                if (!_simulate)
                {
                    PushBlock((Block)((int)Block.ListItem + extraIndentation));
                }

                lexer.ResultSymbol = dontInterrupt
                    ? (ushort)ListMarkerMinusDontInterrupt
                    : (ushort)ListMarkerMinus;
                return true;
            }

            if (minusCount == 3 && (!minusAfterWhitespace) && lineEnd &&
                validSymbols[MinusMetadata])
            {
                for (;;)
                {
                    // advance over newline
                    if (lexer.Lookahead == '\r')
                    {
                        Advance(lexer);
                        if (lexer.Lookahead == '\n')
                        {
                            Advance(lexer);
                        }
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    // check for minuses
                    minusCount = 0;
                    while (lexer.Lookahead == '-')
                    {
                        minusCount++;
                        Advance(lexer);
                    }

                    if (minusCount == 3)
                    {
                        // if exactly 3 check if next symbol (after eventual
                        // whitespace) is newline
                        while (lexer.Lookahead == ' ' ||
                               lexer.Lookahead == '\t')
                        {
                            Advance(lexer);
                        }

                        if (lexer.Lookahead == '\r' || lexer.Lookahead == '\n')
                        {
                            // if so also consume newline
                            if (lexer.Lookahead == '\r')
                            {
                                Advance(lexer);
                                if (lexer.Lookahead == '\n')
                                {
                                    Advance(lexer);
                                }
                            }
                            else
                            {
                                Advance(lexer);
                            }

                            MarkEnd(lexer);
                            lexer.ResultSymbol = (ushort)MinusMetadata;
                            return true;
                        }
                    }

                    // otherwise consume rest of line
                    while (lexer.Lookahead != '\n' && lexer.Lookahead != '\r' &&
                           !lexer.Eof())
                    {
                        Advance(lexer);
                    }

                    // if end of file is reached, then this is not metadata
                    if (lexer.Eof())
                    {
                        break;
                    }
                }
            }

            if (success)
            {
                return true;
            }
        }

        return false;
    }

    private bool ParseHtmlBlock(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (!(validSymbols[HtmlBlock1Start] ||
              validSymbols[HtmlBlock1End] ||
              validSymbols[HtmlBlock2Start] ||
              validSymbols[HtmlBlock3Start] ||
              validSymbols[HtmlBlock4Start] ||
              validSymbols[HtmlBlock5Start] ||
              validSymbols[HtmlBlock6Start] ||
              validSymbols[HtmlBlock7Start]))
        {
            return false;
        }

        Advance(lexer);
        if (lexer.Lookahead == '?' && validSymbols[HtmlBlock3Start])
        {
            Advance(lexer);
            lexer.ResultSymbol = (ushort)HtmlBlock3Start;
            if (!_simulate)
            {
                PushBlock(Block.Anonymous);
            }

            return true;
        }

        if (lexer.Lookahead == '!')
        {
            // could be block 2
            Advance(lexer);
            if (lexer.Lookahead == '-')
            {
                // Block 2 ends at the first `-->`, which may overlap the opening
                // `<!--` as in `<!-->` and `<!--->`. For those, end the token
                // after `<!` so that the closing `-->` is left for the block.
                MarkEnd(lexer);
                byte column = _column;
                Advance(lexer);
                if (lexer.Lookahead != '-' || !validSymbols[HtmlBlock2Start])
                {
                    return false;
                }

                Advance(lexer);
                if (lexer.Lookahead == '>' || lexer.Lookahead == '-')
                {
                    _column = column;
                }
                else
                {
                    MarkEnd(lexer);
                }

                lexer.ResultSymbol = (ushort)HtmlBlock2Start;
                if (!_simulate)
                {
                    PushBlock(Block.Anonymous);
                }

                return true;
            }
            else if ('A' <= lexer.Lookahead && lexer.Lookahead <= 'Z' &&
                     validSymbols[HtmlBlock4Start])
            {
                Advance(lexer);
                lexer.ResultSymbol = (ushort)HtmlBlock4Start;
                if (!_simulate)
                {
                    PushBlock(Block.Anonymous);
                }

                return true;
            }
            else if (lexer.Lookahead == '[')
            {
                Advance(lexer);
                if (lexer.Lookahead == 'C')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == 'D')
                    {
                        Advance(lexer);
                        if (lexer.Lookahead == 'A')
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == 'T')
                            {
                                Advance(lexer);
                                if (lexer.Lookahead == 'A')
                                {
                                    Advance(lexer);
                                    if (lexer.Lookahead == '[' &&
                                        validSymbols[HtmlBlock5Start])
                                    {
                                        Advance(lexer);
                                        lexer.ResultSymbol = (ushort)HtmlBlock5Start;
                                        if (!_simulate)
                                        {
                                            PushBlock(Block.Anonymous);
                                        }

                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        bool startingSlash = lexer.Lookahead == '/';
        if (startingSlash)
        {
            Advance(lexer);
        }

        char[] name = new char[11];
        int nameLength = 0;
        while (IsWAlpha(lexer.Lookahead))
        {
            if (nameLength < 10)
            {
                name[nameLength++] = ToWLower(lexer.Lookahead);
            }
            else
            {
                nameLength = 12;
            }

            Advance(lexer);
        }

        if (nameLength == 0)
        {
            return false;
        }

        bool tagClosed = false;
        if (nameLength < 11)
        {
            // name[nameLength] = 0; of C, the comparison is by length instead of
            // strcmp
            bool nextSymbolValid =
                lexer.Lookahead == ' ' || lexer.Lookahead == '\t' ||
                lexer.Lookahead == '\n' || lexer.Lookahead == '\r' ||
                lexer.Lookahead == '>';
            if (nextSymbolValid)
            {
                // try block 1 names
                for (int i = 0; i < NumHtmlTagNamesRule1; i++)
                {
                    if (NameEquals(name, nameLength, HtmlTagNamesRule1[i]))
                    {
                        if (startingSlash)
                        {
                            if (validSymbols[HtmlBlock1End])
                            {
                                lexer.ResultSymbol = (ushort)HtmlBlock1End;
                                return true;
                            }
                        }
                        else if (validSymbols[HtmlBlock1Start])
                        {
                            lexer.ResultSymbol = (ushort)HtmlBlock1Start;
                            if (!_simulate)
                            {
                                PushBlock(Block.Anonymous);
                            }

                            return true;
                        }
                    }
                }
            }

            if (!nextSymbolValid && lexer.Lookahead == '/')
            {
                Advance(lexer);
                if (lexer.Lookahead == '>')
                {
                    Advance(lexer);
                    tagClosed = true;
                }
            }

            if (nextSymbolValid || tagClosed)
            {
                // try block 2 names
                for (int i = 0; i < NumHtmlTagNamesRule7; i++)
                {
                    if (NameEquals(name, nameLength, HtmlTagNamesRule7[i]) &&
                        validSymbols[HtmlBlock6Start])
                    {
                        lexer.ResultSymbol = (ushort)HtmlBlock6Start;
                        if (!_simulate)
                        {
                            PushBlock(Block.Anonymous);
                        }

                        return true;
                    }
                }
            }
        }

        if (!validSymbols[HtmlBlock7Start])
        {
            return false;
        }

        if (!tagClosed)
        {
            // tag name (continued)
            while (IsWAlNum(lexer.Lookahead) || lexer.Lookahead == '-')
            {
                Advance(lexer);
            }

            if (!startingSlash)
            {
                // attributes
                bool hadWhitespace = false;
                for (;;)
                {
                    // whitespace
                    while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                    {
                        hadWhitespace = true;
                        Advance(lexer);
                    }

                    if (lexer.Lookahead == '/')
                    {
                        Advance(lexer);
                        break;
                    }

                    if (lexer.Lookahead == '>')
                    {
                        break;
                    }

                    // attribute name
                    if (!hadWhitespace)
                    {
                        return false;
                    }

                    if (!IsWAlpha(lexer.Lookahead) &&
                        lexer.Lookahead != '_' && lexer.Lookahead != ':')
                    {
                        return false;
                    }

                    hadWhitespace = false;
                    Advance(lexer);
                    while (IsWAlNum(lexer.Lookahead) ||
                           lexer.Lookahead == '_' || lexer.Lookahead == '.' ||
                           lexer.Lookahead == ':' || lexer.Lookahead == '-')
                    {
                        Advance(lexer);
                    }

                    // attribute value specification
                    // optional whitespace
                    while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                    {
                        hadWhitespace = true;
                        Advance(lexer);
                    }

                    // =
                    if (lexer.Lookahead == '=')
                    {
                        Advance(lexer);
                        hadWhitespace = false;

                        // optional whitespace
                        while (lexer.Lookahead == ' ' ||
                               lexer.Lookahead == '\t')
                        {
                            Advance(lexer);
                        }

                        // attribute value
                        if (lexer.Lookahead == '\'' || lexer.Lookahead == '"')
                        {
                            // char delimiter = (char)lexer->lookahead of C: only the
                            // low byte matters against the ASCII quotes
                            int delimiter = lexer.Lookahead & 0xFF;
                            Advance(lexer);
                            while (lexer.Lookahead != delimiter &&
                                   lexer.Lookahead != '\n' &&
                                   lexer.Lookahead != '\r' && !lexer.Eof())
                            {
                                Advance(lexer);
                            }

                            if (lexer.Lookahead != delimiter)
                            {
                                return false;
                            }

                            Advance(lexer);
                        }
                        else
                        {
                            // unquoted attribute value
                            bool hadOne = false;
                            while (lexer.Lookahead != ' ' &&
                                   lexer.Lookahead != '\t' &&
                                   lexer.Lookahead != '"' &&
                                   lexer.Lookahead != '\'' &&
                                   lexer.Lookahead != '=' &&
                                   lexer.Lookahead != '<' &&
                                   lexer.Lookahead != '>' &&
                                   lexer.Lookahead != '`' &&
                                   lexer.Lookahead != '\n' &&
                                   lexer.Lookahead != '\r' && !lexer.Eof())
                            {
                                Advance(lexer);
                                hadOne = true;
                            }

                            if (!hadOne)
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            else
            {
                while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                {
                    Advance(lexer);
                }
            }

            if (lexer.Lookahead != '>')
            {
                return false;
            }

            Advance(lexer);
        }

        while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
        {
            Advance(lexer);
        }

        if (lexer.Lookahead == '\r' || lexer.Lookahead == '\n')
        {
            lexer.ResultSymbol = (ushort)HtmlBlock7Start;
            if (!_simulate)
            {
                PushBlock(Block.Anonymous);
            }

            return true;
        }

        return false;
    }

    private bool ParsePipeTable(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // unused
        _ = validSymbols;

        // PIPE_TABLE_START is zero width
        MarkEnd(lexer);

        // count number of cells
        int cellCount = 0;

        // also remember if we see starting and ending pipes, as empty headers have
        // to have both
        bool startingPipe = false;
        bool endingPipe = false;
        bool empty = true;
        if (lexer.Lookahead == '|')
        {
            startingPipe = true;
            Advance(lexer);
        }

        while (lexer.Lookahead != '\r' && lexer.Lookahead != '\n' &&
               !lexer.Eof())
        {
            if (lexer.Lookahead == '|')
            {
                cellCount++;
                endingPipe = true;
                Advance(lexer);
            }
            else
            {
                if (lexer.Lookahead != ' ' && lexer.Lookahead != '\t')
                {
                    endingPipe = false;
                }

                if (lexer.Lookahead == '\\')
                {
                    Advance(lexer);
                    if (IsPunctuation(lexer.Lookahead & 0xFF))
                    {
                        Advance(lexer);
                    }
                }
                else
                {
                    Advance(lexer);
                }
            }
        }

        if (empty && cellCount == 0 && !(startingPipe && endingPipe))
        {
            return false;
        }

        if (!endingPipe)
        {
            cellCount++;
        }

        // check the following line for a delimiter row
        // parse a newline
        if (lexer.Lookahead == '\n')
        {
            Advance(lexer);
        }
        else if (lexer.Lookahead == '\r')
        {
            Advance(lexer);
            if (lexer.Lookahead == '\n')
            {
                Advance(lexer);
            }
        }
        else
        {
            return false;
        }

        _indentation = 0;
        _column = 0;
        for (;;)
        {
            if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                _indentation = (byte)(_indentation + Advance(lexer));
            }
            else
            {
                break;
            }
        }

        _simulate = true;
        byte matchedTemp = 0;
        while (matchedTemp < _openBlocks.Count)
        {
            if (Match(lexer, _openBlocks[matchedTemp]))
            {
                matchedTemp++;
            }
            else
            {
                return false;
            }
        }

        // check if delimiter row has the same number of cells and at least one pipe
        int delimiterCellCount = 0;
        if (lexer.Lookahead == '|')
        {
            Advance(lexer);
        }

        for (;;)
        {
            while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                Advance(lexer);
            }

            if (lexer.Lookahead == '|')
            {
                delimiterCellCount++;
                Advance(lexer);
                continue;
            }

            if (lexer.Lookahead == ':')
            {
                Advance(lexer);
                if (lexer.Lookahead != '-')
                {
                    return false;
                }
            }

            bool hadOneMinus = false;
            while (lexer.Lookahead == '-')
            {
                hadOneMinus = true;
                Advance(lexer);
            }

            if (hadOneMinus)
            {
                delimiterCellCount++;
            }

            if (lexer.Lookahead == ':')
            {
                if (!hadOneMinus)
                {
                    return false;
                }

                Advance(lexer);
            }

            while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                Advance(lexer);
            }

            if (lexer.Lookahead == '|')
            {
                if (!hadOneMinus)
                {
                    delimiterCellCount++;
                }

                Advance(lexer);
                continue;
            }

            if (lexer.Lookahead != '\r' && lexer.Lookahead != '\n')
            {
                return false;
            }
            else
            {
                break;
            }
        }

        // if the cell counts are not equal then this is not a table
        if (cellCount != delimiterCellCount)
        {
            return false;
        }

        lexer.ResultSymbol = (ushort)PipeTableStart;
        return true;
    }

    // scan of scanner.c; the exported function below only clears simulate first.
    private bool ScanCore(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // Once MaxOpenBlocks blocks are open, block markers are parsed as text
        if (_openBlocks.Count >= MaxOpenBlocks)
        {
            bool[] limitedValidSymbols = new bool[NumTokenTypes];
            for (int i = 0; i < NumTokenTypes; i++)
            {
                limitedValidSymbols[i] = validSymbols[i] && !OpensBlock(i);
            }

            validSymbols = limitedValidSymbols;
        }

        // A normal tree-sitter rule decided that the current branch is invalid and
        // now "requests" an error to stop the branch
        if (validSymbols[TriggerError])
        {
            return EmitError(lexer);
        }

        // Close the inner most block after the next line break as requested. See
        // `$._close_block` in grammar.js
        if (validSymbols[CloseBlock])
        {
            _state |= StateCloseBlock;
            lexer.ResultSymbol = (ushort)CloseBlock;
            return true;
        }

        // if we are at the end of the file and there are still open blocks close
        // them all
        if (lexer.Eof())
        {
            if (validSymbols[TokenEof])
            {
                lexer.ResultSymbol = (ushort)TokenEof;
                return true;
            }

            if (_openBlocks.Count > 0)
            {
                lexer.ResultSymbol = (ushort)BlockClose;
                if (!_simulate)
                {
                    PopBlock();
                }

                return true;
            }

            return false;
        }

        if ((_state & StateMatching) == 0)
        {
            // Parse any preceeding whitespace and remember its length. This makes a
            // lot of parsing quite a bit easier.
            for (;;)
            {
                if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                {
                    _indentation = (byte)(_indentation + Advance(lexer));
                }
                else
                {
                    break;
                }
            }

            // We are not matching. This is where the parsing logic for most
            // "normal" token is. Most importantly parsing logic for the start of
            // new blocks.
            if (validSymbols[IndentedChunkStart] &&
                !validSymbols[NoIndentedChunk])
            {
                if (_indentation >= 4 && lexer.Lookahead != '\n' &&
                    lexer.Lookahead != '\r')
                {
                    lexer.ResultSymbol = (ushort)IndentedChunkStart;
                    if (!_simulate)
                    {
                        PushBlock(Block.IndentedCodeBlock);
                    }

                    _indentation = (byte)(_indentation - 4);
                    return true;
                }
            }

            // Decide which tokens to consider based on the first non-whitespace
            // character
            switch (lexer.Lookahead)
            {
                case '\r':
                case '\n':
                    if (validSymbols[BlankLineStart])
                    {
                        // A blank line token is actually just 0 width, so do not
                        // consume the characters
                        lexer.ResultSymbol = (ushort)BlankLineStart;
                        return true;
                    }

                    break;
                case '`':
                    // A backtick could mark the beginning or ending of a fenced
                    // code block.
                    return ParseFencedCodeBlock('`', lexer, validSymbols);
                case '~':
                    // A tilde could mark the beginning or ending of a fenced code
                    // block.
                    return ParseFencedCodeBlock('~', lexer, validSymbols);
                case '*':
                    // A star could either mark  a list item or a thematic break.
                    // This code is similar to the code for '_' and '+'.
                    return ParseStar(lexer, validSymbols);
                case '_':
                    return ParseThematicBreakUnderscore(lexer, validSymbols);
                case '>':
                    // A '>' could mark the beginning of a block quote
                    return ParseBlockQuote(lexer, validSymbols);
                case '#':
                    // A '#' could mark a atx heading
                    return ParseAtxHeading(lexer, validSymbols);
                case '=':
                    // A '=' could mark a setext underline
                    return ParseSetextUnderline(lexer, validSymbols);
                case '+':
                    // A '+' could be a list marker
                    return ParsePlus(lexer, validSymbols);
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    // A number could be a list marker (if followed by a dot or a
                    // parenthesis)
                    return ParseOrderedListMarker(lexer, validSymbols);
                case '-':
                    // A minus could mark a list marker, a thematic break or a
                    // setext underline
                    return ParseMinus(lexer, validSymbols);
                case '<':
                    // A < could mark the beginning of a html block
                    return ParseHtmlBlock(lexer, validSymbols);
            }

            if (lexer.Lookahead != '\r' && lexer.Lookahead != '\n' &&
                validSymbols[PipeTableStart])
            {
                return ParsePipeTable(lexer, validSymbols);
            }
        }
        else
        {
            // we are in the state of trying to match all currently open blocks
            bool partialSuccess = false;
            while (_matched < _openBlocks.Count)
            {
                if (_matched == _openBlocks.Count - 1 &&
                    (_state & StateCloseBlock) != 0)
                {
                    if (!partialSuccess)
                    {
                        _state = (byte)(_state & ~StateCloseBlock);
                    }

                    break;
                }

                if (Match(lexer, _openBlocks[_matched]))
                {
                    partialSuccess = true;
                    _matched++;
                }
                else
                {
                    if ((_state & StateWasSoftLineBreak) != 0)
                    {
                        _state = (byte)(_state & ~StateMatching);
                    }

                    break;
                }
            }

            if (partialSuccess)
            {
                if (_matched == _openBlocks.Count)
                {
                    _state = (byte)(_state & ~StateMatching);
                }

                lexer.ResultSymbol = (ushort)BlockContinuation;
                return true;
            }

            if ((_state & StateWasSoftLineBreak) == 0)
            {
                lexer.ResultSymbol = (ushort)BlockClose;
                PopBlock();
                if (_matched == _openBlocks.Count)
                {
                    _state = (byte)(_state & ~StateMatching);
                }

                return true;
            }
        }

        // The parser just encountered a line break. Setup the state correspondingly
        if ((validSymbols[LineEnding] || validSymbols[SoftLineEnding] ||
             validSymbols[PipeTableLineEnding]) &&
            (lexer.Lookahead == '\n' || lexer.Lookahead == '\r'))
        {
            if (lexer.Lookahead == '\r')
            {
                Advance(lexer);
                if (lexer.Lookahead == '\n')
                {
                    Advance(lexer);
                }
            }
            else
            {
                Advance(lexer);
            }

            _indentation = 0;
            _column = 0;
            if ((_state & StateCloseBlock) == 0 &&
                (validSymbols[SoftLineEnding] ||
                 validSymbols[PipeTableLineEnding]))
            {
                lexer.MarkEnd();
                for (;;)
                {
                    if (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
                    {
                        _indentation = (byte)(_indentation + Advance(lexer));
                    }
                    else
                    {
                        break;
                    }
                }

                _simulate = true;
                byte matchedTemp = _matched;
                _matched = 0;
                bool oneWillBeMatched = false;
                while (_matched < _openBlocks.Count)
                {
                    if (Match(lexer, _openBlocks[_matched]))
                    {
                        _matched++;
                        oneWillBeMatched = true;
                    }
                    else
                    {
                        break;
                    }
                }

                bool allWillBeMatched = _matched == _openBlocks.Count;
                if (!lexer.Eof() &&
                    !ScanCore(lexer, ParagraphInterruptSymbols))
                {
                    _matched = matchedTemp;

                    // If the last line break ended a paragraph and no new block
                    // opened, the last line break should have been a soft line
                    // break Reset the counter for matched blocks
                    _matched = 0;
                    _indentation = 0;
                    _column = 0;

                    // If there is at least one open block, we should be in the
                    // matching state. Also set the matching flag if a
                    // `$._soft_line_break_marker` can be emitted so it does get
                    // emitted.
                    if (oneWillBeMatched)
                    {
                        _state |= StateMatching;
                    }
                    else
                    {
                        _state = (byte)(_state & ~StateMatching);
                    }

                    if (validSymbols[PipeTableLineEnding])
                    {
                        if (allWillBeMatched)
                        {
                            lexer.ResultSymbol = (ushort)PipeTableLineEnding;
                            return true;
                        }
                    }
                    else
                    {
                        lexer.ResultSymbol = (ushort)SoftLineEnding;

                        // reset some state variables
                        _state |= StateWasSoftLineBreak;
                        return true;
                    }
                }
                else
                {
                    _matched = matchedTemp;
                }

                _indentation = 0;
                _column = 0;
            }

            if (validSymbols[LineEnding])
            {
                // If the last line break ended a paragraph and no new block opened,
                // the last line break should have been a soft line break Reset the
                // counter for matched blocks
                _matched = 0;

                // If there is at least one open block, we should be in the matching
                // state. Also set the matching flag if a
                // `$._soft_line_break_marker` can be emitted so it does get
                // emitted.
                if (_openBlocks.Count > 0)
                {
                    _state |= StateMatching;
                }
                else
                {
                    _state = (byte)(_state & ~StateMatching);
                }

                // reset some state variables
                _state = (byte)(_state & ~StateWasSoftLineBreak);
                lexer.ResultSymbol = (ushort)LineEnding;
                return true;
            }
        }

        return false;
    }

    /// <summary>tree_sitter_markdown_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        _simulate = false;
        return ScanCore(lexer, validSymbols);
    }

    /// <summary>tree_sitter_markdown_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        int size = 0;
        buffer[size++] = _state;
        buffer[size++] = _matched;
        buffer[size++] = _indentation;
        buffer[size++] = _column;
        buffer[size++] = _fencedCodeBlockDelimiterLength;
        int blocksCount = _openBlocks.Count;
        int maxBlocksCount = (SerializationBufferSize - size) / BlockSize;
        if (blocksCount > maxBlocksCount)
        {
            blocksCount = maxBlocksCount;
        }

        for (int i = 0; i < blocksCount; i++)
        {
            int value = (int)_openBlocks[i];
            buffer[size] = (byte)(value & 0xFF);
            buffer[size + 1] = (byte)((value >> 8) & 0xFF);
            buffer[size + 2] = (byte)((value >> 16) & 0xFF);
            buffer[size + 3] = (byte)((value >> 24) & 0xFF);
            size += BlockSize;
        }

        return (uint)size;
    }

    /// <summary>tree_sitter_markdown_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        _openBlocks.Clear();
        _state = 0;
        _matched = 0;
        _indentation = 0;
        _column = 0;
        _fencedCodeBlockDelimiterLength = 0;
        if (buffer.Length > 0)
        {
            int size = 0;
            _state = buffer[size++];
            _matched = buffer[size++];
            _indentation = buffer[size++];
            _column = buffer[size++];
            _fencedCodeBlockDelimiterLength = buffer[size++];
            int blocksSize = buffer.Length - size;
            if (blocksSize > 0)
            {
                int blocksCount = blocksSize / BlockSize;
                for (int i = 0; i < blocksCount; i++)
                {
                    int value = buffer[size] | (buffer[size + 1] << 8) |
                                (buffer[size + 2] << 16) | (buffer[size + 3] << 24);
                    _openBlocks.Add((Block)value);
                    size += BlockSize;
                }
            }
        }
    }

    // iswalpha of the C locale.
    private static bool IsWAlpha(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    // iswalnum of the C locale.
    private static bool IsWAlNum(int c) => IsWAlpha(c) || (c >= '0' && c <= '9');

    // towlower of the C locale: only 'A'..'Z' map to 'a'..'z'. The caller checks
    // iswalpha first, so only ASCII letters get here.
    private static char ToWLower(int c) => c >= 'A' && c <= 'Z' ? (char)(c - 'A' + 'a') : (char)c;
}
