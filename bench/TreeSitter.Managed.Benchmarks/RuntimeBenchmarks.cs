using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace TreeSitter.Benchmarks;

/// <summary>
/// Full parse, one-byte edit with incremental reparse, and highlights.scm captures: the
/// port and the native runtime on the same input. The edit replaces the byte in the
/// middle of the input by itself, so both sides reparse the same text.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public unsafe class RuntimeBenchmarks
{
    private GCHandle _sourceHandle;
    private GCHandle _queryHandle;
    private IntPtr _native;
    private byte[] _source = [];
    private byte[] _query = [];
    private Parser _parser = null!;
    private Tree _tree = null!;
    private Query _compiledQuery = null!;
    private QueryCursor _cursor = null!;
    private InputEdit _edit;
    private uint _offset;

    [Params("json", "c", "python")]
    public string Grammar { get; set; } = "json";

    [GlobalSetup]
    public void Setup()
    {
        _source = Inputs.Source(Grammar);
        _query = Inputs.HighlightsQuery(Grammar);
        Language language = Inputs.Language(Grammar);
        _parser = new Parser();
        _parser.SetLanguage(language);
        _tree = _parser.ParseString(_source)!;
        _compiledQuery = new Query(language, _query);
        _cursor = new QueryCursor();
        _offset = (uint)_source.Length / 2;
        _edit = new InputEdit(_offset, _offset + 1, _offset + 1, PointAt(_offset), PointAt(_offset + 1), PointAt(_offset + 1));

        _sourceHandle = GCHandle.Alloc(_source, GCHandleType.Pinned);
        _queryHandle = GCHandle.Alloc(_query, GCHandleType.Pinned);
        _native = Native.Open(Grammar, (byte*)_sourceHandle.AddrOfPinnedObject(), (uint)_source.Length,
            (byte*)_queryHandle.AddrOfPinnedObject(), (uint)_query.Length);
        if (_native == IntPtr.Zero)
        {
            throw new InvalidOperationException("oracle_bench_open failed");
        }

        // The same results on both sides, or the comparison means nothing.
        Check(ManagedParse(), NativeParse(), "parse");
        Check(ManagedReparse(), NativeReparse(), "reparse");
        Check(ManagedQuery(), NativeQuery(), "query");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Native.Close(_native);
        _sourceHandle.Free();
        _queryHandle.Free();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("parse")]
    public uint NativeParse() => Native.Run(_native, 0);

    [Benchmark]
    [BenchmarkCategory("parse")]
    public uint ManagedParse()
    {
        Tree tree = _parser.ParseString(_source)!;
        return tree.RootNode.DescendantCount;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("reparse")]
    public uint NativeReparse() => Native.Run(_native, 1);

    [Benchmark]
    [BenchmarkCategory("reparse")]
    public uint ManagedReparse()
    {
        Tree copy = _tree.Copy();
        copy.Edit(_edit);
        Tree tree = _parser.ParseString(_source, copy)!;
        return tree.RootNode.ChildCount;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("query")]
    public uint NativeQuery() => Native.Run(_native, 2);

    [Benchmark]
    [BenchmarkCategory("query")]
    public uint ManagedQuery()
    {
        _cursor.Exec(_compiledQuery, _tree.RootNode);
        uint count = 0;
        while (_cursor.NextCapture(out _, out _))
        {
            count++;
        }

        return count;
    }

    private Point PointAt(uint offset)
    {
        uint row = 0;
        uint column = 0;
        for (uint i = 0; i < offset; i++)
        {
            if (_source[i] == '\n')
            {
                row++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return new Point(row, column);
    }

    private static void Check(uint managed, uint native, string what)
    {
        if (managed != native)
        {
            throw new InvalidOperationException($"{what}: managed {managed}, native {native}");
        }
    }
}
