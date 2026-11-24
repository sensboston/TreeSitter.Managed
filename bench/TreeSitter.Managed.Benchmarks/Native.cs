using System.Reflection;
using System.Runtime.InteropServices;

namespace TreeSitter.Benchmarks;

/// <summary>
/// The native runtime v0.27.2 with the same grammars: the oracle library of the tests
/// (tools/build-oracle.sh, built with -O2), its oracle_bench_* functions.
/// </summary>
internal static unsafe partial class Native
{
    private const string LibraryName = "ts_oracle";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
    }

    public static string LibraryPath { get; } = Path.Combine(
        Inputs.RepositoryRoot, "artifacts", "oracle",
        OperatingSystem.IsWindows() ? "ts_oracle.dll" : OperatingSystem.IsMacOS() ? "libts_oracle.dylib" : "libts_oracle.so");

    /// <summary>oracle_bench_open: parses the input once and compiles the query. The arrays must stay alive (pinned) until Close.</summary>
    public static IntPtr Open(string grammar, byte* input, uint length, byte* query, uint queryLength) =>
        oracle_bench_open(grammar, input, length, query, queryLength);

    /// <summary>oracle_bench_run: 0 - full parse, 1 - edit and reparse, 2 - query captures.</summary>
    public static uint Run(IntPtr bench, int operation) => oracle_bench_run(bench, operation);

    public static void Close(IntPtr bench) => oracle_bench_close(bench);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == LibraryName ? NativeLibrary.Load(LibraryPath) : IntPtr.Zero;

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr oracle_bench_open(string grammar, byte* input, uint length, byte* query, uint queryLength);

    [LibraryImport(LibraryName)]
    private static partial uint oracle_bench_run(IntPtr bench, int operation);

    [LibraryImport(LibraryName)]
    private static partial void oracle_bench_close(IntPtr bench);
}
