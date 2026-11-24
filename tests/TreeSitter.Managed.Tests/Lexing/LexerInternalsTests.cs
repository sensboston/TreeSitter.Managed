using TreeSitter;
using Xunit;

namespace TreeSitter.Tests.Lexing;

// Unit tests for the parts of the lexer that the scripts of the oracle do not cover: the
// column cache after a new chunk, the public API of a lex function, the log of the parser
// level and ts_lexer_delete.
public class LexerInternalsTests
{
    [Fact]
    public void DeleteResetsTheLexerToTheInitialState()
    {
        Lexer lexer = new Lexer();
        lexer.SetInput(ParseInputs.FromString("ab", chunkSize: 2));
        lexer.Start();
        lexer.Advance(false);
        lexer.Reset(new Length(1, new Point(0, 1)));
        Assert.Equal(1, lexer.IncludedRanges.Length);
        lexer.Delete();

        // ts_lexer_delete frees the ranges, so nothing is left. It is the last call of a lexer
        // in C too: every position is past the end of the empty range list.
        Assert.True(lexer.IncludedRanges.IsEmpty);
        Assert.True(lexer.Eof());
        lexer.Reset(new Length(0, new Point(0, 0)));
        Assert.True(lexer.Eof());
        Assert.Equal(0u, lexer.CurrentPosition.Bytes);
    }

    [Fact]
    public void ColumnIsRebuiltAfterTheChunkIsReleased()
    {
        // A reset to the same position keeps the chunk; a reset back to the start of the line
        // of the column rebuild reads a new chunk, so the whole line is scanned again.
        Lexer lexer = new Lexer();
        lexer.SetInput(ParseInputs.FromBytes(new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d' }, InputEncoding.Utf8, chunkSize: 2));
        lexer.Start();
        Assert.False(lexer.DidGetColumn);
        lexer.Advance(false);
        lexer.Advance(false);
        lexer.Advance(false);
        Assert.Equal(3u, lexer.GetColumn());
        Assert.True(lexer.DidGetColumn);
        Assert.Equal(3u, lexer.GetColumn());

        lexer.Start();
        Assert.False(lexer.DidGetColumn);

        // A reset to another byte invalidates the cache; a reset back to the start of the line
        // of the column rebuild reads the chunk again, so the line is scanned again.
        lexer.Reset(new Length(2, new Point(0, 2)));
        Assert.Equal(2u, lexer.GetColumn());

        lexer.Reset(new Length(0, new Point(0, 0)));
        Assert.Equal(0u, lexer.GetColumn());

        lexer.Reset(new Length(4, new Point(0, 4)));
        Assert.Equal(4u, lexer.GetColumn());
    }

    [Fact]
    public void LookaheadOfACustomDecoderIsUsed()
    {
        DecodeFunction decode = (ReadOnlySpan<byte> span, out int codePoint) =>
        {
            codePoint = 0x1F600;
            return (uint)Math.Min(4, span.Length);
        };
        IParseInput input = ParseInputs.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, InputEncoding.Custom, chunkSize: 4, decode);

        Lexer lexer = new Lexer();
        lexer.SetInput(input);
        lexer.Start();
        Assert.Equal(0x1F600, lexer.Lookahead);
        Assert.Equal(4u, lexer.LookaheadSize);
        lexer.Advance(false);
        Assert.Equal(4u, lexer.CurrentPosition.Bytes);
        Assert.Equal(4u, lexer.CurrentPosition.Extent.Column);
        lexer.Advance(false);
        Assert.Equal(8u, lexer.CurrentPosition.Bytes);
        Assert.True(lexer.Eof());
    }

    [Fact]
    public void PublicMembersReadTheStateOfTheLexer()
    {
        Lexer lexer = new Lexer();
        lexer.SetInput(ParseInputs.FromString("a b"));
        lexer.Start();
        Assert.False(lexer.Eof());
        Assert.True(lexer.IsAtIncludedRangeStart());
        Assert.Equal(0u, lexer.GetColumn());

        lexer.ResultSymbol = 7;
        lexer.MarkEnd();
        Assert.Equal(0u, lexer.TokenEndPosition.Bytes);
        lexer.Advance(false);
        lexer.MarkEnd();
        Assert.Equal(2u, lexer.TokenEndPosition.Bytes);
        Assert.Equal(0u, lexer.TokenStartPosition.Bytes);

        // ts_lexer_finish marks the end when the token has none, and adds four bytes to the
        // end of an invalid lookahead.
        Lexer unfinished = new Lexer();
        unfinished.SetInput(ParseInputs.FromBytes(new byte[] { 0x41, 0xFF }, InputEncoding.Utf8));
        unfinished.Start();
        unfinished.Advance(false);
        uint lookaheadEndByte = 0;
        unfinished.Finish(ref lookaheadEndByte);
        Assert.Equal(6u, lookaheadEndByte);
        Assert.Equal(1u, unfinished.TokenEndPosition.Bytes);
        Assert.Equal(0u, unfinished.TokenStartPosition.Bytes);
    }

    [Fact]
    public void FinishKeepsTheStartOfATokenThatEndsBeforeIt()
    {
        // The token ends at the end of the first included range, so its start moves back.
        Lexer lexer = new Lexer();
        lexer.SetInput(ParseInputs.FromBytes(new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d' }, InputEncoding.Utf8));
        lexer.SetIncludedRanges(
        [
            new Range(new Point(0, 0), new Point(0, 1), 0, 1),
            new Range(new Point(0, 2), new Point(0, 4), 2, 4),
        ]);
        lexer.Reset(new Length(2, new Point(0, 2)));
        lexer.Start();
        lexer.Advance(false);
        lexer.Advance(false);
        lexer.MarkEndInternal();
        uint lookaheadEndByte = 0;
        lexer.Finish(ref lookaheadEndByte);
        Assert.Equal(4u, lexer.TokenEndPosition.Bytes);
        Assert.Equal(5u, lookaheadEndByte);
        Assert.Equal(2u, lexer.TokenStartPosition.Bytes);
        Assert.True(lexer.Eof());

        // A mark_end at the start of the second range ends the token at the first range.
        lexer.Reset(new Length(2, new Point(0, 2)));
        lexer.Start();
        lexer.MarkEndInternal();
        Assert.Equal(1u, lexer.TokenEndPosition.Bytes);
    }

    [Fact]
    public void UnorderedRangesAreRejectedAndKeepTheOldOnes()
    {
        Lexer lexer = new Lexer();
        Assert.False(lexer.SetIncludedRanges(
        [
            new Range(new Point(0, 2), new Point(0, 4), 2, 4),
            new Range(new Point(0, 0), new Point(0, 1), 0, 1),
        ]));
        Assert.Equal(1, lexer.IncludedRanges.Length);

        // An end before the start is rejected too.
        Assert.False(lexer.SetIncludedRanges([new Range(new Point(0, 0), new Point(0, 0), 5, 4)]));
        Assert.True(lexer.SetIncludedRanges(ReadOnlySpan<Range>.Empty));
        Assert.Equal(uint.MaxValue, lexer.IncludedRanges[0].EndByte);
    }
}
