// Port of src/scanner.c of tree-sitter-python (commit 26855eabccb19c6abf499fbc5b8dc7cc9ab8bc64,
// grammars/Python/upstream/src/scanner.c), the external scanner of the Python grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - Array(uint16_t) indents and Array(Delimiter) delimiters are List<ushort> and List<byte>
//   (Delimiter is one char of flags).
// - The serialized state is the same bytes as the C scanner writes.


namespace TreeSitter.Grammars.Python;

/// <summary>The external scanner of the Python grammar (indentation and strings).</summary>
internal sealed class PythonScanner : IExternalScanner
{
    // enum TokenType
    private const int Newline = 0;
    private const int Indent = 1;
    private const int Dedent = 2;
    private const int StringStart = 3;
    private const int StringContent = 4;
    private const int EscapeInterpolation = 5;
    private const int StringEnd = 6;
    private const int Comment = 7;
    private const int CloseParen = 8;
    private const int CloseBracket = 9;
    private const int CloseBrace = 10;
    private const int Except = 11;

    // Flags
    private const byte SingleQuote = 1 << 0;
    private const byte DoubleQuote = 1 << 1;
    private const byte BackQuote = 1 << 2;
    private const byte Raw = 1 << 3;
    private const byte Format = 1 << 4;
    private const byte Triple = 1 << 5;
    private const byte Bytes = 1 << 6;

    // TREE_SITTER_SERIALIZATION_BUFFER_SIZE
    private const int SerializationBufferSize = 1024;

    private readonly List<ushort> _indents = new List<ushort>();
    private readonly List<byte> _delimiters = new List<byte>();
    private bool _insideInterpolatedString;

    /// <summary>tree_sitter_python_external_scanner_create</summary>
    public PythonScanner()
    {
        Deserialize(ReadOnlySpan<byte>.Empty);
    }

    private static bool IsFormat(byte delimiter) => (delimiter & Format) != 0;

    private static bool IsRaw(byte delimiter) => (delimiter & Raw) != 0;

    private static bool IsTriple(byte delimiter) => (delimiter & Triple) != 0;

    private static bool IsBytes(byte delimiter) => (delimiter & Bytes) != 0;

    private static int EndCharacter(byte delimiter)
    {
        if ((delimiter & SingleQuote) != 0)
        {
            return '\'';
        }

        if ((delimiter & DoubleQuote) != 0)
        {
            return '"';
        }

        if ((delimiter & BackQuote) != 0)
        {
            return '`';
        }

        return 0;
    }

    // set_end_character: the callers pass only the three quote characters.
    private static byte SetEndCharacter(byte delimiter, int character) => character switch
    {
        '\'' => (byte)(delimiter | SingleQuote),
        '"' => (byte)(delimiter | DoubleQuote),
        _ => (byte)(delimiter | BackQuote),
    };

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    /// <summary>tree_sitter_python_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        bool errorRecoveryMode = validSymbols[StringContent] && validSymbols[Indent];
        bool withinBrackets = validSymbols[CloseBrace] || validSymbols[CloseParen] || validSymbols[CloseBracket];

        bool advancedOnce = false;
        if (validSymbols[EscapeInterpolation] && _delimiters.Count > 0 &&
            (lexer.Lookahead == '{' || lexer.Lookahead == '}') && !errorRecoveryMode)
        {
            byte delimiter = _delimiters[^1];
            if (IsFormat(delimiter))
            {
                lexer.MarkEnd();
                bool isLeftBrace = lexer.Lookahead == '{';
                Advance(lexer);
                advancedOnce = true;
                if ((lexer.Lookahead == '{' && isLeftBrace) || (lexer.Lookahead == '}' && !isLeftBrace))
                {
                    Advance(lexer);
                    lexer.MarkEnd();
                    lexer.ResultSymbol = EscapeInterpolation;
                    return true;
                }

                return false;
            }
        }

        if (validSymbols[StringContent] && _delimiters.Count > 0 && !errorRecoveryMode)
        {
            byte delimiter = _delimiters[^1];
            int endChar = EndCharacter(delimiter);
            bool hasContent = advancedOnce;
            while (lexer.Lookahead != 0)
            {
                if ((advancedOnce || lexer.Lookahead == '{' || lexer.Lookahead == '}') && IsFormat(delimiter))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = StringContent;
                    return hasContent;
                }

                if (lexer.Lookahead == '\\')
                {
                    if (IsRaw(delimiter))
                    {
                        // Step over the backslash.
                        Advance(lexer);

                        // Step over any escaped quotes.
                        if (lexer.Lookahead == EndCharacter(delimiter) || lexer.Lookahead == '\\')
                        {
                            Advance(lexer);
                        }

                        // Step over newlines
                        if (lexer.Lookahead == '\r')
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == '\n')
                            {
                                Advance(lexer);
                            }
                        }
                        else if (lexer.Lookahead == '\n')
                        {
                            Advance(lexer);
                        }

                        continue;
                    }

