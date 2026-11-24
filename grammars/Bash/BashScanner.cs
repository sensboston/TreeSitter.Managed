// Port of src/scanner.c of tree-sitter-bash (commit a06c2e4415e9bc0346c6b86d401879ffb44058f7,
// grammars/Bash/upstream/src/scanner.c), the external scanner of the Bash grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - Array(char) (String) is List<sbyte>: a C char is signed on the platforms the oracle runs on,
//   and the lexer value is truncated to it as in C (array_push of an int32 into a char array).
//   strcmp compares the bytes as unsigned and stops at the terminating zero.
// - Array(Heredoc) is List<Heredoc>; Heredoc is a class, so the C pointers into the array
//   (array_back, array_get) are references to the same objects. As in C, deserialize never
//   shrinks the array, and pop only removes the last heredoc.
// - The gotos of scan() (regex, extglob_pattern, expansion_word, brace_start) are the entry
//   points of ScanTail: the labelled blocks in their order, each one falling through to the
//   next when its condition is false. Each jump returns what the fall-through would return.
// - uint8_t and uint32_t fields and locals are byte and uint, with the C wrap-around.
// - Serialized state: the same bytes as the C scanner writes on a little endian platform.

using System.Diagnostics;

namespace TreeSitter.Grammars.Bash;

/// <summary>The external scanner of the Bash grammar (heredocs, words, test operators and globs).</summary>
internal sealed class BashScanner : IExternalScanner
{
    // enum TokenType
    private const int HeredocStart = 0;
    private const int SimpleHeredocBody = 1;
    private const int HeredocBodyBeginning = 2;
    private const int HeredocContent = 3;
    private const int HeredocEnd = 4;
    private const int FileDescriptor = 5;
    private const int EmptyValue = 6;
    private const int Concat = 7;
    private const int VariableName = 8;
    private const int TestOperator = 9;
    private const int Regex = 10;
    private const int RegexNoSlash = 11;
    private const int RegexNoSpace = 12;
    private const int ExpansionWord = 13;
    private const int ExtglobPattern = 14;
    private const int BareDollar = 15;
    private const int BraceStart = 16;
    private const int ImmediateDoubleHash = 17;
    private const int ExternalExpansionSymHash = 18;
    private const int ExternalExpansionSymBang = 19;
    private const int ExternalExpansionSymEqual = 20;
    private const int ClosingBrace = 21;
    private const int ClosingBracket = 22;
    private const int HeredocArrow = 23;
    private const int HeredocArrowDash = 24;
    private const int Newline = 25;
    private const int OpeningParen = 26;
    private const int Esac = 27;
    private const int ErrorRecovery = 28;

    // TREE_SITTER_SERIALIZATION_BUFFER_SIZE
    private const int SerializationBufferSize = 1024;

    // The labelled blocks of scan(), in the order of the file (see ScanTail).
    private enum Entry
    {
        Regex,
        ExtglobPattern,
        ExpansionWord,
        BraceStart,
    }

    // Heredoc: the state of one heredoc (the C struct). Delimiter and CurrentLeadingWord are
    // the C String arrays, with the terminating zero of advance_word and scan_heredoc_end_identifier.
    private sealed class Heredoc
    {
        public bool IsRaw { get; set; }

        public bool Started { get; set; }

        public bool AllowsIndent { get; set; }

        public List<sbyte> Delimiter { get; } = new List<sbyte>();

        public List<sbyte> CurrentLeadingWord { get; } = new List<sbyte>();
    }

    private byte _lastGlobParenDepth;
    private bool _extWasInDoubleQuote;
    private bool _extSawOutsideQuote;
    private readonly List<Heredoc> _heredocs = new List<Heredoc>();

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    // iswspace of the C locale.
    private static bool IsWSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // iswalpha of the C locale.
    private static bool IsWAlpha(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    // iswalnum of the C locale.
    private static bool IsWAlNum(int c) => IsWAlpha(c) || (c >= '0' && c <= '9');

    // iswdigit and isdigit of the C locale.
    private static bool IsWDigit(int c) => c >= '0' && c <= '9';

    private static bool InErrorRecovery(ReadOnlySpan<bool> validSymbols) => validSymbols[ErrorRecovery];

    private static void ResetString(List<sbyte> value) => value.Clear();

    private static void ResetHeredoc(Heredoc heredoc)
    {
        heredoc.IsRaw = false;
        heredoc.Started = false;
        heredoc.AllowsIndent = false;
        ResetString(heredoc.Delimiter);
    }

    // reset: the heredocs stay in the array, each one is reset.
    private void Reset()
    {
        for (int i = 0; i < _heredocs.Count; i++)
        {
            ResetHeredoc(_heredocs[i]);
        }
    }

    /// <summary>tree_sitter_bash_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        int size = 0;

        buffer[size++] = _lastGlobParenDepth;
        buffer[size++] = _extWasInDoubleQuote ? (byte)1 : (byte)0;
        buffer[size++] = _extSawOutsideQuote ? (byte)1 : (byte)0;
        buffer[size++] = (byte)_heredocs.Count;

        for (int i = 0; i < _heredocs.Count; i++)
        {
            Heredoc heredoc = _heredocs[i];
            if (size + 3 + sizeof(uint) + heredoc.Delimiter.Count >= SerializationBufferSize)
            {
                return 0;
            }

            buffer[size++] = heredoc.IsRaw ? (byte)1 : (byte)0;
            buffer[size++] = heredoc.Started ? (byte)1 : (byte)0;
            buffer[size++] = heredoc.AllowsIndent ? (byte)1 : (byte)0;

            // memcpy of the uint32_t size (little endian)
            uint delimiterSize = (uint)heredoc.Delimiter.Count;
            buffer[size++] = (byte)(delimiterSize & 0xFF);
            buffer[size++] = (byte)((delimiterSize >> 8) & 0xFF);
            buffer[size++] = (byte)((delimiterSize >> 16) & 0xFF);
            buffer[size++] = (byte)((delimiterSize >> 24) & 0xFF);

            for (int j = 0; j < heredoc.Delimiter.Count; j++)
            {
                buffer[size++] = (byte)heredoc.Delimiter[j];
            }
        }

        return (uint)size;
    }

