// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/lexer.c and lib/src/lexer.h.
//
// One class plays both roles of C: the TSLexer seen by lex functions and external
// scanners (public members) and the Lexer used by the parser (internal members).
// Rules:
// - Same fields and same logic as lexer.c, including the column cache (ColumnData), the
//   chunk handling (a chunk is ReadOnlyMemory<byte> from IParseInput.Read), the decode
//   retry at chunk ends, the byte order mark rule, TS_DECODE_ERROR (-1) and included ranges.
// - Decoding uses Unicode.DecodeUtf8 / DecodeUtf16Le / DecodeUtf16Be or IParseInput.Decode.
// - Log messages have the same text as C (ts_lexer__log formats with vsnprintf; callers in
//   C# pass the finished text). The debug buffer limit of 1024 bytes
//   (TREE_SITTER_SERIALIZATION_BUFFER_SIZE) applies: longer messages are cut like vsnprintf.
// - No allocations in Advance, MarkEnd, GetColumn, Eof.

using System.ComponentModel;
using System.Globalization;

namespace TreeSitter;

/// <summary>
/// The lexer seen by generated lex functions and external scanners (TSLexer). Valid only
/// during a call from the parser.
/// </summary>
public sealed class Lexer
{
    /// <summary>TREE_SITTER_SERIALIZATION_BUFFER_SIZE (parser.h): the size of the log buffer.</summary>
    private const int SerializationBufferSize = 1024;

    /// <summary>BYTE_ORDER_MARK (lexer.c).</summary>
    private const int ByteOrderMark = 0xFEFF;

    /// <summary>DEFAULT_RANGE (lexer.c): the range used when none is set.</summary>
    private static readonly Range DefaultRange =
        new(new Point(0, 0), new Point(uint.MaxValue, uint.MaxValue), 0, uint.MaxValue);

    private IParseInput? _input;
    private Range[] _includedRanges;
    private uint _includedRangeCount;
    private uint _currentIncludedRangeIndex;
    private ReadOnlyMemory<byte> _chunk;
    private bool _hasChunk;
    private uint _chunkStart;
    private uint _chunkSize;
    private uint _lookaheadSize;
    private int _lookahead;
    private ushort _resultSymbol;
    private Length _currentPosition;
    private Length _tokenStartPosition;
    private Length _tokenEndPosition;
    private bool _didGetColumn;
    private bool _columnValid;
    private uint _columnValue;

    /// <summary>ts_lexer_init (and the ts_lexer_set_included_ranges of its tail).</summary>
    internal Lexer()
    {
        _includedRanges = [];
        _includedRangeCount = 0;
        _currentPosition = Length.Zero;
        _tokenStartPosition = Length.Zero;
        _tokenEndPosition = Length.Zero;
        SetIncludedRanges(ReadOnlySpan<Range>.Empty);
    }

    // ------------------------------------------------------------ TSLexer (public)

    /// <summary>TSLexer.lookahead: the current code point, 0 at the end, -1 for invalid input.</summary>
    public int Lookahead => _lookahead;

    /// <summary>TSLexer.result_symbol</summary>
    public ushort ResultSymbol
    {
        get => _resultSymbol;
        set => _resultSymbol = value;
    }

    /// <summary>TSLexer.advance (ts_lexer__advance)</summary>
    public void Advance(bool skip)
    {
        if (!_hasChunk)
        {
            return;
        }

        LogCharacter(skip ? "skip" : "consume");

        uint nextPosition = _currentPosition.Bytes + 1;
        uint currentRangeEnd = _currentIncludedRangeIndex < _includedRangeCount
            ? _includedRanges[_currentIncludedRangeIndex].EndByte
            : 0;
        if (_input!.Encoding == InputEncoding.Utf8 &&
            _lookaheadSize == 1 &&
            _lookahead != '\n' &&
            nextPosition < currentRangeEnd &&
            nextPosition < _chunkStart + _chunkSize)
        {
            byte nextByte = _chunk.Span[(int)(nextPosition - _chunkStart)];
            if (nextByte < 0x80)
            {
                IncrementColumnData();
                _currentPosition = new Length(
                    _currentPosition.Bytes + 1,
                    new Point(_currentPosition.Extent.Row, _currentPosition.Extent.Column + 1));
                if (skip)
                {
                    _tokenStartPosition = _currentPosition;
                }

                _lookahead = nextByte;
                return;
            }
        }

        DoAdvance(skip);
    }

