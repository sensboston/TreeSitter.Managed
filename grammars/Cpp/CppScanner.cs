// Port of src/scanner.c of tree-sitter-cpp (commit c009222808634c1014f82438d4883753516a2c24,
// grammars/Cpp/upstream/src/scanner.c), the external scanner of the C++ grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - The delimiter is an array of wchar_t: int (32 bits, the size of wchar_t on Linux, where
//   the oracle runs). The serialized state is the same bytes as the C scanner writes there:
//   each delimiter character as 4 bytes, little endian.
// - iswspace is the ASCII one of the C locale (see the oracle_wctype.h of the tests).

namespace TreeSitter.Grammars.Cpp;

/// <summary>The external scanner of the C++ grammar (raw string literals).</summary>
internal sealed class CppScanner : IExternalScanner
{
    // enum TokenType
    private const int RawStringDelimiter = 0;
    private const int RawStringContent = 1;

    /// <summary>The spec limits delimiters to 16 chars</summary>
    private const int MaxDelimiterLength = 16;

    // sizeof(wchar_t) of the C scanner: 2 bytes on Windows (UTF-16), 4 bytes on the other
    // platforms (UTF-32). The serialized delimiter has this width (tests/Oracle runs on both).
    private static readonly int WCharSize = OperatingSystem.IsWindows() ? 2 : 4;

    // Scanner: delimiter_length and delimiter (tree_sitter_cpp_external_scanner_create
    // returns a zeroed Scanner, which is the initial state of these fields).
    private readonly int[] _delimiter = new int[MaxDelimiterLength];
    private int _delimiterLength;

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    // iswspace of the C locale.
    private static bool IsWSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private void Reset()
    {
        _delimiterLength = 0;
        Array.Clear(_delimiter);
    }

    /// <summary>The value stored in a wchar_t: truncated to 16 bits where wchar_t is 16 bits.</summary>
    private static int WChar(int c) => WCharSize == 2 ? (ushort)c : c;

    /// <summary>Scan the raw string delimiter in R"delimiter(content)delimiter"</summary>
    private bool ScanRawStringDelimiter(Lexer lexer)
    {
        if (_delimiterLength > 0)
        {
            // Closing delimiter: must exactly match the opening delimiter.
            // We already checked this when scanning content, but this is how we
            // know when to stop. We can't stop at ", because R"""hello""" is valid.
            for (int i = 0; i < _delimiterLength; ++i)
            {
                if (lexer.Lookahead != _delimiter[i])
                {
                    return false;
                }

                Advance(lexer);
            }

            Reset();
            return true;
        }

        // Opening delimiter: record the d-char-sequence up to (.
        // d-char is any basic character except parens, backslashes, and spaces.
        for (;;)
        {
            if (_delimiterLength >= MaxDelimiterLength || lexer.Eof() || lexer.Lookahead == '\\' ||
                IsWSpace(lexer.Lookahead))
            {
                return false;
            }

            if (lexer.Lookahead == '(')
            {
                // Rather than create a token for an empty delimiter, we fail and
                // let the grammar fall back to a delimiter-less rule.
                return _delimiterLength > 0;
            }

            _delimiter[_delimiterLength++] = WChar(lexer.Lookahead);
            Advance(lexer);
        }
    }

    /// <summary>Scan the raw string content in R"delimiter(content)delimiter"</summary>
    private bool ScanRawStringContent(Lexer lexer)
    {
        // The progress made through the delimiter since the last ')'.
        // The delimiter may not contain ')' so a single counter suffices.
        for (int delimiterIndex = -1; ;)
        {
            // If we hit EOF, consider the content to terminate there.
            // This forms an incomplete raw_string_literal, and models the code
            // well.
            if (lexer.Eof())
            {
                lexer.MarkEnd();
                return true;
            }

            if (delimiterIndex >= 0)
            {
                if (delimiterIndex == _delimiterLength)
                {
                    if (lexer.Lookahead == '"')
                    {
                        return true;
                    }

                    delimiterIndex = -1;
                }
                else
                {
                    if (lexer.Lookahead == _delimiter[delimiterIndex])
                    {
                        delimiterIndex += 1;
                    }
                    else
                    {
                        delimiterIndex = -1;
                    }
                }
            }

            if (delimiterIndex == -1 && lexer.Lookahead == ')')
            {
                // The content doesn't include the )delimiter" part.
                // We must still scan through it, but exclude it from the token.
                lexer.MarkEnd();
                delimiterIndex = 0;
            }

            Advance(lexer);
        }
    }

    /// <summary>tree_sitter_cpp_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[RawStringDelimiter] && validSymbols[RawStringContent])
        {
            // we're in error recovery
            return false;
        }

        // No skipping leading whitespace: raw-string grammar is space-sensitive.
        if (validSymbols[RawStringDelimiter])
        {
            lexer.ResultSymbol = RawStringDelimiter;
            return ScanRawStringDelimiter(lexer);
        }

        if (validSymbols[RawStringContent])
        {
            lexer.ResultSymbol = RawStringContent;
            return ScanRawStringContent(lexer);
        }

        return false;
    }

    /// <summary>tree_sitter_cpp_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        // static_assert(MAX_DELIMITER_LENGTH * sizeof(wchar_t) < TREE_SITTER_SERIALIZATION_BUFFER_SIZE)
        int size = _delimiterLength * WCharSize;
        for (int i = 0; i < _delimiterLength; i++)
        {
            int value = _delimiter[i];
            int offset = i * WCharSize;
            for (int b = 0; b < WCharSize; b++)
            {
                buffer[offset + b] = (byte)(value >> (8 * b));
            }
        }

        return (uint)size;
    }

    /// <summary>tree_sitter_cpp_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        // assert(length % sizeof(wchar_t) == 0 && "Can't decode serialized delimiter!")
        System.Diagnostics.Debug.Assert(buffer.Length % WCharSize == 0, "Can't decode serialized delimiter!");

        _delimiterLength = buffer.Length / WCharSize;
        for (int i = 0; i < _delimiterLength; i++)
        {
            int offset = i * WCharSize;
            int value = 0;
            for (int b = 0; b < WCharSize; b++)
            {
                value |= buffer[offset + b] << (8 * b);
            }

            _delimiter[i] = value;
        }
    }
}
