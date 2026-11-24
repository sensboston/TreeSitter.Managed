namespace TreeSitter.Generator.CSource;

using System.Text;

/// <summary>
/// Tokenizes a C source file (specifically tree-sitter generated parser.c files).
/// Handles comments, string/char literals with C escapes, integer literals,
/// identifiers, punctuators, and preprocessor directives.
/// </summary>
public sealed class CTokenizer
{
    private static readonly string[] MultiCharacterPunctuators =
    [
        "%:%:",
        "<<=",
        ">>=",
        "...",
        "->",
        "++",
        "--",
        "<<",
        ">>",
        "<=",
        ">=",
        "==",
        "!=",
        "&&",
        "||",
        "*=",
        "/=",
        "%=",
        "+=",
        "-=",
        "&=",
        "^=",
        "|=",
        "##",
        "<:",
        ":>",
        "<%",
        "%>",
        "%:",
    ];

    private readonly string _text;
    private readonly int _length;
    private int _pos;
    private int _line;
    private int _column;
    private bool _atStartOfLine;
    private List<CToken>? _cachedTokens;

    public CTokenizer(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fileName);
        FileName = fileName;
        _text = text;
        _length = text.Length;
        _line = 1;
        _column = 1;
        _atStartOfLine = true;
    }

    /// <summary>File name used in error messages (CSourceException).</summary>
    public string FileName { get; }

    /// <summary>Returns all tokens followed by exactly one EOF token.</summary>
    public List<CToken> Tokenize()
    {
        if (_cachedTokens is not null)
        {
            return new List<CToken>(_cachedTokens);
        }

        var tokens = new List<CToken>();
        while (_pos < _length)
        {
            char c = _text[_pos];

            if (IsWhitespace(c))
            {
                Advance(c);
                continue;
            }

            if (_atStartOfLine && c == '#')
            {
                tokens.Add(ReadPreprocessorDirective());
                continue;
            }

            if (c == '/' && _pos + 1 < _length)
            {
                if (_text[_pos + 1] == '/')
                {
                    SkipLineComment();
                    continue;
                }

                if (_text[_pos + 1] == '*')
                {
                    SkipBlockComment();
                    continue;
                }
            }

            _atStartOfLine = false;

            if (c == '"')
            {
                tokens.Add(ReadQuotedLiteral('"', CTokenKind.StringLiteral));
                continue;
            }

            if (c == '\'')
            {
                tokens.Add(ReadQuotedLiteral('\'', CTokenKind.CharLiteral));
                continue;
            }

            if (IsAsciiDigit(c))
            {
                tokens.Add(ReadNumberLiteral());
                continue;
            }

            if (IsIdentifierStart(c))
            {
                tokens.Add(ReadIdentifier());
                continue;
            }

            tokens.Add(ReadPunctuator());
        }

        tokens.Add(new CToken(CTokenKind.EOF, string.Empty, _line, _column));
        _cachedTokens = tokens;
        return new List<CToken>(tokens);
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\v' or '\f' or '\r' or '\n';

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static bool IsHexDigit(char c) =>
        IsAsciiDigit(c) || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F';

    private static bool IsIdentifierStart(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || IsAsciiDigit(c);

    private static bool IsLineBreak(char c) => c is '\r' or '\n';

    private void Advance(char c)
    {
        _pos++;
        if (c == '\n' || (c == '\r' && (_pos >= _length || _text[_pos] != '\n')))
        {
            _line++;
            _column = 1;
            _atStartOfLine = true;
        }
        else
        {
            _column++;
        }
    }

    private void AdvanceLineBreak()
    {
        char first = _text[_pos];
        Advance(first);
        if (first == '\r' && _pos < _length && _text[_pos] == '\n')
        {
            Advance('\n');
        }
    }

    private int StartLine => _line;

    private int StartColumn => _column;

    private void SkipLineComment()
    {
        while (_pos < _length && !IsLineBreak(_text[_pos]))
        {
            Advance(_text[_pos]);
        }
    }

    private void SkipBlockComment()
    {
        int startLine = StartLine;
        int startColumn = StartColumn;
        Advance('/');
        Advance('*');

        while (_pos < _length)
        {
            if (_text[_pos] == '*' && _pos + 1 < _length && _text[_pos + 1] == '/')
            {
                Advance('*');
                Advance('/');
                return;
            }

            Advance(_text[_pos]);
        }

        throw Error(new SourcePos(startLine, startColumn), "Unterminated block comment.");
    }

    private CToken ReadPreprocessorDirective()
    {
        int startLine = StartLine;
        int startColumn = StartColumn;
        var sb = new StringBuilder();

        while (_pos < _length)
        {
            char c = _text[_pos];
            if (c == '\\' && _pos + 1 < _length && IsLineBreak(_text[_pos + 1]))
            {
                sb.Append(c);
                Advance(c);
                char lineBreak = _text[_pos];
                sb.Append(lineBreak);
                Advance(lineBreak);
                if (lineBreak == '\r' && _pos < _length && _text[_pos] == '\n')
                {
                    sb.Append('\n');
                    Advance('\n');
                }

                continue;
            }

            if (IsLineBreak(c))
            {
                AdvanceLineBreak();
                break;
            }

            sb.Append(c);
            Advance(c);
        }

        return new CToken(CTokenKind.PreprocessorDirective, sb.ToString(), startLine, startColumn);
    }

    private CToken ReadQuotedLiteral(char quote, CTokenKind kind)
    {
        int startLine = StartLine;
        int startColumn = StartColumn;
        var sb = new StringBuilder();
        sb.Append(quote);
        Advance(quote);

        while (_pos < _length)
        {
            char c = _text[_pos];
            if (c == quote)
            {
                sb.Append(c);
                Advance(c);
                return new CToken(kind, sb.ToString(), startLine, startColumn);
            }

            if (IsLineBreak(c))
            {
                string literalName = kind == CTokenKind.StringLiteral ? "string" : "character";
                throw Error(new SourcePos(startLine, startColumn), $"Unterminated {literalName} literal.");
            }

            sb.Append(c);
            Advance(c);
            if (c == '\\')
            {
                if (_pos >= _length || IsLineBreak(_text[_pos]))
                {
                    string literalName = kind == CTokenKind.StringLiteral ? "string" : "character";
                    throw Error(new SourcePos(startLine, startColumn), $"Unterminated {literalName} literal.");
                }

                sb.Append(_text[_pos]);
                Advance(_text[_pos]);
            }
        }

        string unterminatedName = kind == CTokenKind.StringLiteral ? "string" : "character";
        throw Error(new SourcePos(startLine, startColumn), $"Unterminated {unterminatedName} literal.");
    }

    private CToken ReadNumberLiteral()
    {
        int startLine = StartLine;
        int startColumn = StartColumn;
        var sb = new StringBuilder();

        if (_text[_pos] == '0' && _pos + 1 < _length && _text[_pos + 1] is 'x' or 'X')
        {
            sb.Append(_text[_pos]);
            Advance(_text[_pos]);
            sb.Append(_text[_pos]);
            Advance(_text[_pos]);
            while (_pos < _length && IsHexDigit(_text[_pos]))
            {
                sb.Append(_text[_pos]);
                Advance(_text[_pos]);
            }
        }
        else
        {
            while (_pos < _length && IsAsciiDigit(_text[_pos]))
            {
                sb.Append(_text[_pos]);
                Advance(_text[_pos]);
            }
        }

        while (_pos < _length && _text[_pos] is 'u' or 'U' or 'l' or 'L')
        {
            sb.Append(_text[_pos]);
            Advance(_text[_pos]);
        }

        return new CToken(CTokenKind.NumberLiteral, sb.ToString(), startLine, startColumn);
    }

    private CToken ReadIdentifier()
    {
        int startLine = StartLine;
        int startColumn = StartColumn;
        var sb = new StringBuilder();

        while (_pos < _length && IsIdentifierPart(_text[_pos]))
        {
            sb.Append(_text[_pos]);
            Advance(_text[_pos]);
        }

        return new CToken(CTokenKind.Identifier, sb.ToString(), startLine, startColumn);
    }

    private CToken ReadPunctuator()
    {
        int startLine = StartLine;
        int startColumn = StartColumn;

        foreach (string punctuator in MultiCharacterPunctuators)
        {
            if (_pos + punctuator.Length <= _length &&
                string.CompareOrdinal(_text, _pos, punctuator, 0, punctuator.Length) == 0)
            {
                foreach (char c in punctuator)
                {
                    Advance(c);
                }

                return new CToken(CTokenKind.Punctuator, punctuator, startLine, startColumn);
            }
        }

        char ch = _text[_pos];
        if (!IsSingleCharacterPunctuator(ch))
        {
            throw Error(new SourcePos(startLine, startColumn), $"Unexpected character '{ch}'.");
        }

        Advance(ch);
        return new CToken(CTokenKind.Punctuator, ch.ToString(), startLine, startColumn);
    }

    private static bool IsSingleCharacterPunctuator(char c) => c is
        '[' or ']' or '(' or ')' or '{' or '}' or '.' or '&' or '*' or '+' or '-' or '~' or '!' or '/' or '%' or '<' or '>' or '^' or '|' or '?' or ':' or ';' or '=' or ',' or '#';

    private CSourceException Error(SourcePos pos, string message) => new(FileName, pos, message);

    /// <summary>
    /// Parses the integer value of a number literal token, stripping suffixes.
    /// </summary>
    public static long ParseInteger(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int end = text.Length;
        while (end > 0 && text[end - 1] is 'u' or 'U' or 'l' or 'L')
        {
            end--;
        }

        string digits = text[..end];
        if (digits.Length >= 2 && digits[0] == '0' && digits[1] is 'x' or 'X')
        {
            if (digits.Length == 2)
            {
                throw new FormatException("Hexadecimal integer literal must contain at least one digit.");
            }

            return Convert.ToInt64(digits[2..], 16);
        }

        return Convert.ToInt64(digits, 10);
    }

    /// <summary>
    /// Unescapes a C string literal (including surrounding quotes) and returns the content.
    /// </summary>
    public static string UnescapeString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < 2)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        int end = text.Length - 1;
        int index = 1;
        while (index < end)
        {
            char c = text[index];
            if (c != '\\' || index + 1 >= end)
            {
                sb.Append(c);
                index++;
                continue;
            }

            index++;
            int value = DecodeEscape(text, ref index, end);
            AppendCodePoint(sb, value);
            index++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Unescapes a C char literal (including surrounding quotes) and returns its integer value.
    /// </summary>
    public static int ParseCharLiteral(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < 2)
        {
            return 0;
        }

        string inner = text[1..^1];
        if (inner.Length == 0)
        {
            return 0;
        }

        if (inner[0] != '\\')
        {
            return inner[0];
        }

        if (inner.Length == 1)
        {
            return '\\';
        }

        int index = 1;
        return DecodeEscape(inner, ref index, inner.Length);
    }

    private static int DecodeEscape(string text, ref int index, int endExclusive)
    {
        char escape = text[index];
        switch (escape)
        {
            case 'a': return '\a';
            case 'b': return '\b';
            case 'f': return '\f';
            case 'n': return '\n';
            case 'r': return '\r';
            case 't': return '\t';
            case 'v': return '\v';
            case '\\': return '\\';
            case '\'': return '\'';
            case '"': return '"';
            case '?': return '?';
            case >= '0' and <= '7':
            {
                uint value = 0;
                int count = 0;
                while (index < endExclusive && count < 3 && text[index] is >= '0' and <= '7')
                {
                    value = (value * 8) + (uint)(text[index] - '0');
                    index++;
                    count++;
                }

                index--;
                return unchecked((int)value);
            }
            case 'x':
            {
                int firstDigit = index + 1;
                int end = firstDigit;
                while (end < endExclusive && IsHexDigit(text[end]))
                {
                    end++;
                }

                if (end == firstDigit)
                {
                    return 'x';
                }

                uint value = ParseRadix(text, firstDigit, end, 16);
                index = end - 1;
                return unchecked((int)value);
            }
            case 'u':
                return DecodeFixedHexEscape(text, ref index, endExclusive, 4, escape);
            case 'U':
                return DecodeFixedHexEscape(text, ref index, endExclusive, 8, escape);
            default:
                return escape;
        }
    }

    private static int DecodeFixedHexEscape(string text, ref int index, int endExclusive, int digitCount, char fallback)
    {
        int firstDigit = index + 1;
        int end = firstDigit;
        while (end < endExclusive && end - firstDigit < digitCount && IsHexDigit(text[end]))
        {
            end++;
        }

        if (end - firstDigit != digitCount)
        {
            return fallback;
        }

        uint value = ParseRadix(text, firstDigit, end, 16);
        index = end - 1;
        return unchecked((int)value);
    }

    private static uint ParseRadix(string text, int start, int end, uint radix)
    {
        uint value = 0;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            uint digit = c is >= '0' and <= '9'
                ? (uint)(c - '0')
                : c is >= 'a' and <= 'f'
                    ? (uint)(c - 'a' + 10)
                    : (uint)(c - 'A' + 10);
            value = unchecked((value * radix) + digit);
        }

        return value;
    }

    private static void AppendCodePoint(StringBuilder sb, int value)
    {
        if (value is >= 0 and <= 0xFFFF)
        {
            sb.Append((char)value);
        }
        else if (value is > 0xFFFF and <= 0x10FFFF)
        {
            sb.Append(char.ConvertFromUtf32(value));
        }
        else
        {
            sb.Append(unchecked((char)value));
        }
    }
}