    /// <summary>TSLexer.mark_end (ts_lexer__mark_end)</summary>
    public void MarkEnd() => MarkEndInternal();

    /// <summary>TSLexer.get_column (ts_lexer__get_column)</summary>
    public uint GetColumn()
    {
        _didGetColumn = true;

        if (!_columnValid)
        {
            // Record current position
            uint goalByte = _currentPosition.Bytes;

            // Back up to the beginning of the line
            Length startOfColumn = new(
                _currentPosition.Bytes - _currentPosition.Extent.Column,
                new Point(_currentPosition.Extent.Row, 0));
            Goto(startOfColumn);
            SetColumnData(0);
            GetChunk();

            if (!Eof())
            {
                GetLookahead();

                // Advance to the recorded position
                while (_currentPosition.Bytes < goalByte && !Eof() && _hasChunk)
                {
                    DoAdvance(false);
                    if (Eof())
                    {
                        break;
                    }
                }
            }
        }

        return _columnValue;
    }

    /// <summary>TSLexer.is_at_included_range_start (ts_lexer__is_at_included_range_start)</summary>
    public bool IsAtIncludedRangeStart()
    {
        if (_currentIncludedRangeIndex < _includedRangeCount)
        {
            Range currentRange = _includedRanges[_currentIncludedRangeIndex];
            return _currentPosition.Bytes == currentRange.StartByte;
        }

        return false;
    }

    /// <summary>TSLexer.eof (ts_lexer__eof)</summary>
    public bool Eof() => _currentIncludedRangeIndex == _includedRangeCount;