                    if (IsBytes(delimiter))
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == 'N' || lexer.Lookahead == 'u' || lexer.Lookahead == 'U')
                        {
                            // In bytes string, \N{...}, \uXXXX and \UXXXXXXXX are
                            // not escape sequences
                            Advance(lexer);
                        }
                        else
                        {
                            lexer.ResultSymbol = StringContent;
                            return hasContent;
                        }
                    }
                    else
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = StringContent;
                        return hasContent;
                    }
                }
                else if (lexer.Lookahead == endChar)
                {
                    if (IsTriple(delimiter))
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == endChar)
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == endChar)
                            {
                                if (hasContent)
                                {
                                    lexer.ResultSymbol = StringContent;
                                }
                                else
                                {
                                    Advance(lexer);
                                    lexer.MarkEnd();
                                    _delimiters.RemoveAt(_delimiters.Count - 1);
                                    lexer.ResultSymbol = StringEnd;
                                    _insideInterpolatedString = false;
                                }

                                return true;
                            }

                            lexer.MarkEnd();
                            lexer.ResultSymbol = StringContent;
                            return true;
                        }

                        lexer.MarkEnd();
                        lexer.ResultSymbol = StringContent;
                        return true;
                    }

                    if (hasContent)
                    {
                        lexer.ResultSymbol = StringContent;
                    }
                    else
                    {
                        Advance(lexer);
                        _delimiters.RemoveAt(_delimiters.Count - 1);
                        lexer.ResultSymbol = StringEnd;
                        _insideInterpolatedString = false;
                    }

                    lexer.MarkEnd();
                    return true;
                }
                else if (lexer.Lookahead == '\n' && hasContent && !IsTriple(delimiter))
                {
                    return false;
                }

                Advance(lexer);
                hasContent = true;
            }
        }

        lexer.MarkEnd();

        bool foundEndOfLine = false;
        ushort indentLength = 0;
        int firstCommentIndentLength = -1;
        for (;;)
        {
            if (lexer.Lookahead == '\n')
            {
                foundEndOfLine = true;
                indentLength = 0;
                Skip(lexer);
            }
            else if (lexer.Lookahead == ' ')
            {
                indentLength++;
                Skip(lexer);
            }
            else if (lexer.Lookahead == '\r' || lexer.Lookahead == '\f')
            {
                indentLength = 0;
                Skip(lexer);
            }
            else if (lexer.Lookahead == '\t')
            {
                indentLength += 8;
                Skip(lexer);
            }
            else if (lexer.Lookahead == '#' && (validSymbols[Indent] || validSymbols[Dedent] ||
                                                validSymbols[Newline] || validSymbols[Except]))
            {
                // If we haven't found an EOL yet,
                // then this is a comment after an expression:
                //   foo = bar # comment
                // Just return, since we don't want to generate an indent/dedent
                // token.
                if (!foundEndOfLine)
                {
                    return false;
                }

                if (firstCommentIndentLength == -1)
                {
                    firstCommentIndentLength = indentLength;
                }

                while (lexer.Lookahead != 0 && lexer.Lookahead != '\n')
                {
                    Skip(lexer);
                }

                Skip(lexer);
                indentLength = 0;
            }
            else if (lexer.Lookahead == '\\')
            {
                Skip(lexer);
                if (lexer.Lookahead == '\r')
                {
                    Skip(lexer);
                }

                if (lexer.Lookahead == '\n' || lexer.Eof())
                {
                    Skip(lexer);
                }
                else
                {
                    return false;
                }
            }
            else if (lexer.Eof())
            {
                indentLength = 0;
                foundEndOfLine = true;
                break;
            }
            else
            {
                break;
            }
        }

        if (foundEndOfLine)
        {
            if (_indents.Count > 0)
            {
                ushort currentIndentLength = _indents[^1];

                if (validSymbols[Indent] && indentLength > currentIndentLength)
                {
                    _indents.Add(indentLength);
                    lexer.ResultSymbol = Indent;
                    return true;
                }

                bool nextTokIsStringStart =
                    lexer.Lookahead == '"' || lexer.Lookahead == '\'' || lexer.Lookahead == '`';

                if ((validSymbols[Dedent] ||
                     (!validSymbols[Newline] && !(validSymbols[StringStart] && nextTokIsStringStart) &&
                      !withinBrackets)) &&
                    indentLength < currentIndentLength && !_insideInterpolatedString &&

                    // Wait to create a dedent token until we've consumed any
                    // comments
                    // whose indentation matches the current block.
                    firstCommentIndentLength < currentIndentLength)
                {
                    _indents.RemoveAt(_indents.Count - 1);
                    lexer.ResultSymbol = Dedent;
                    return true;
                }
            }

            if (validSymbols[Newline] && !errorRecoveryMode)
            {
                lexer.ResultSymbol = Newline;
                return true;
            }
        }

        if (firstCommentIndentLength == -1 && validSymbols[StringStart])
        {
            byte delimiter = 0;

            bool hasFlags = false;
            while (lexer.Lookahead != 0)
            {
                if (lexer.Lookahead == 'f' || lexer.Lookahead == 'F' || lexer.Lookahead == 't' ||
                    lexer.Lookahead == 'T')
                {
                    delimiter |= Format;
                }
                else if (lexer.Lookahead == 'r' || lexer.Lookahead == 'R')
                {
                    delimiter |= Raw;
                }
                else if (lexer.Lookahead == 'b' || lexer.Lookahead == 'B')
                {
                    delimiter |= Bytes;
                }
                else if (lexer.Lookahead != 'u' && lexer.Lookahead != 'U')
                {
                    break;
                }

                hasFlags = true;
                Advance(lexer);
            }

            if (lexer.Lookahead == '`')
            {
                delimiter = SetEndCharacter(delimiter, '`');
                Advance(lexer);
                lexer.MarkEnd();
            }
            else if (lexer.Lookahead == '\'')
            {
                delimiter = SetEndCharacter(delimiter, '\'');
                Advance(lexer);
                lexer.MarkEnd();
                if (lexer.Lookahead == '\'')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '\'')
                    {
                        Advance(lexer);
                        lexer.MarkEnd();
                        delimiter |= Triple;
                    }
                }
            }
            else if (lexer.Lookahead == '"')
            {
                delimiter = SetEndCharacter(delimiter, '"');
                Advance(lexer);
                lexer.MarkEnd();
                if (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '"')
                    {
                        Advance(lexer);
                        lexer.MarkEnd();
                        delimiter |= Triple;
                    }
                }
            }

            if (EndCharacter(delimiter) != 0)
            {
                _delimiters.Add(delimiter);
                lexer.ResultSymbol = StringStart;
                _insideInterpolatedString = IsFormat(delimiter);
                return true;
            }

            if (hasFlags)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>tree_sitter_python_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        int size = 0;

        buffer[size++] = _insideInterpolatedString ? (byte)1 : (byte)0;

        int delimiterCount = _delimiters.Count;
        if (delimiterCount > byte.MaxValue)
        {
            delimiterCount = byte.MaxValue;
        }

        buffer[size++] = (byte)delimiterCount;

        for (int i = 0; i < delimiterCount; i++)
        {
            buffer[size + i] = _delimiters[i];
        }

        size += delimiterCount;

        // As in C, an odd size of 1023 writes one byte past the buffer (here an
        // IndexOutOfRangeException); it needs more than 500 indentation levels.
        int iter = 1;
        for (; iter < _indents.Count && size < SerializationBufferSize; ++iter)
        {
            ushort indentValue = _indents[iter];
            buffer[size++] = (byte)(indentValue & 0xFF);
            buffer[size++] = (byte)((indentValue >> 8) & 0xFF);
        }

        return (uint)size;
    }

    /// <summary>tree_sitter_python_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        _delimiters.Clear();
        _indents.Clear();
        _indents.Add(0);

        // inside_interpolated_string is kept when the buffer is empty, as in C.
        if (buffer.Length > 0)
        {
            int size = 0;

            _insideInterpolatedString = buffer[size++] != 0;

            int delimiterCount = buffer[size++];
            if (delimiterCount > 0)
            {
                for (int i = 0; i < delimiterCount; i++)
                {
                    _delimiters.Add(buffer[size + i]);
                }

                size += delimiterCount;
            }

            for (; size + 1 < buffer.Length; size += 2)
            {
                ushort indentValue = (ushort)(buffer[size] | (buffer[size + 1] << 8));
                _indents.Add(indentValue);
            }
        }
    }
}
