using System.Reflection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace TreeSitter.Tests.Oracle;

/// <summary>
/// Access to the native oracle test harness (ts_oracle). The library is test
/// only and never shipped. Its location is taken from the TS_ORACLE_PATH
/// environment variable (a directory) or, otherwise, from artifacts/oracle/
/// relative to the repository root (found via TreeSitter.Managed.slnx).
/// </summary>
public static partial class NativeOracle
{
    private const string LibraryDllName = "ts_oracle";

    private static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "ts_oracle.dll"
        : OperatingSystem.IsMacOS() ? "libts_oracle.dylib"
        : "libts_oracle.so";

    static NativeOracle()
    {
        List<string> candidates = new();
        string? directory = Environment.GetEnvironmentVariable("TS_ORACLE_PATH");
        if (!string.IsNullOrEmpty(directory))
        {
            candidates.Add(Path.Combine(directory, LibraryFileName));
        }
        string? repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot != null)
        {
            candidates.Add(Path.Combine(repositoryRoot, "artifacts", "oracle", LibraryFileName));
        }

        SearchedPaths = candidates;
        LibraryPath = null;
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                LibraryPath = candidate;
                break;
            }
        }

        if (LibraryPath != null)
        {
            NativeLibrary.SetDllImportResolver(
                typeof(NativeOracle).Assembly,
                ResolveLibrary);
        }
    }

    public static string? LibraryPath { get; }
    public static IReadOnlyList<string> SearchedPaths { get; }

    internal static bool IsCiEnvironment =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
        Environment.GetEnvironmentVariable("CI") == "1";

    private static IntPtr ResolveLibrary(string? libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryDllName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }
        if (LibraryPath == null ||
            !NativeLibrary.TryLoad(LibraryPath, out IntPtr handle))
        {
            return IntPtr.Zero;
        }
        return handle;
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TreeSitter.Managed.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr oracle_language(string name);

    [LibraryImport(LibraryDllName)]
    private static partial IntPtr oracle_dump_tables(IntPtr language);

    [LibraryImport(LibraryDllName)]
    private static partial IntPtr oracle_dump_language_api(IntPtr language);

    [LibraryImport(LibraryDllName)]
    private static partial void oracle_free(IntPtr p);

    [LibraryImport(LibraryDllName)]
    private static partial uint oracle_abi_version(IntPtr language);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr oracle_subtree_script(string script);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr oracle_lexer_script(string script);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_leaves(string grammar, byte* utf8, uint length);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_parse(string grammar, byte* input, uint length, uint flags);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_parser_dot_graphs(string grammar, byte* input, uint length);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_parser_dot_graphs_size(string grammar, byte* input, uint length);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_tree_dot_graph(string grammar, byte* input, uint length);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_query(string grammar, byte* source, uint length);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_edit(string grammar, byte* input, uint length, string edits, uint flags);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial IntPtr oracle_query_exec(string grammar, byte* source, uint sourceLength, byte* query, uint queryLength, string commands);

    [LibraryImport(LibraryDllName, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr oracle_lookahead(string grammar, string commands);

    [LibraryImport(LibraryDllName)]
    private static unsafe partial uint oracle_decode(int encoding, byte* buffer, uint length, int* codePoint);

    public static bool IsAvailable => LibraryPath != null;

    public static IntPtr OracleLanguage(string name) => oracle_language(name);

    public static string? OracleDumpTables(string name)
    {
        IntPtr language = oracle_language(name);
        if (language == IntPtr.Zero)
        {
            return null;
        }
        IntPtr buffer = oracle_dump_tables(language);
        try
        {
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    public static string? OracleDumpLanguageApi(string name)
    {
        IntPtr language = oracle_language(name);
        if (language == IntPtr.Zero)
        {
            return null;
        }
        IntPtr buffer = oracle_dump_language_api(language);
        try
        {
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    public static uint OracleAbiVersion(IntPtr language) => oracle_abi_version(language);

    /// <summary>
    /// Decodes the first code point of the input with a native decoder. The encoding is
    /// 0 for UTF-8 (ts_decode_utf8), 1 for UTF-16LE and 2 for UTF-16BE. Returns the number of
    /// bytes consumed; the code point (or -1 on error) is returned in codePoint.
    /// </summary>
    /// <param name="encoding">0, 1 or 2, see above.</param>
    /// <param name="input">The buffer. Only the first length bytes are read.</param>
    /// <param name="length">The number of bytes to decode, 0 to 4294967295.</param>
    /// <param name="codePoint">The decoded code point, or -1 on error.</param>
    public static unsafe uint OracleDecode(int encoding, byte[] input, uint length, out int codePoint)
    {
        int value = 0;
        uint consumed;
        fixed (byte* buffer = input)
        {
            consumed = oracle_decode(encoding, buffer, length, &value);
        }
        codePoint = value;
        return consumed;
    }

    /// <summary>
    /// Runs a subtree script in the native shim (oracle_subtree_script) and returns its
    /// output. The format of the script is described in tests/Oracle/FORMATS.md, section
    /// "Subtree script".
    /// </summary>
    /// <param name="script">The script text.</param>
    /// <returns>The text the native interpreter printed.</returns>
    public static string? OracleSubtreeScript(string script)
    {
        IntPtr buffer = oracle_subtree_script(script);
        try
        {
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>
    /// Runs a lexer script in the native shim (oracle_lexer_script) and returns its output.
    /// The format of the script is described in tests/Oracle/FORMATS.md, section "Lexer
    /// script".
    /// </summary>
    /// <param name="script">The script text.</param>
    /// <returns>The text the native interpreter printed.</returns>
    public static string? OracleLexerScript(string script)
    {
        IntPtr buffer = oracle_lexer_script(script);
        try
        {
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>Parses UTF-8 input natively and returns the parse leaf stream.</summary>
    public static unsafe string OracleLeaves(string grammar, byte[] utf8)
    {
        IntPtr buffer;
        fixed (byte* data = utf8)
        {
            buffer = oracle_leaves(grammar, data, checked((uint)utf8.Length));
        }
        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>
    /// Parses UTF-8 input natively (tests/Oracle/FORMATS.md, section "oracle_parse"):
    /// flag 1 adds the log, flag 2 parses again with the first tree as the old tree.
    /// </summary>
    public static unsafe string OracleParse(string grammar, byte[] utf8, uint flags)
    {
        IntPtr buffer;
        fixed (byte* data = utf8)
        {
            buffer = oracle_parse(grammar, data, checked((uint)utf8.Length), flags);
        }

        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>
    /// Parses the input natively with <c>ts_parser_print_dot_graphs</c> writing to a
    /// temporary file and returns the dot graphs (the log, the stack graphs and the tree
    /// graph) with the addresses normalized. The format is described in
    /// tests/Oracle/FORMATS.md, section "oracle_parser_dot_graphs".
    /// </summary>
    public static unsafe string OracleParserDotGraphs(string grammar, byte[] utf8)
    {
        IntPtr buffer;
        fixed (byte* data = utf8)
        {
            buffer = oracle_parser_dot_graphs(grammar, data, checked((uint)utf8.Length));
        }

        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>
    /// The size in bytes of the dot graphs <see cref="OracleParserDotGraphs"/> would
    /// return for the input, without building them: some inputs produce hundreds of
    /// megabytes of dot graphs, and the tests skip those without allocating them.
    /// The format is described in tests/Oracle/FORMATS.md, section
    /// "oracle_parser_dot_graphs_size".
    /// </summary>
    public static unsafe ulong OracleParserDotGraphsSize(string grammar, byte[] utf8)
    {
        IntPtr buffer;
        fixed (byte* data = utf8)
        {
            buffer = oracle_parser_dot_graphs_size(grammar, data, checked((uint)utf8.Length));
        }

        try
        {
            string text = Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong size))
            {
                throw new InvalidOperationException("oracle_parser_dot_graphs_size: " + text);
            }

            return size;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>
    /// Parses the input natively and prints <c>ts_tree_print_dot_graph</c> of the resulting
    /// tree to a temporary file, returning the dot graph with the addresses normalized.
    /// The format is described in tests/Oracle/FORMATS.md, section "oracle_tree_dot_graph".
    /// </summary>
    public static unsafe string OracleTreeDotGraph(string grammar, byte[] utf8)
    {
        IntPtr buffer;
        fixed (byte* data = utf8)
        {
            buffer = oracle_tree_dot_graph(grammar, data, checked((uint)utf8.Length));
        }

        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>Compiles a query natively (tests/Oracle/FORMATS.md, section "oracle_query").</summary>
    public static unsafe string OracleQuery(string grammar, byte[] source)
    {
        IntPtr buffer;
        fixed (byte* data = source)
        {
            buffer = oracle_query(grammar, data, checked((uint)source.Length));
        }

        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>Parses, edits and reparses natively (tests/Oracle/FORMATS.md, section "oracle_edit").</summary>
    public static unsafe string OracleEdit(string grammar, byte[] input, string edits, uint flags)
    {
        IntPtr buffer;
        fixed (byte* data = input)
        {
            buffer = oracle_edit(grammar, data, checked((uint)input.Length), edits, flags);
        }

        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>Runs a query natively (tests/Oracle/FORMATS.md, section "oracle_query_exec").</summary>
    public static unsafe string OracleQueryExec(string grammar, byte[] source, byte[] query, string commands)
    {
        IntPtr buffer;
        fixed (byte* sourceData = source)
        fixed (byte* queryData = query)
        {
            buffer = oracle_query_exec(grammar, sourceData, checked((uint)source.Length), queryData, checked((uint)query.Length), commands);
        }

        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    /// <summary>
    /// Runs the lookahead iterator commands natively (tests/Oracle/FORMATS.md, section
    /// "oracle_lookahead") and returns the text the shim printed.
    /// </summary>
    /// <param name="grammar">The grammar name the oracle knows, for example "json".</param>
    /// <param name="commands">One command per line; see the format document.</param>
    public static string OracleLookahead(string grammar, string commands)
    {
        IntPtr buffer = oracle_lookahead(grammar, commands);
        try
        {
            return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
        }
        finally
        {
            oracle_free(buffer);
        }
    }

    public static void EnsureAvailable()
    {
        if (IsAvailable)
        {
            return;
        }
        throw new InvalidOperationException("Native oracle library not found. " +
            "Searched: " + string.Join(", ", SearchedPaths) + ". " +
            "Build it with 'bash tools/build-oracle.sh' or set the " +
            "TS_ORACLE_PATH environment variable to the directory containing " +
            "libts_oracle.so / ts_oracle.dll / libts_oracle.dylib.");
    }
}