    /// <summary>tree_sitter_bash_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            Reset();
            return;
        }

        int size = 0;
        _lastGlobParenDepth = buffer[size++];
        _extWasInDoubleQuote = buffer[size++] != 0;
        _extSawOutsideQuote = buffer[size++] != 0;
        int heredocCount = buffer[size++];
        for (int i = 0; i < heredocCount; i++)
        {
            Heredoc heredoc;
            if (i < _heredocs.Count)
            {
                heredoc = _heredocs[i];
            }
            else
            {
                heredoc = new Heredoc();
                _heredocs.Add(heredoc);
            }

            heredoc.IsRaw = buffer[size++] != 0;
            heredoc.Started = buffer[size++] != 0;
            heredoc.AllowsIndent = buffer[size++] != 0;

            // memcpy of the uint32_t size (little endian); the contents follow it.
            uint delimiterSize = (uint)(buffer[size] | (buffer[size + 1] << 8) |
                                        (buffer[size + 2] << 16) | (buffer[size + 3] << 24));
            size += sizeof(uint);
            heredoc.Delimiter.Clear();
            for (int j = 0; j < delimiterSize; j++)
            {
                heredoc.Delimiter.Add((sbyte)buffer[size + j]);
            }

            size += (int)delimiterSize;
        }

        Debug.Assert(size == buffer.Length);
    }

    /// <summary>
    /// Consume a "word" in POSIX parlance, and returns it unquoted.
    ///
    /// This is an approximate implementation that doesn't deal with any
    /// POSIX-mandated substitution, and assumes the default value for
    /// IFS.
    /// </summary>
    private static bool AdvanceWord(Lexer lexer, List<sbyte> unquotedWord)
    {
        bool empty = true;

        int quote = 0;
        if (lexer.Lookahead == '\'' || lexer.Lookahead == '"')
        {
            quote = lexer.Lookahead;
            Advance(lexer);
        }

        while (lexer.Lookahead != 0 &&
               !(quote != 0 ? lexer.Lookahead == quote || lexer.Lookahead == '\r' || lexer.Lookahead == '\n'
                   : IsWSpace(lexer.Lookahead)))
        {
            if (lexer.Lookahead == '\\')
            {
                Advance(lexer);
                if (lexer.Lookahead == 0)
                {
                    return false;
                }
            }

            empty = false;
            unquotedWord.Add((sbyte)lexer.Lookahead);
            Advance(lexer);
        }

        unquotedWord.Add(0);

        if (quote != 0 && lexer.Lookahead == quote)
        {
            Advance(lexer);
        }

        return !empty;
    }

    private static bool ScanBareDollar(Lexer lexer)
    {
        while (IsWSpace(lexer.Lookahead) && lexer.Lookahead != '\n' && !lexer.Eof())
        {
            Skip(lexer);
        }

        if (lexer.Lookahead == '$')
        {
            Advance(lexer);
            lexer.ResultSymbol = BareDollar;
            lexer.MarkEnd();
            return IsWSpace(lexer.Lookahead) || lexer.Eof() || lexer.Lookahead == '"';
        }

        return false;
    }

    private static bool ScanHeredocStart(Heredoc heredoc, Lexer lexer)
    {
        while (IsWSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        lexer.ResultSymbol = HeredocStart;
        heredoc.IsRaw = lexer.Lookahead == '\'' || lexer.Lookahead == '"' || lexer.Lookahead == '\\';

        bool foundDelimiter = AdvanceWord(lexer, heredoc.Delimiter);
        if (!foundDelimiter)
        {
            ResetString(heredoc.Delimiter);
            return false;
        }

        return foundDelimiter;
    }

    private static bool ScanHeredocEndIdentifier(Heredoc heredoc, Lexer lexer)
    {
        ResetString(heredoc.CurrentLeadingWord);

        // Scan the first 'n' characters on this line, to see if they match the
        // heredoc delimiter
        int size = 0;
        if (heredoc.Delimiter.Count > 0)
        {
            while (lexer.Lookahead != '\0' && lexer.Lookahead != '\n' &&
                   heredoc.CurrentLeadingWord.Count < heredoc.Delimiter.Count &&
                   heredoc.Delimiter[size] == lexer.Lookahead)
            {
                heredoc.CurrentLeadingWord.Add((sbyte)lexer.Lookahead);
                Advance(lexer);
                size++;
            }
        }

        heredoc.CurrentLeadingWord.Add(0);
        return heredoc.Delimiter.Count == 0
            ? false
            : CStringEquals(heredoc.CurrentLeadingWord, heredoc.Delimiter);
    }

    // strcmp(a, b) == 0 of two zero terminated char arrays (unsigned comparison of the bytes).
    private static bool CStringEquals(List<sbyte> a, List<sbyte> b)
    {
        for (int i = 0; ; i++)
        {
            byte x = i < a.Count ? (byte)a[i] : (byte)0;
            byte y = i < b.Count ? (byte)b[i] : (byte)0;
            if (x != y)
            {
                return false;
            }

            if (x == 0)
            {
                return true;
            }
        }
    }

    private bool ScanHeredocContent(Lexer lexer, int middleType, int endType)
    {
        bool didAdvance = false;
        Heredoc heredoc = _heredocs[^1];

        for (;;)
        {
            switch (lexer.Lookahead)
            {
                case '\0':
                {
                    if (lexer.Eof() && didAdvance)
                    {
                        ResetHeredoc(heredoc);
                        lexer.ResultSymbol = (ushort)endType;
                        return true;
                    }

                    return false;
                }

                case '\\':
                {
                    didAdvance = true;
                    Advance(lexer);
                    Advance(lexer);
                    break;
                }

                case '$':
                {
                    if (heredoc.IsRaw)
                    {
                        didAdvance = true;
                        Advance(lexer);
                        break;
                    }

                    if (didAdvance)
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = (ushort)middleType;
                        heredoc.Started = true;
                        Advance(lexer);
                        if (IsWAlpha(lexer.Lookahead) || lexer.Lookahead == '{' || lexer.Lookahead == '(')
                        {
                            return true;
                        }

                        break;
                    }

                    if (middleType == HeredocBodyBeginning && lexer.GetColumn() == 0)
                    {
                        lexer.ResultSymbol = (ushort)middleType;
                        heredoc.Started = true;
                        return true;
                    }

                    return false;
                }

                case '\n':
                {
                    if (!didAdvance)
                    {
                        Skip(lexer);
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    didAdvance = true;
                    if (heredoc.AllowsIndent)
                    {
                        while (IsWSpace(lexer.Lookahead))
                        {
                            Advance(lexer);
                        }
                    }

                    lexer.ResultSymbol = (ushort)(heredoc.Started ? middleType : endType);
                    lexer.MarkEnd();
                    if (ScanHeredocEndIdentifier(heredoc, lexer))
                    {
                        if (lexer.ResultSymbol == HeredocEnd)
                        {
                            _heredocs.RemoveAt(_heredocs.Count - 1);
                        }

                        return true;
                    }

                    break;
                }

                default:
                {
                    if (lexer.GetColumn() == 0)
                    {
                        // an alternative is to check the starting column of the
                        // heredoc body and track that statefully
                        while (IsWSpace(lexer.Lookahead))
                        {
                            if (didAdvance)
                            {
                                Advance(lexer);
                            }
                            else
                            {
                                Skip(lexer);
                            }
                        }

                        if (endType != SimpleHeredocBody)
                        {
                            lexer.ResultSymbol = (ushort)middleType;
                            if (ScanHeredocEndIdentifier(heredoc, lexer))
                            {
                                return true;
                            }
                        }

                        if (endType == SimpleHeredocBody)
                        {
                            lexer.ResultSymbol = (ushort)endType;
                            lexer.MarkEnd();
                            if (ScanHeredocEndIdentifier(heredoc, lexer))
                            {
                                return true;
                            }
                        }
                    }

                    didAdvance = true;
                    Advance(lexer);
                    break;
                }
            }
        }
    }

    /// <summary>tree_sitter_bash_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[Concat] && !InErrorRecovery(validSymbols))
        {
            if (!(lexer.Lookahead == 0 || IsWSpace(lexer.Lookahead) || lexer.Lookahead == '>' ||
                  lexer.Lookahead == '<' || lexer.Lookahead == ')' || lexer.Lookahead == '(' ||
                  lexer.Lookahead == ';' || lexer.Lookahead == '&' || lexer.Lookahead == '|' ||
                  (lexer.Lookahead == '}' && validSymbols[ClosingBrace]) ||
                  (lexer.Lookahead == ']' && validSymbols[ClosingBracket])))
            {
                lexer.ResultSymbol = Concat;
                // So for a`b`, we want to return a concat. We check if the
                // 2nd backtick has whitespace after it, and if it does we
                // return concat.
                if (lexer.Lookahead == '`')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    while (lexer.Lookahead != '`' && !lexer.Eof())
                    {
                        Advance(lexer);
                    }

                    if (lexer.Eof())
                    {
                        return false;
                    }

                    if (lexer.Lookahead == '`')
                    {
                        Advance(lexer);
                    }

                    return IsWSpace(lexer.Lookahead) || lexer.Eof();
                }

                // strings w/ expansions that contains escaped quotes or
                // backslashes need this to return a concat
                if (lexer.Lookahead == '\\')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '"' || lexer.Lookahead == '\'' || lexer.Lookahead == '\\')
                    {
                        return true;
                    }

                    if (lexer.Eof())
                    {
                        return false;
                    }
                }
                else
                {
                    return true;
                }
            }

            if (IsWSpace(lexer.Lookahead) && validSymbols[ClosingBrace] && !validSymbols[ExpansionWord])
            {
                lexer.ResultSymbol = Concat;
                return true;
            }
        }

        if (validSymbols[ImmediateDoubleHash] && !InErrorRecovery(validSymbols))
        {
            // advance two # and ensure not } after
            if (lexer.Lookahead == '#')
            {
                lexer.MarkEnd();
                Advance(lexer);
                if (lexer.Lookahead == '#')
                {
                    Advance(lexer);
                    if (lexer.Lookahead != '}')
                    {
                        lexer.ResultSymbol = ImmediateDoubleHash;
                        lexer.MarkEnd();
                        return true;
                    }
                }
            }
        }

        if (validSymbols[ExternalExpansionSymHash] && !InErrorRecovery(validSymbols))
        {
            if (lexer.Lookahead == '#' || lexer.Lookahead == '=' || lexer.Lookahead == '!')
            {
                lexer.ResultSymbol = (ushort)(lexer.Lookahead == '#' ? ExternalExpansionSymHash
                    : lexer.Lookahead == '!' ? ExternalExpansionSymBang
                    : ExternalExpansionSymEqual);
                Advance(lexer);
                lexer.MarkEnd();
                while (lexer.Lookahead == '#' || lexer.Lookahead == '=' || lexer.Lookahead == '!')
                {
                    Advance(lexer);
                }

                while (IsWSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }

                if (lexer.Lookahead == '}')
                {
                    return true;
                }

                return false;
            }
        }

        if (validSymbols[EmptyValue])
        {
            if (IsWSpace(lexer.Lookahead) || lexer.Eof() || lexer.Lookahead == ';' || lexer.Lookahead == '&')
            {
                lexer.ResultSymbol = EmptyValue;
                return true;
            }
        }

        if ((validSymbols[HeredocBodyBeginning] || validSymbols[SimpleHeredocBody]) && _heredocs.Count > 0 &&
            !_heredocs[^1].Started && !InErrorRecovery(validSymbols))
        {
            return ScanHeredocContent(lexer, HeredocBodyBeginning, SimpleHeredocBody);
        }

        if (validSymbols[HeredocEnd] && _heredocs.Count > 0)
        {
            Heredoc heredoc = _heredocs[^1];
            if (ScanHeredocEndIdentifier(heredoc, lexer))
            {
                heredoc.CurrentLeadingWord.Clear();
                heredoc.Delimiter.Clear();
                _heredocs.RemoveAt(_heredocs.Count - 1);
                lexer.ResultSymbol = HeredocEnd;
                return true;
            }
        }

        if (validSymbols[HeredocContent] && _heredocs.Count > 0 && _heredocs[^1].Started &&
            !InErrorRecovery(validSymbols))
        {
            return ScanHeredocContent(lexer, HeredocContent, HeredocEnd);
        }

        if (validSymbols[HeredocStart] && !InErrorRecovery(validSymbols) && _heredocs.Count > 0)
        {
            return ScanHeredocStart(_heredocs[^1], lexer);
        }

        if (validSymbols[TestOperator] && !validSymbols[ExpansionWord])
        {
            while (IsWSpace(lexer.Lookahead) && lexer.Lookahead != '\n')
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '\\')
            {
                if (validSymbols[ExtglobPattern])
                {
                    return ScanTail(Entry.ExtglobPattern, lexer, validSymbols);
                }

                if (validSymbols[RegexNoSpace])
                {
                    return ScanTail(Entry.Regex, lexer, validSymbols);
                }

                Skip(lexer);

                if (lexer.Eof())
                {
                    return false;
                }

                if (lexer.Lookahead == '\r')
                {
                    Skip(lexer);
                    if (lexer.Lookahead == '\n')
                    {
                        Skip(lexer);
                    }
                }
                else if (lexer.Lookahead == '\n')
                {
                    Skip(lexer);
                }
                else
                {
                    return false;
                }

                while (IsWSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }
            }

            if (lexer.Lookahead == '\n' && !validSymbols[Newline])
            {
                Skip(lexer);

                while (IsWSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }
            }

            if (lexer.Lookahead == '-')
            {
                Advance(lexer);

                bool advancedOnce = false;
                while (IsWAlpha(lexer.Lookahead))
                {
                    advancedOnce = true;
                    Advance(lexer);
                }

                if (IsWSpace(lexer.Lookahead) && advancedOnce)
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '}' && validSymbols[ClosingBrace])
                    {
                        if (validSymbols[ExpansionWord])
                        {
                            lexer.MarkEnd();
                            lexer.ResultSymbol = ExpansionWord;
                            return true;
                        }

                        return false;
                    }

                    lexer.ResultSymbol = TestOperator;
                    return true;
                }

                if (IsWSpace(lexer.Lookahead) && validSymbols[ExtglobPattern])
                {
                    lexer.ResultSymbol = ExtglobPattern;
                    return true;
                }
            }

            if (validSymbols[BareDollar] && !InErrorRecovery(validSymbols) && ScanBareDollar(lexer))
            {
                return true;
            }
        }

        if ((validSymbols[VariableName] || validSymbols[FileDescriptor] || validSymbols[HeredocArrow]) &&
            !validSymbols[RegexNoSlash] && !InErrorRecovery(validSymbols))
        {
            for (;;)
            {
                if ((lexer.Lookahead == ' ' || lexer.Lookahead == '\t' || lexer.Lookahead == '\r' ||
                     (lexer.Lookahead == '\n' && !validSymbols[Newline])) &&
                    !validSymbols[ExpansionWord])
                {
                    Skip(lexer);
                }
                else if (lexer.Lookahead == '\\')
                {
                    Skip(lexer);

                    if (lexer.Eof())
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = VariableName;
                        return true;
                    }

                    if (lexer.Lookahead == '\r')
                    {
                        Skip(lexer);
                    }

                    if (lexer.Lookahead == '\n')
                    {
                        Skip(lexer);
                    }
                    else
                    {
                        if (lexer.Lookahead == '\\' && validSymbols[ExpansionWord])
                        {
                            return ScanTail(Entry.ExpansionWord, lexer, validSymbols);
                        }

                        return false;
                    }
                }
                else
                {
                    break;
                }
            }

            // no '*', '@', '?', '-', '$', '0', '_'
            if (!validSymbols[ExpansionWord] &&
                (lexer.Lookahead == '*' || lexer.Lookahead == '@' || lexer.Lookahead == '?' || lexer.Lookahead == '-' ||
                 lexer.Lookahead == '0' || lexer.Lookahead == '_'))
            {
                lexer.MarkEnd();
                Advance(lexer);
                if (lexer.Lookahead == '=' || lexer.Lookahead == '[' || lexer.Lookahead == ':' ||
                    lexer.Lookahead == '-' || lexer.Lookahead == '%' || lexer.Lookahead == '#' ||
                    lexer.Lookahead == '/')
                {
                    return false;
                }

                if (validSymbols[ExtglobPattern] && IsWSpace(lexer.Lookahead))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = ExtglobPattern;
                    return true;
                }
            }

            if (validSymbols[HeredocArrow] && lexer.Lookahead == '<')
            {
                Advance(lexer);
                if (lexer.Lookahead == '<')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '-')
                    {
                        Advance(lexer);
                        Heredoc heredocDash = new Heredoc();
                        heredocDash.AllowsIndent = true;
                        _heredocs.Add(heredocDash);
                        lexer.ResultSymbol = HeredocArrowDash;
                    }
                    else if (lexer.Lookahead == '<' || lexer.Lookahead == '=')
                    {
                        return false;
                    }
                    else
                    {
                        Heredoc heredoc = new Heredoc();
                        _heredocs.Add(heredoc);
                        lexer.ResultSymbol = HeredocArrow;
                    }

                    return true;
                }

                return false;
            }

            bool isNumber = true;
            if (IsWDigit(lexer.Lookahead))
            {
                Advance(lexer);
            }
            else if (IsWAlpha(lexer.Lookahead) || lexer.Lookahead == '_')
            {
                isNumber = false;
                Advance(lexer);
            }
            else
            {
                if (lexer.Lookahead == '{')
                {
                    return ScanTail(Entry.BraceStart, lexer, validSymbols);
                }

                if (validSymbols[ExpansionWord])
                {
                    return ScanTail(Entry.ExpansionWord, lexer, validSymbols);
                }

                if (validSymbols[ExtglobPattern])
                {
                    return ScanTail(Entry.ExtglobPattern, lexer, validSymbols);
                }

                return false;
            }

            for (;;)
            {
                if (IsWDigit(lexer.Lookahead))
                {
                    Advance(lexer);
                }
                else if (IsWAlpha(lexer.Lookahead) || lexer.Lookahead == '_')
                {
                    isNumber = false;
                    Advance(lexer);
                }
                else
                {
                    break;
                }
            }

            if (isNumber && validSymbols[FileDescriptor] && (lexer.Lookahead == '>' || lexer.Lookahead == '<'))
            {
                lexer.ResultSymbol = FileDescriptor;
                return true;
            }

            if (validSymbols[VariableName])
            {
                if (lexer.Lookahead == '+')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '=' || lexer.Lookahead == ':' || validSymbols[ClosingBrace])
                    {
                        lexer.ResultSymbol = VariableName;
                        return true;
                    }

                    return false;
                }

                if (lexer.Lookahead == '/')
                {
                    return false;
                }

                // TODO(amaanq): more cases for regular word chars but not variable
                // names for function words, only handling : for now? #235
                if (lexer.Lookahead == '=' || lexer.Lookahead == '[' ||
                    (lexer.Lookahead == ':' && !validSymbols[ClosingBrace] && !validSymbols[OpeningParen]) ||
                    lexer.Lookahead == '%' ||
                    (lexer.Lookahead == '#' && !isNumber) || lexer.Lookahead == '@' ||
                    (lexer.Lookahead == '-' && validSymbols[ClosingBrace]))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = VariableName;
                    return true;
                }

                if (lexer.Lookahead == '?')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    lexer.ResultSymbol = VariableName;
                    return IsWAlpha(lexer.Lookahead);
                }
            }

            return false;
        }

        if (validSymbols[BareDollar] && !InErrorRecovery(validSymbols) && ScanBareDollar(lexer))
        {
            return true;
        }

        return ScanTail(Entry.Regex, lexer, validSymbols);
    }

    // The blocks after the label `regex:` of scan(), in their order. Entering at an entry
    // point runs the blocks from there on; a block whose condition is false falls through
    // to the next one, and after the last one the result is false.
    private bool ScanTail(Entry entry, Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (entry <= Entry.Regex)
        {
            bool? result = ScanRegexBlock(lexer, validSymbols);
            if (result.HasValue)
            {
                return result.Value;
            }
        }

        if (entry <= Entry.ExtglobPattern)
        {
            bool? result = ScanExtglobBlock(lexer, validSymbols);
            if (result.HasValue)
            {
                return result.Value;
            }
        }

        if (entry <= Entry.ExpansionWord)
        {
            bool? result = ScanExpansionWordBlock(lexer, validSymbols);
            if (result.HasValue)
            {
                return result.Value;
            }
        }

        if (entry <= Entry.BraceStart)
        {
            bool? result = ScanBraceStartBlock(lexer, validSymbols);
            if (result.HasValue)
            {
                return result.Value;
            }
        }

        return false;
    }

    // State of the regex block (the C struct State of the block).
    private sealed class RegexState
    {
        public bool Done;
        public bool AdvancedOnce;
        public bool FoundNonAlnumDollarUnderdash;
        public bool LastWasEscape;
        public bool InSingleQuote;
        public uint ParenDepth;
        public uint BracketDepth;
        public uint BraceDepth;
    }

    // The block `regex:`. Null when its condition is false (the block falls through).
    private static bool? ScanRegexBlock(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if ((validSymbols[Regex] || validSymbols[RegexNoSlash] || validSymbols[RegexNoSpace]) &&
            !InErrorRecovery(validSymbols))
        {
            if (validSymbols[Regex] || validSymbols[RegexNoSpace])
            {
                while (IsWSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }
            }

            if ((lexer.Lookahead != '"' && lexer.Lookahead != '\'') ||
                ((lexer.Lookahead == '$' || lexer.Lookahead == '\'') && validSymbols[RegexNoSlash]) ||
                (lexer.Lookahead == '\'' && validSymbols[RegexNoSpace]))
            {
                if (lexer.Lookahead == '$' && validSymbols[RegexNoSlash])
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '(')
                    {
                        return false;
                    }
                }

                lexer.MarkEnd();

                RegexState state = new RegexState();
                while (!state.Done)
                {
                    if (state.InSingleQuote)
                    {
                        if (lexer.Lookahead == '\'')
                        {
                            state.InSingleQuote = false;
                            Advance(lexer);
                            lexer.MarkEnd();
                        }
                    }

                    switch (lexer.Lookahead)
                    {
                        case '\\':
                            state.LastWasEscape = true;
                            break;
                        case '\0':
                            return false;
                        case '(':
                            state.ParenDepth++;
                            state.LastWasEscape = false;
                            break;
                        case '[':
                            state.BracketDepth++;
                            state.LastWasEscape = false;
                            break;
                        case '{':
                            if (!state.LastWasEscape)
                            {
                                state.BraceDepth++;
                            }

                            state.LastWasEscape = false;
                            break;
                        case ')':
                            if (state.ParenDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.ParenDepth--;
                            state.LastWasEscape = false;
                            break;
                        case ']':
                            if (state.BracketDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BracketDepth--;
                            state.LastWasEscape = false;
                            break;
                        case '}':
                            if (state.BraceDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BraceDepth--;
                            state.LastWasEscape = false;
                            break;
                        case '\'':
                            // Enter or exit a single-quoted string.
                            state.InSingleQuote = !state.InSingleQuote;
                            Advance(lexer);
                            state.AdvancedOnce = true;
                            state.LastWasEscape = false;
                            continue;
                        default:
                            state.LastWasEscape = false;
                            break;
                    }

                    if (!state.Done)
                    {
                        if (validSymbols[Regex])
                        {
                            bool wasSpace = !state.InSingleQuote && IsWSpace(lexer.Lookahead);
                            Advance(lexer);
                            state.AdvancedOnce = true;
                            if (!wasSpace || state.ParenDepth > 0)
                            {
                                lexer.MarkEnd();
                            }
                        }
                        else if (validSymbols[RegexNoSlash])
                        {
                            if (lexer.Lookahead == '/')
                            {
                                lexer.MarkEnd();
                                lexer.ResultSymbol = RegexNoSlash;
                                return state.AdvancedOnce;
                            }

                            if (lexer.Lookahead == '\\')
                            {
                                Advance(lexer);
                                state.AdvancedOnce = true;
                                if (!lexer.Eof() && lexer.Lookahead != '[' && lexer.Lookahead != '/')
                                {
                                    Advance(lexer);
                                    lexer.MarkEnd();
                                }
                            }
                            else
                            {
                                bool wasSpace = !state.InSingleQuote && IsWSpace(lexer.Lookahead);
                                Advance(lexer);
                                state.AdvancedOnce = true;
                                if (!wasSpace)
                                {
                                    lexer.MarkEnd();
                                }
                            }
                        }
                        else if (validSymbols[RegexNoSpace])
                        {
                            if (lexer.Lookahead == '\\')
                            {
                                state.FoundNonAlnumDollarUnderdash = true;
                                Advance(lexer);
                                if (!lexer.Eof())
                                {
                                    Advance(lexer);
                                }
                            }
                            else if (lexer.Lookahead == '$')
                            {
                                lexer.MarkEnd();
                                Advance(lexer);
                                // do not parse a command
                                // substitution
                                if (lexer.Lookahead == '(')
                                {
                                    return false;
                                }

                                // end $ always means regex, e.g.
                                // 99999999$
                                if (IsWSpace(lexer.Lookahead))
                                {
                                    lexer.ResultSymbol = RegexNoSpace;
                                    lexer.MarkEnd();
                                    return true;
                                }
                            }
                            else
                            {
                                bool wasSpace = !state.InSingleQuote && IsWSpace(lexer.Lookahead);
                                if (wasSpace && state.ParenDepth == 0)
                                {
                                    lexer.MarkEnd();
                                    lexer.ResultSymbol = RegexNoSpace;
                                    return state.FoundNonAlnumDollarUnderdash;
                                }

                                if (!IsWAlNum(lexer.Lookahead) && lexer.Lookahead != '$' && lexer.Lookahead != '-' &&
                                    lexer.Lookahead != '_')
                                {
                                    state.FoundNonAlnumDollarUnderdash = true;
                                }

                                Advance(lexer);
                            }
                        }
                    }
                }

                lexer.ResultSymbol = (ushort)(validSymbols[RegexNoSlash] ? RegexNoSlash
                    : validSymbols[RegexNoSpace] ? RegexNoSpace
                    : Regex);
                if (validSymbols[Regex] && !state.AdvancedOnce)
                {
                    return false;
                }

                return true;
            }
        }

        return null;
    }

    // State of the extglob block (the C struct State of the block).
    private sealed class ExtglobState
    {
        public bool Done;
        public bool SawNonAlphaDot;
        public uint ParenDepth;
        public uint BracketDepth;
        public uint BraceDepth;
    }

    // The block `extglob_pattern:`. Null when its condition is false (the block falls through).
    private bool? ScanExtglobBlock(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[ExtglobPattern] && !InErrorRecovery(validSymbols))
        {
            // first skip ws, then check for ? * + @ !
            while (IsWSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '?' || lexer.Lookahead == '*' || lexer.Lookahead == '+' || lexer.Lookahead == '@' ||
                lexer.Lookahead == '!' || lexer.Lookahead == '-' || lexer.Lookahead == ')' || lexer.Lookahead == '\\' ||
                lexer.Lookahead == '.' || lexer.Lookahead == '[' || IsWAlpha(lexer.Lookahead))
            {
                if (lexer.Lookahead == '\\')
                {
                    Advance(lexer);
                    if ((IsWSpace(lexer.Lookahead) || lexer.Lookahead == '"') && lexer.Lookahead != '\r' &&
                        lexer.Lookahead != '\n')
                    {
                        Advance(lexer);
                    }
                    else
                    {
                        return false;
                    }
                }

                if (lexer.Lookahead == ')' && _lastGlobParenDepth == 0)
                {
                    lexer.MarkEnd();
                    Advance(lexer);

                    if (IsWSpace(lexer.Lookahead))
                    {
                        return false;
                    }
                }

                lexer.MarkEnd();
                bool wasNonAlpha = !IsWAlpha(lexer.Lookahead);
                if (lexer.Lookahead != '[')
                {
                    // no esac
                    if (lexer.Lookahead == 'e')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == 's')
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == 'a')
                            {
                                Advance(lexer);
                                if (lexer.Lookahead == 'c')
                                {
                                    Advance(lexer);
                                    if (IsWSpace(lexer.Lookahead))
                                    {
                                        return false;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Advance(lexer);
                    }
                }

                // -\w is just a word, find something else special
                if (lexer.Lookahead == '-')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    while (IsWAlNum(lexer.Lookahead))
                    {
                        Advance(lexer);
                    }

                    if (lexer.Lookahead == ')' || lexer.Lookahead == '\\' || lexer.Lookahead == '.')
                    {
                        return false;
                    }

                    lexer.MarkEnd();
                }

                // case item -) or *)
                if (lexer.Lookahead == ')' && _lastGlobParenDepth == 0)
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (IsWSpace(lexer.Lookahead))
                    {
                        lexer.ResultSymbol = ExtglobPattern;
                        return wasNonAlpha;
                    }
                }

                if (IsWSpace(lexer.Lookahead))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = ExtglobPattern;
                    _lastGlobParenDepth = 0;
                    return true;
                }

                if (lexer.Lookahead == '$')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '{' || lexer.Lookahead == '(')
                    {
                        lexer.ResultSymbol = ExtglobPattern;
                        return true;
                    }
                }

                if (lexer.Lookahead == '|')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    lexer.ResultSymbol = ExtglobPattern;
                    return true;
                }

                if (!IsWAlNum(lexer.Lookahead) && lexer.Lookahead != '(' && lexer.Lookahead != '"' &&
                    lexer.Lookahead != '[' && lexer.Lookahead != '?' && lexer.Lookahead != '/' &&
                    lexer.Lookahead != '\\' && lexer.Lookahead != '_' && lexer.Lookahead != '*')
                {
                    return false;
                }

                ExtglobState state = new ExtglobState
                {
                    Done = false,
                    SawNonAlphaDot = wasNonAlpha,
                    ParenDepth = _lastGlobParenDepth,
                    BracketDepth = 0,
                    BraceDepth = 0,
                };
                while (!state.Done)
                {
                    switch (lexer.Lookahead)
                    {
                        case '\0':
                            return false;
                        case '(':
                            state.ParenDepth++;
                            break;
                        case '[':
                            state.BracketDepth++;
                            break;
                        case '{':
                            state.BraceDepth++;
                            break;
                        case ')':
                            if (state.ParenDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.ParenDepth--;
                            break;
                        case ']':
                            if (state.BracketDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BracketDepth--;
                            break;
                        case '}':
                            if (state.BraceDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BraceDepth--;
                            break;
                    }

                    if (lexer.Lookahead == '|')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
                        {
                            lexer.ResultSymbol = ExtglobPattern;
                            return true;
                        }
                    }

                    if (!state.Done)
                    {
                        bool wasSpace = IsWSpace(lexer.Lookahead);
                        if (lexer.Lookahead == '$')
                        {
                            lexer.MarkEnd();
                            if (!IsWAlpha(lexer.Lookahead) && lexer.Lookahead != '.' && lexer.Lookahead != '\\')
                            {
                                state.SawNonAlphaDot = true;
                            }

                            Advance(lexer);
                            if (lexer.Lookahead == '(' || lexer.Lookahead == '{')
                            {
                                lexer.ResultSymbol = ExtglobPattern;
                                _lastGlobParenDepth = (byte)state.ParenDepth;
                                return state.SawNonAlphaDot;
                            }
                        }

                        if (wasSpace)
                        {
                            lexer.MarkEnd();
                            lexer.ResultSymbol = ExtglobPattern;
                            _lastGlobParenDepth = 0;
                            return state.SawNonAlphaDot;
                        }

                        if (lexer.Lookahead == '"')
                        {
                            lexer.MarkEnd();
                            lexer.ResultSymbol = ExtglobPattern;
                            _lastGlobParenDepth = 0;
                            return state.SawNonAlphaDot;
                        }

                        if (lexer.Lookahead == '\\')
                        {
                            if (!IsWAlpha(lexer.Lookahead) && lexer.Lookahead != '.' && lexer.Lookahead != '\\')
                            {
                                state.SawNonAlphaDot = true;
                            }

                            Advance(lexer);
                            if (IsWSpace(lexer.Lookahead) || lexer.Lookahead == '"')
                            {
                                Advance(lexer);
                            }
                        }
                        else
                        {
                            if (!IsWAlpha(lexer.Lookahead) && lexer.Lookahead != '.' && lexer.Lookahead != '\\')
                            {
                                state.SawNonAlphaDot = true;
                            }

                            Advance(lexer);
                        }

                        if (!wasSpace)
                        {
                            lexer.MarkEnd();
                        }
                    }
                }

                lexer.ResultSymbol = ExtglobPattern;
                _lastGlobParenDepth = 0;
                return state.SawNonAlphaDot;
            }

            _lastGlobParenDepth = 0;

            return false;
        }

        return null;
    }

    // The block `expansion_word:`. Null when its condition is false (the block falls through).
    private static bool? ScanExpansionWordBlock(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[ExpansionWord])
        {
            bool advancedOnce = false;
            bool advanceOnceSpace = false;
            for (;;)
            {
                if (lexer.Lookahead == '"')
                {
                    return false;
                }

                if (lexer.Lookahead == '$')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '{' || lexer.Lookahead == '(' || lexer.Lookahead == '\'' ||
                        IsWAlNum(lexer.Lookahead))
                    {
                        lexer.ResultSymbol = ExpansionWord;
                        return advancedOnce;
                    }

                    advancedOnce = true;
                }

                if (lexer.Lookahead == '}')
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = ExpansionWord;
                    return advancedOnce || advanceOnceSpace;
                }

                if (lexer.Lookahead == '(' && !(advancedOnce || advanceOnceSpace))
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    while (lexer.Lookahead != ')' && !lexer.Eof())
                    {
                        // if we find a $( or ${ assume this is valid and is
                        // a garbage concatenation of some weird word + an
                        // expansion
                        // I wonder where this can fail
                        if (lexer.Lookahead == '$')
                        {
                            lexer.MarkEnd();
                            Advance(lexer);
                            if (lexer.Lookahead == '{' || lexer.Lookahead == '(' || lexer.Lookahead == '\'' ||
                                IsWAlNum(lexer.Lookahead))
                            {
                                lexer.ResultSymbol = ExpansionWord;
                                return advancedOnce;
                            }

                            advancedOnce = true;
                        }
                        else
                        {
                            advancedOnce = advancedOnce || !IsWSpace(lexer.Lookahead);
                            advanceOnceSpace = advanceOnceSpace || IsWSpace(lexer.Lookahead);
                            Advance(lexer);
                        }
                    }

                    lexer.MarkEnd();
                    if (lexer.Lookahead == ')')
                    {
                        advancedOnce = true;
                        Advance(lexer);
                        lexer.MarkEnd();
                        if (lexer.Lookahead == '}')
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                if (lexer.Lookahead == '\'')
                {
                    return false;
                }

                if (lexer.Eof())
                {
                    return false;
                }

                advancedOnce = advancedOnce || !IsWSpace(lexer.Lookahead);
                advanceOnceSpace = advanceOnceSpace || IsWSpace(lexer.Lookahead);
                Advance(lexer);
            }
        }

        return null;
    }

    // The block `brace_start:`. Null when its condition is false (the block falls through).
    private static bool? ScanBraceStartBlock(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[BraceStart] && !InErrorRecovery(validSymbols))
        {
            while (IsWSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead != '{')
            {
                return false;
            }

            Advance(lexer);
            lexer.MarkEnd();

            while (IsWDigit(lexer.Lookahead))
            {
                Advance(lexer);
            }

            if (lexer.Lookahead != '.')
            {
                return false;
            }

            Advance(lexer);

            if (lexer.Lookahead != '.')
            {
                return false;
            }

            Advance(lexer);

            while (IsWDigit(lexer.Lookahead))
            {
                Advance(lexer);
            }

            if (lexer.Lookahead != '}')
            {
                return false;
            }

            lexer.ResultSymbol = BraceStart;
            return true;
        }

        return null;
    }
}
