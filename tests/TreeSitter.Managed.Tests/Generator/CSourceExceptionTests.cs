using TreeSitter.Generator.CSource;
using Xunit;

namespace TreeSitter.Tests.Generator;

public class CSourceExceptionTests
{
    [Fact]
    public void ConstructorsPreserveLocationAndMessageMetadata()
    {
        var position = new SourcePos(2, 7);
        var located = new CSourceException("parser.c", position, "unexpected token");
        Assert.Equal("parser.c", located.FileName);
        Assert.Equal(position, located.Pos);
        Assert.Equal("unexpected token", located.Text);
        Assert.Equal("parser.c:2:7: unexpected token", located.Message);

        var defaultException = new CSourceException();
        Assert.Equal(string.Empty, defaultException.FileName);
        Assert.Equal(new SourcePos(0, 0), defaultException.Pos);
        Assert.Equal(string.Empty, defaultException.Text);

        var messageException = new CSourceException("plain message");
        Assert.Equal(string.Empty, messageException.FileName);
        Assert.Equal(new SourcePos(0, 0), messageException.Pos);
        Assert.Equal("plain message", messageException.Text);
        Assert.Equal("plain message", messageException.Message);

        var inner = new InvalidOperationException("root cause");
        var wrappedException = new CSourceException("wrapped message", inner);
        Assert.Equal(string.Empty, wrappedException.FileName);
        Assert.Equal(new SourcePos(0, 0), wrappedException.Pos);
        Assert.Equal("wrapped message", wrappedException.Text);
        Assert.Equal("wrapped message", wrappedException.Message);
        Assert.Same(inner, wrappedException.InnerException);
    }
}