    /// <summary>TSLexer.log (ts_lexer__log)</summary>
    /// <param name="message">The finished text of the message.</param>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public void Log(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogText(message);
    }

    // ------------------------------------------------------------ Lexer (internal, lexer.h)

    internal Length CurrentPosition => _currentPosition;

    internal Length TokenStartPosition => _tokenStartPosition;

    internal Length TokenEndPosition => _tokenEndPosition;

    /// <summary>Lexer.lookahead_size</summary>
    internal uint LookaheadSize => _lookaheadSize;

    /// <summary>Lexer.current_included_range_index</summary>
    internal uint CurrentIncludedRangeIndex => _currentIncludedRangeIndex;

    /// <summary>
    /// Lexer.did_get_column: the parser reads it after a lex function call to know whether
    /// the token depends on the column (parser.c).
    /// </summary>
    internal bool DidGetColumn => _didGetColumn;

    internal Logger? Logger { get; set; }

    /// <summary>
    /// Lexer.column_data: the parser saves it before the external scanner runs and puts it
    /// back when the scanner finds no token (parser.c, ts_parser__lex).
    /// </summary>
    internal (bool Valid, uint Value) ColumnData
    {
        get => (_columnValid, _columnValue);
        set
        {
            _columnValid = value.Valid;
            _columnValue = value.Value;
        }
    }

    /// <summary>ts_lexer_set_input</summary>
    internal void SetInput(IParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        ClearChunk();
        Goto(_currentPosition);
    }

    /// <summary>ts_lexer_reset</summary>
    internal void Reset(Length position)
    {
        if (position.Bytes != _currentPosition.Bytes)
        {
            Goto(position);
        }
    }

    /// <summary>ts_lexer_start</summary>
    internal void Start()
    {
        _tokenStartPosition = _currentPosition;
        _tokenEndPosition = Length.Undefined;
        _resultSymbol = 0;
        _didGetColumn = false;
        if (!Eof())
        {
            if (_chunkSize == 0)
            {
                GetChunk();
            }

            if (_lookaheadSize == 0)
            {
                GetLookahead();
            }

            if (_currentPosition.Bytes == 0)
            {
                if (_lookahead == ByteOrderMark)
                {
                    Advance(true);
                }

                SetColumnData(0);
            }
        }
    }

    /// <summary>ts_lexer_finish</summary>
    internal void Finish(ref uint lookaheadEndByte)
    {
        if (_tokenEndPosition.IsUndefined)
        {
            MarkEndInternal();
        }

        // If the token ended at an included range boundary, then its end position
        // will have been reset to the end of the preceding range. Reset the start
        // position to match.
        if (_tokenEndPosition.Bytes < _tokenStartPosition.Bytes)
        {
            _tokenStartPosition = _tokenEndPosition;
        }

        uint currentLookaheadEndByte = _currentPosition.Bytes + 1;

        // In order to determine that a byte sequence is invalid UTF8 or UTF16,
        // the character decoding algorithm may have looked at the following byte.
        // Therefore, the next byte *after* the current (invalid) character
        // affects the interpretation of the current character.
        if (_lookahead == Unicode.DecodeError)
        {
            currentLookaheadEndByte += 4; // the maximum number of bytes read to identify an invalid code point
        }

        if (currentLookaheadEndByte > lookaheadEndByte)
        {
            lookaheadEndByte = currentLookaheadEndByte;
        }
    }

    /// <summary>ts_lexer_mark_end</summary>
    internal void MarkEndInternal()
    {
        if (!Eof())
        {
            // If the lexer is right at the beginning of included range,
            // then the token should be considered to end at the *end* of the
            // previous included range, rather than here.
            Range currentIncludedRange = _includedRanges[_currentIncludedRangeIndex];
            if (_currentIncludedRangeIndex > 0 &&
                _currentPosition.Bytes == currentIncludedRange.StartByte)
            {
                Range previousIncludedRange = _includedRanges[_currentIncludedRangeIndex - 1];
                _tokenEndPosition = new Length(previousIncludedRange.EndByte, previousIncludedRange.EndPoint);
                return;
            }
        }

        _tokenEndPosition = _currentPosition;
    }

    /// <summary>ts_lexer_set_included_ranges; false when the ranges are not ordered.</summary>
    internal bool SetIncludedRanges(ReadOnlySpan<Range> ranges)
    {
        if (ranges.Length == 0)
        {
            _includedRanges = [DefaultRange];
            _includedRangeCount = 1;
        }
        else
        {
            uint previousByte = 0;
            for (int i = 0; i < ranges.Length; i++)
            {
                Range range = ranges[i];
                if (range.StartByte < previousByte || range.EndByte < range.StartByte)
                {
                    return false;
                }

                previousByte = range.EndByte;
            }

            _includedRanges = ranges.ToArray();
            _includedRangeCount = (uint)ranges.Length;
        }

        Goto(_currentPosition);
        return true;
    }

    /// <summary>ts_lexer_included_ranges</summary>
    internal ReadOnlySpan<Range> IncludedRanges => _includedRanges.AsSpan(0, (int)_includedRangeCount);

    // ts_lexer_delete: the included ranges are owned by the lexer. The garbage collector frees
    // them; the method exists so that the port has the function of lexer.c and a caller can
    // release the ranges early.
    /// <summary>ts_lexer_delete</summary>
    internal void Delete()
    {
        _includedRanges = [];
        _includedRangeCount = 0;
        _currentIncludedRangeIndex = 0;
        ClearChunk();
    }

    // ------------------------------------------------------------ lexer.c internals

    /// <summary>ts_lexer__set_column_data</summary>
    private void SetColumnData(uint value)
    {
        _columnValid = true;
        _columnValue = value;
    }

    /// <summary>ts_lexer__increment_column_data</summary>
    private void IncrementColumnData()
    {
        if (_columnValid)
        {
            _columnValue++;
        }
    }

    /// <summary>ts_lexer__invalidate_column_data</summary>
    private void InvalidateColumnData()
    {
        _columnValid = false;
        _columnValue = 0;
    }

    /// <summary>ts_lexer__clear_chunk</summary>
    private void ClearChunk()
    {
        _chunk = default;
        _hasChunk = false;
        _chunkSize = 0;
        _chunkStart = 0;
    }

    // Call the lexer's input callback to obtain a new chunk of source code
    // for the current position.
    /// <summary>ts_lexer__get_chunk</summary>
    private void GetChunk()
    {
        _chunkStart = _currentPosition.Bytes;
        ReadOnlyMemory<byte> chunk = _input!.Read(_currentPosition.Bytes, _currentPosition.Extent);
        _chunkSize = (uint)chunk.Length;
        if (_chunkSize == 0)
        {
            _currentIncludedRangeIndex = _includedRangeCount;
            _chunk = default;
            _hasChunk = false;
            return;
        }

        _chunk = chunk;
        _hasChunk = true;
    }

    // Decode the next unicode character in the current chunk of source code.
    // This assumes that the lexer has already retrieved a chunk of source
    // code that spans the current position.
    /// <summary>ts_lexer__get_lookahead</summary>
    private void GetLookahead()
    {
        uint positionInChunk = _currentPosition.Bytes - _chunkStart;
        uint size = _chunkSize - positionInChunk;

        if (size == 0)
        {
            _lookaheadSize = 1;
            _lookahead = 0;
            return;
        }

        ReadOnlySpan<byte> chunk = _chunk.Span.Slice((int)positionInChunk);

        if (_input!.Encoding == InputEncoding.Utf8 && chunk[0] < 0x80)
        {
            _lookahead = chunk[0];
            _lookaheadSize = 1;
            return;
        }

        DecodeFunction decode = DecodeFunctionFor(_input);
        _lookaheadSize = decode(chunk, out _lookahead);

        // If this chunk ended in the middle of a multi-byte character,
        // try again with a fresh chunk.
        if (_lookahead == Unicode.DecodeError && size < 4)
        {
            GetChunk();
            chunk = _chunk.Span;
            _lookaheadSize = decode(chunk, out _lookahead);
        }

        if (_lookahead == Unicode.DecodeError)
        {
            _lookaheadSize = 1;
        }
    }

    /// <summary>ts_lexer_goto</summary>
    private void Goto(Length position)
    {
        if (position.Bytes != _currentPosition.Bytes)
        {
            InvalidateColumnData();
        }

        _currentPosition = position;

        // Move to the first valid position at or after the given position.
        bool foundIncludedRange = false;
        for (uint i = 0; i < _includedRangeCount; i++)
        {
            Range includedRange = _includedRanges[i];
            if (includedRange.EndByte > _currentPosition.Bytes &&
                includedRange.EndByte > includedRange.StartByte)
            {
                if (includedRange.StartByte >= _currentPosition.Bytes)
                {
                    _currentPosition = new Length(includedRange.StartByte, includedRange.StartPoint);
                }

                _currentIncludedRangeIndex = i;
                foundIncludedRange = true;
                break;
            }
        }

        if (foundIncludedRange)
        {
            // If the current position is outside of the current chunk of text,
            // then clear out the current chunk of text.
            if (_currentPosition.Bytes < _chunkStart ||
                _currentPosition.Bytes >= _chunkStart + _chunkSize)
            {
                ClearChunk();
            }

            _lookaheadSize = 0;
            _lookahead = 0;
        }
        else
        {
            // If the given position is beyond any of included ranges, move to the EOF
            // state - past the end of the included ranges.
            _currentIncludedRangeIndex = _includedRangeCount;
            if (_includedRangeCount > 0)
            {
                // C reads included_ranges[count - 1] without a check. There is always a range
                // after ts_lexer_init and ts_lexer_set_included_ranges; only a lexer that was
                // deleted has none, and then the position is left as it is.
                Range lastIncludedRange = _includedRanges[(int)_includedRangeCount - 1];
                _currentPosition = new Length(lastIncludedRange.EndByte, lastIncludedRange.EndPoint);
            }

            ClearChunk();
            _lookaheadSize = 1;
            _lookahead = 0;
        }
    }

    /// <summary>ts_lexer__do_advance</summary>
    private void DoAdvance(bool skip)
    {
        if (_lookaheadSize != 0)
        {
            if (_lookahead == '\n')
            {
                _currentPosition = new Length(
                    _currentPosition.Bytes,
                    new Point(_currentPosition.Extent.Row + 1, 0));
                SetColumnData(0);
            }
            else
            {
                bool isBom = _currentPosition.Bytes == 0 && _lookahead == ByteOrderMark;
                if (!isBom)
                {
                    IncrementColumnData();
                }

                _currentPosition = new Length(
                    _currentPosition.Bytes,
                    new Point(
                        _currentPosition.Extent.Row,
                        _currentPosition.Extent.Column + _lookaheadSize));
            }

            _currentPosition = new Length(_currentPosition.Bytes + _lookaheadSize, _currentPosition.Extent);
        }

        // C reads self->included_ranges[current_included_range_index] without a bounds check: the
        // index must be below the count when the loop starts. At the end of the input (index ==
        // count) the loop body would index past the array, which the managed port cannot do, so
        // the loop is skipped there. The state of both is the same.
        bool hasRange = _currentIncludedRangeIndex < _includedRangeCount;
        while (hasRange && IsCurrentRangeExhausted())
        {
            _currentIncludedRangeIndex++;
            if (_currentIncludedRangeIndex < _includedRangeCount)
            {
                Range currentRange = _includedRanges[_currentIncludedRangeIndex];
                _currentPosition = new Length(currentRange.StartByte, currentRange.StartPoint);
            }
            else
            {
                hasRange = false;
            }
        }

        if (skip)
        {
            _tokenStartPosition = _currentPosition;
        }

        if (hasRange)
        {
            if (_currentPosition.Bytes < _chunkStart ||
                _currentPosition.Bytes >= _chunkStart + _chunkSize)
            {
                GetChunk();
            }

            GetLookahead();
        }
        else
        {
            ClearChunk();
            _lookahead = 0;
            _lookaheadSize = 1;
        }
    }

    /// <summary>
    /// The loop condition of ts_lexer__do_advance: the current position reached the end of
    /// its included range, or the range is empty.
    /// </summary>
    private bool IsCurrentRangeExhausted()
    {
        Range currentRange = _includedRanges[_currentIncludedRangeIndex];
        return _currentPosition.Bytes >= currentRange.EndByte ||
            currentRange.EndByte == currentRange.StartByte;
    }

    /// <summary>The decode function of the input (the ternary chain of ts_lexer__get_lookahead).</summary>
    private static DecodeFunction DecodeFunctionFor(IParseInput input)
    {
        return input.Encoding switch
        {
            InputEncoding.Utf8 => Unicode.DecodeUtf8,
            InputEncoding.Utf16LE => Unicode.DecodeUtf16Le,
            InputEncoding.Utf16BE => Unicode.DecodeUtf16Be,
            _ => input.Decode ??
                throw new InvalidOperationException("The input of a custom encoding has no decode function."),
        };
    }

    // ------------------------------------------------------------ log (LOG macro, ts_lexer__log)

    /// <summary>The LOG macro of lexer.c: the message and the lookahead character.</summary>
    private void LogCharacter(string message)
    {
        if (Logger is null)
        {
            return;
        }

        string text = _lookahead >= 32 && _lookahead < 127
            ? string.Concat(message, " character:'", ((char)_lookahead).ToString(CultureInfo.InvariantCulture), "'")
            : string.Concat(message, " character:", _lookahead.ToString(CultureInfo.InvariantCulture));
        LogText(text);
    }

    /// <summary>ts_lexer__log: the logger gets the text, cut to the debug buffer size.</summary>
    private void LogText(string message) => Logger?.Invoke(LogType.Lex, TruncateToDebugBuffer(message));

    /// <summary>
    /// vsnprintf writes at most <see cref="SerializationBufferSize"/> - 1 bytes into
    /// debug_buffer, so a longer message is cut. The cut never splits a character.
    /// </summary>
    internal static string TruncateToDebugBuffer(string message)
    {
        int bytes = 0;
        int index = 0;
        while (index < message.Length)
        {
            int size = Utf8Size(message[index]);
            if (bytes + size > SerializationBufferSize - 1)
            {
                return message[..index];
            }

            bytes += size;
            index++;
        }

        return message;
    }

    /// <summary>The number of bytes of the UTF-8 encoding of one UTF-16 code unit.</summary>
    private static int Utf8Size(char value) => value switch
    {
        < (char)0x80 => 1,
        < (char)0x800 => 2,
        _ => 3,
    };
}
