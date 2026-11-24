using TreeSitter;
using Xunit;

namespace TreeSitter.Tests.Lexing;

// Unit tests for the parts of the lexer and of the input types that the scripts cannot reach:
// the logger (LOG and ts_lexer__log) and the built-in inputs of ParseInputs.
public class LexerInputTests
{
    private static IParseInput Input(string text) => ParseInputs.FromBytes(System.Text.Encoding.UTF8.GetBytes(text), InputEncoding.Utf8);

    private static Lexer StartedLexer(IParseInput input, Logger logger)
    {
        Lexer lexer = new Lexer { Logger = logger };
        lexer.SetInput(input);
        lexer.Start();
        return lexer;
    }

    [Fact]
    public void AdvanceLogsTheConsumedCharacter()
    {
        List<string> messages = new List<string>();
        Lexer lexer = StartedLexer(Input("a\n\u00e9x"), (type, message) =>
        {
            Assert.Equal(LogType.Lex, type);
            messages.Add(message);
        });

        lexer.Advance(false);
        lexer.Advance(true);
        lexer.Advance(false);
        lexer.Advance(false);

        Assert.Equal(new[] { "consume character:'a'", "skip character:10", "consume character:233", "consume character:'x'" }, messages);
    }

    [Fact]
    public void AdvanceWithoutALoggerDoesNotFail()
    {
        Lexer lexer = StartedLexer(Input("ab"), (_, _) => { });
        lexer.Logger = null;
        lexer.Advance(false);
        Assert.Equal('b', lexer.Lookahead);
    }

    [Fact]
    public void LogCutsTheMessageToTheDebugBuffer()
    {
        List<string> messages = new List<string>();
        Lexer lexer = StartedLexer(Input("a"), (_, message) => messages.Add(message));

        lexer.Log(new string('x', 2_000));
        Assert.Equal(1_023, messages[0].Length);

        // A multi-byte character is never cut in the middle: 511 of the 600 two byte
        // characters fit into the 1023 bytes of the buffer.
        lexer.Log(new string('\u00e9', 600));
        Assert.Equal(511, messages[1].Length);
        Assert.All(messages[1], c => Assert.Equal('\u00e9', c));

        lexer.Log(string.Empty);
        Assert.Equal(string.Empty, messages[2]);
    }

    [Fact]
    public void LogRejectsAMissingMessage()
    {
        Lexer lexer = StartedLexer(Input("a"), (_, _) => { });
        Assert.Throws<ArgumentNullException>(() => lexer.Log(null!));
    }

    [Fact]
    public void SetInputRejectsAMissingInput()
    {
        Lexer lexer = new Lexer();
        Assert.Throws<ArgumentNullException>(() => lexer.SetInput(null!));
    }

    [Fact]
    public void ResultSymbolIsStored()
    {
        Lexer lexer = StartedLexer(Input("ab"), (_, _) => { });
        lexer.ResultSymbol = 42;
        Assert.Equal(42, lexer.ResultSymbol);
        lexer.Start();
        Assert.Equal(0, lexer.ResultSymbol);
    }

    [Fact]
    public void MarkEndWithoutAChunkUsesTheCurrentPosition()
    {
        Lexer lexer = new Lexer();
        lexer.MarkEnd();
        Assert.Equal(Length.Zero.Bytes, lexer.TokenEndPosition.Bytes);
    }

    [Fact]
    public void BytesInputFollowsTheChunkContract()
    {
        IParseInput input = ParseInputs.FromBytes(new byte[] { 1, 2, 3, 4, 5 }, InputEncoding.Utf8, chunkSize: 2);
        Assert.Equal(InputEncoding.Utf8, input.Encoding);
        Assert.Null(input.Decode);
        Assert.Equal(new byte[] { 1, 2 }, input.Read(0, default).ToArray());
        Assert.Equal(new byte[] { 3, 4 }, input.Read(2, default).ToArray());
        Assert.Equal(new byte[] { 5 }, input.Read(4, default).ToArray());
        Assert.True(input.Read(5, default).IsEmpty);
        Assert.True(input.Read(uint.MaxValue, default).IsEmpty);
        Assert.True(ParseInputs.FromBytes(Array.Empty<byte>(), InputEncoding.Utf8).Read(0, default).IsEmpty);
    }

