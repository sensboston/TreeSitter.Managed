using Xunit;

namespace TreeSitter.Tests.Runtime;

// Hand-checked expectations for the decoders. The differential tests against the native
// runtime are in UnicodeOracleTests. Consumed counts on errors follow the U8_NEXT and
// U16_NEXT macros (utf8.h, utf16.h) and are not always 1.
public class UnicodeTests
{
    private static (int CodePoint, uint Consumed) Utf8(params byte[] input)
    {
        uint consumed = Unicode.DecodeUtf8(input, out int codePoint);
        return (codePoint, consumed);
    }

    private static (int CodePoint, uint Consumed) Utf16Le(params byte[] input)
    {
        uint consumed = Unicode.DecodeUtf16Le(input, out int codePoint);
        return (codePoint, consumed);
    }

    private static (int CodePoint, uint Consumed) Utf16Be(params byte[] input)
    {
        uint consumed = Unicode.DecodeUtf16Be(input, out int codePoint);
        return (codePoint, consumed);
    }

    [Fact]
    public void Utf8DecodesOneToFourByteSequences()
    {
        Assert.Equal((0x41, 1u), Utf8(0x41));
        Assert.Equal((0x20AC, 3u), Utf8(0xE2, 0x82, 0xAC));
        Assert.Equal((0x1F600, 4u), Utf8(0xF0, 0x9F, 0x98, 0x80));
        Assert.Equal((0x10FFFF, 4u), Utf8(0xF4, 0x8F, 0xBF, 0xBF));
    }

    [Fact]
    public void Utf8RejectsOverlongSurrogateAndTooLargeSequences()
    {
        Assert.Equal((-1, 1u), Utf8(0xC0, 0x80));
        Assert.Equal((-1, 1u), Utf8(0xC1, 0xBF));
        Assert.Equal((-1, 1u), Utf8(0xE0, 0x80, 0x80));
        Assert.Equal((-1, 1u), Utf8(0xF0, 0x80, 0x80, 0x80));
        Assert.Equal((-1, 1u), Utf8(0xED, 0xA0, 0x80));
        Assert.Equal((-1, 1u), Utf8(0xF4, 0x90, 0x80, 0x80));
        Assert.Equal((-1, 1u), Utf8(0xF5, 0x80, 0x80, 0x80));
        Assert.Equal((-1, 1u), Utf8(0xFF, 0xBF, 0xBF, 0xBF));
    }

    [Fact]
    public void Utf8RejectsStrayTrailBytes()
    {
        Assert.Equal((-1, 1u), Utf8(0x80));
        Assert.Equal((-1, 1u), Utf8(0xBF, 0x41));
    }

    [Fact]
    public void Utf8TruncatedSequenceConsumesTheBytesItHasChecked()
    {
        // The macro leaves the index after the bytes it accepted before the input ended.
        Assert.Equal((-1, 2u), Utf8(0xE2, 0x82));
        Assert.Equal((-1, 3u), Utf8(0xF0, 0x9F, 0x98));
        Assert.Equal((-1, 1u), Utf8(0xE2));
    }

    [Fact]
    public void Utf8InterruptedSequenceStopsAtTheBadByte()
    {
        Assert.Equal((-1, 1u), Utf8(0xE2, 0x41));
        Assert.Equal((-1, 2u), Utf8(0xE2, 0x82, 0x41));
        Assert.Equal((-1, 3u), Utf8(0xF0, 0x9F, 0x98, 0x41));
    }

    [Fact]
    public void Utf8EmptyInputIsAnErrorWithNothingConsumed()
    {
        Assert.Equal((-1, 0u), Utf8());
    }

    [Fact]
    public void Utf16LeDecodesUnitsAndSurrogatePairs()
    {
        Assert.Equal((0x41, 2u), Utf16Le(0x41, 0x00));
        Assert.Equal((0x1F600, 4u), Utf16Le(0x3D, 0xD8, 0x00, 0xDE));
    }

    [Fact]
    public void Utf16BeDecodesUnitsAndSurrogatePairs()
    {
        Assert.Equal((0x41, 2u), Utf16Be(0x00, 0x41));
        Assert.Equal((0x1F600, 4u), Utf16Be(0xD8, 0x3D, 0xDE, 0x00));
    }

    [Fact]
    public void Utf16ReturnsLoneSurrogatesAsCodePoints()
    {
        // ICU returns a lone surrogate as the code point itself, not as an error.
        Assert.Equal((0xD800, 2u), Utf16Le(0x00, 0xD8));
        Assert.Equal((0xDC00, 2u), Utf16Le(0x00, 0xDC));
        Assert.Equal((0xD800, 2u), Utf16Be(0xD8, 0x00));
        Assert.Equal((0xD83D, 2u), Utf16Le(0x3D, 0xD8, 0x41, 0x00));
    }

    [Fact]
    public void Utf16ShorterThanTwoBytesIsAnError()
    {
        Assert.Equal((-1, 1u), Utf16Le(0x41));
        Assert.Equal((-1, 0u), Utf16Le());
        Assert.Equal((-1, 1u), Utf16Be(0x41));
        Assert.Equal((-1, 0u), Utf16Be());
    }

    [Fact]
    public void Utf16OddLengthLeavesTheLastByteUnconsumed()
    {
        Assert.Equal((0x41, 2u), Utf16Le(0x41, 0x00, 0x41));
        Assert.Equal((0x41, 2u), Utf16Be(0x00, 0x41, 0x00));
    }
}
