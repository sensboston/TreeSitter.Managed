// Input types of the public API (api.h: TSInputEncoding, TSDecodeFunction, TSInput, TSRange,
// TSLogType, TSLogger).

namespace TreeSitter;

/// <summary>TSInputEncoding</summary>
public enum InputEncoding
{
    /// <summary>UTF-8 bytes.</summary>
    Utf8,

    /// <summary>UTF-16 little-endian bytes.</summary>
    Utf16LE,

    /// <summary>UTF-16 big-endian bytes.</summary>
    Utf16BE,

    /// <summary>Bytes decoded by <see cref="IParseInput.Decode"/>.</summary>
    Custom,
}

/// <summary>
/// TSDecodeFunction: decodes one code point from the start of <paramref name="input"/>,
/// returns the number of bytes used, and writes -1 to <paramref name="codePoint"/> for invalid input.
/// </summary>
public delegate uint DecodeFunction(ReadOnlySpan<byte> input, out int codePoint);

/// <summary>TSInput: source text read in chunks.</summary>
public interface IParseInput
{
    /// <summary>The encoding of the bytes returned by <see cref="Read"/>.</summary>
    InputEncoding Encoding { get; }

    /// <summary>Used only when <see cref="Encoding"/> is <see cref="InputEncoding.Custom"/>.</summary>
    DecodeFunction? Decode { get; }

    /// <summary>
    /// Returns the bytes starting at <paramref name="byteIndex"/> (also given as
    /// <paramref name="position"/>); an empty result means the end of the input. A returned
    /// chunk must stay unchanged until the next call.
    /// </summary>
    ReadOnlyMemory<byte> Read(uint byteIndex, Point position);
}

/// <summary>TSRange: a range of the source, in bytes and points.</summary>
public readonly record struct Range(Point StartPoint, Point EndPoint, uint StartByte, uint EndByte);

/// <summary>TSLogType</summary>
public enum LogType
{
    /// <summary>Messages of the parser.</summary>
    Parse,

    /// <summary>Messages of the lexer.</summary>
    Lex,
}

/// <summary>TSLogger: receives debug messages with the same text as the C runtime.</summary>
public delegate void Logger(LogType type, string message);

/// <summary>Built-in inputs over memory.</summary>
public static class ParseInputs
{
    /// <summary>
    /// Input over a byte buffer. <paramref name="chunkSize"/> limits the size of each chunk
    /// (0 = the whole rest of the buffer); tests use small chunks to check chunk borders.
    /// </summary>
    public static IParseInput FromBytes(
        ReadOnlyMemory<byte> bytes,
        InputEncoding encoding,
        uint chunkSize = 0,
        DecodeFunction? decode = null) =>
        new BytesInput(bytes, encoding, chunkSize, decode);

    /// <summary>
    /// Input over a .NET string as UTF-16LE. Chunks of at most <paramref name="chunkSize"/> bytes
    /// (default 4096, rounded down to an even number) are copied into a reused buffer.
    /// </summary>
    public static IParseInput FromString(string text, uint chunkSize = 4096) =>
        new StringInput(text, chunkSize);

    /// <summary>The input of ParseInputs.FromBytes: the chunks are slices of the buffer.</summary>
    private sealed class BytesInput : IParseInput
    {
        private readonly ReadOnlyMemory<byte> _bytes;
        private readonly uint _chunkSize;

        internal BytesInput(ReadOnlyMemory<byte> bytes, InputEncoding encoding, uint chunkSize, DecodeFunction? decode)
        {
            if (encoding == InputEncoding.Custom && decode is null)
            {
                throw new ArgumentNullException(nameof(decode), "A custom encoding needs a decode function.");
            }

            _bytes = bytes;
            _chunkSize = chunkSize;
            Encoding = encoding;
            Decode = decode;
        }

        public InputEncoding Encoding { get; }

        public DecodeFunction? Decode { get; }

        public ReadOnlyMemory<byte> Read(uint byteIndex, Point position)
        {
            _ = position;
            if (byteIndex >= _bytes.Length)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            uint remaining = (uint)_bytes.Length - byteIndex;
            uint size = _chunkSize == 0 || _chunkSize > remaining ? remaining : _chunkSize;
            return _bytes.Slice((int)byteIndex, (int)size);
        }
    }

    /// <summary>
    /// The input of ParseInputs.FromString: the UTF-16LE bytes of the string, copied chunk by
    /// chunk into a buffer that the next call reuses (as the TSInput contract allows).
    /// </summary>
    private sealed class StringInput : IParseInput
    {
        private readonly string _text;
        private readonly uint _chunkSize;
        private byte[] _buffer = [];

        internal StringInput(string text, uint chunkSize)
        {
            ArgumentNullException.ThrowIfNull(text);
            _text = text;
            _chunkSize = chunkSize;
        }

        public InputEncoding Encoding => InputEncoding.Utf16LE;

        public DecodeFunction? Decode => null;

        public ReadOnlyMemory<byte> Read(uint byteIndex, Point position)
        {
            _ = position;
            uint total = (uint)_text.Length * 2;
            if (byteIndex >= total)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            // Chunks start on a code unit boundary and hold whole code units: the size is
            // rounded down to an even number, with a minimum of one code unit.
            uint limit = _chunkSize == 0 ? uint.MaxValue : Math.Max(2u, _chunkSize & ~1u);
            uint remaining = total - byteIndex;
            uint size = remaining < limit ? remaining : limit;
            if (size > _buffer.Length)
            {
                _buffer = new byte[size];
            }

            for (uint i = 0; i < size; i++)
            {
                uint offset = byteIndex + i;
                char value = _text[(int)(offset >> 1)];
                _buffer[i] = (offset & 1) == 0 ? (byte)(value & 0xFF) : (byte)(value >> 8);
            }

            return _buffer.AsMemory(0, (int)size);
        }
    }
}