    [Fact]
    public void BytesInputWithoutAChunkLimitReturnsTheRest()
    {
        IParseInput input = ParseInputs.FromBytes(new byte[] { 1, 2, 3 }, InputEncoding.Utf16LE);
        Assert.Equal(new byte[] { 1, 2, 3 }, input.Read(0, default).ToArray());
        Assert.Equal(new byte[] { 3 }, input.Read(2, default).ToArray());
        Assert.Equal(InputEncoding.Utf16LE, input.Encoding);
    }

    [Fact]
    public void BytesInputSupportsACustomDecoder()
    {
        DecodeFunction decode = (ReadOnlySpan<byte> span, out int codePoint) =>
        {
            codePoint = span[0] == 0 ? -1 : 'A' + span[0];
            return 1;
        };
        IParseInput input = ParseInputs.FromBytes(new byte[] { 1, 0, 2 }, InputEncoding.Custom, chunkSize: 1, decode);
        Assert.Equal(InputEncoding.Custom, input.Encoding);
        Assert.Same(decode, input.Decode);

        Lexer lexer = StartedLexer(input, (_, _) => { });
        Assert.Equal('B', lexer.Lookahead);
        lexer.Advance(false);
        Assert.Equal(-1, lexer.Lookahead);
        lexer.Advance(false);
        Assert.Equal('C', lexer.Lookahead);
    }

    [Fact]
    public void CustomEncodingWithoutADecoderIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ParseInputs.FromBytes(new byte[] { 1 }, InputEncoding.Custom));
    }

    [Fact]
    public void StringInputIsUtf16LittleEndian()
    {
        IParseInput input = ParseInputs.FromString("A\u00e9", chunkSize: 4);
        Assert.Equal(InputEncoding.Utf16LE, input.Encoding);
        Assert.Null(input.Decode);
        Assert.Equal(new byte[] { 0x41, 0, 0xe9, 0 }, input.Read(0, default).ToArray());
        Assert.Equal(new byte[] { 0xe9, 0 }, input.Read(2, default).ToArray());
        Assert.True(input.Read(4, default).IsEmpty);
    }

    [Fact]
    public void StringInputRoundsTheChunkSizeAndReusesTheBuffer()
    {
        IParseInput input = ParseInputs.FromString("abcdef", chunkSize: 5);
        Assert.Equal(new byte[] { 0x61, 0, 0x62, 0 }, input.Read(0, default).ToArray());
        Assert.Equal(new byte[] { 0x63, 0, 0x64, 0 }, input.Read(4, default).ToArray());
        Assert.Equal(new byte[] { 0x65, 0, 0x66, 0 }, input.Read(8, default).ToArray());
        Assert.True(input.Read(12, default).IsEmpty);
        Assert.True(input.Read(uint.MaxValue, default).IsEmpty);
    }

    [Fact]
    public void StringInputWithoutAChunkLimitReturnsTheRest()
    {
        IParseInput input = ParseInputs.FromString("ab", chunkSize: 0);
        Assert.Equal(new byte[] { 0x61, 0, 0x62, 0 }, input.Read(0, default).ToArray());
    }

    [Fact]
    public void StringInputCanStartAtAnOddByte()
    {
        // The contract of TSInput allows any byte index; an odd one starts on the high byte.
        IParseInput input = ParseInputs.FromString("ab", chunkSize: 2);
        Assert.Equal(new byte[] { 0, 0x62 }, input.Read(1, default).ToArray());
    }

    [Fact]
    public void StringInputRejectsAMissingString()
    {
        Assert.Throws<ArgumentNullException>(() => ParseInputs.FromString(null!));
    }

    [Fact]
    public void StringInputsParseAsUtf16()
    {
        List<string> messages = new List<string>();
        Lexer lexer = StartedLexer(ParseInputs.FromString("a\n\u00e9"), (_, message) => messages.Add(message));
        Assert.Equal('a', lexer.Lookahead);
        lexer.Advance(false);
        Assert.Equal(2u, lexer.CurrentPosition.Bytes);
        Assert.Equal(0u, lexer.CurrentPosition.Extent.Row);
        Assert.Equal('\n', lexer.Lookahead);
        lexer.Advance(false);
        Assert.Equal(1u, lexer.CurrentPosition.Extent.Row);
        Assert.Equal(0u, lexer.CurrentPosition.Extent.Column);
        Assert.Equal('\u00e9', lexer.Lookahead);
        lexer.Advance(false);
        Assert.Equal(6u, lexer.CurrentPosition.Bytes);
        Assert.Equal(2u, lexer.CurrentPosition.Extent.Column);
        Assert.True(lexer.Eof());
        Assert.Equal(new[] { "consume character:'a'", "consume character:10", "consume character:233" }, messages);
    }
}
