using Xunit;

namespace TreeSitter.Tests;

public class SmokeTests
{
    [Fact]
    public void GeneratorPrintsUsage()
    {
        Assert.Equal(1, TreeSitter.Generator.Program.Main([]));
        Assert.Equal(0, TreeSitter.Generator.Program.Main(["--help"]));
        Assert.Equal(1, TreeSitter.Generator.Program.Main(["no-such-command"]));
    }
}
