using BenchmarkDotNet.Running;

namespace TreeSitter.Benchmarks;

/// <summary>
/// The benchmarks (port against native, see docs/PERFORMANCE.md). The native side needs the oracle library:
/// `bash tools/build-oracle.sh` first.
/// Usage: dotnet run -c Release --project bench/TreeSitter.Managed.Benchmarks -- [BenchmarkDotNet arguments]
/// </summary>
internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
