// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/parser.c.
// Rules: each method has the C name above it and the same logic, in the same order.
// - TSParser fields are private fields with the same names. StackVersion is uint,
//   STACK_VERSION_NONE is Stack.VersionNone.
// - LOG(...) is Log(...) behind `if (IsLogging)`, so the text is built only when a logger
//   or a dot-graph writer is set. The text is cut like the 1024-byte debug buffer of the
//   C code; every log message is also written to the dot-graph writer as a `graph`
//   block, exactly like ts_parser__log.
// - LOG_STACK and LOG_TREE write to the dot-graph writer when it is set, even without a
//   logger (PrintDotGraphs). The dot graphs (ts_parser_print_dot_graphs) are ported with
//   ts_subtree_print_dot_graph (SubtreeOps.PrintDotGraph).
// - The wasm store is out of scope (ts_language_is_wasm is always false).
// - The external scanner payload is the IExternalScanner object; destroy is Dispose.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TreeSitter.Abi;
using TreeSitter.Parsing;
using TreeSitter.Subtrees;
using static System.FormattableString;

namespace TreeSitter;

/// <summary>A parser (TSParser). Not thread safe.</summary>
public sealed class Parser
{
    private const uint MaxVersionCount = 6;
    private const uint MaxVersionCountOverflow = 4;
    private const uint MaxSummaryDepth = 16;
    private const uint MaxCostDifference = 18 * ErrorCosts.ErrorCostPerSkippedTree;
    private const uint OpCountPerParserCallbackCheck = 100;

    /// <summary>TREE_SITTER_SERIALIZATION_BUFFER_SIZE</summary>
    private const int SerializationBufferSize = 1024;

    /// <summary>TS_TREE_STATE_NONE</summary>
    private const ushort TreeStateNone = ushort.MaxValue;

    /// <summary>TREE_SITTER_LANGUAGE_VERSION</summary>
    private const uint LanguageVersion = 15;

    /// <summary>TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION</summary>
    private const uint MinCompatibleLanguageVersion = 13;

    private readonly Lexer lexer;
    private readonly Stack stack;
    private readonly SubtreePool treePool;
    private Language? language;
    private TsArray<ReduceAction> reduceActions;
    private Subtree finishedTree;
    private TsArray<Subtree> trailingExtras;
    private TsArray<Subtree> trailingExtras2;
    private TsArray<Subtree> scratchTrees;
    private TokenCache tokenCache;
    private ReusableNode reusableNode;
    private IExternalScanner? externalScannerPayload;
    private uint acceptCount;
    private uint operationCount;
    private Subtree oldTree;
    private TsArray<Range> includedRangeDifferences;
    private ParseOptions? parseOptions;
    private readonly ParseState parseState = new ParseState();
    private uint includedRangeDifferenceIndex;
    private bool hasScannerError;
    private bool canceledBalancing;
    private bool hasError;

    /// <summary>The writer the dot graphs are printed to (ts_parser_print_dot_graphs):
    ///   the log messages, the stack graphs and the tree graph. Null when disabled.</summary>
    private TextWriter? dotGraphWriter;

    /// <summary>The serialization half of Lexer.debug_buffer: the external scanner state.</summary>
    private readonly byte[] serializationBuffer = new byte[SerializationBufferSize];

    /// <summary>TokenCache</summary>
    private struct TokenCache
    {
        internal Subtree Token;
        internal Subtree LastExternalToken;
        internal uint ByteIndex;
    }

    /// <summary>ErrorStatus</summary>
    private struct ErrorStatus
    {
        internal uint Cost;
        internal uint NodeCount;
        internal int DynamicPrecedence;
        internal bool IsInError;
    }

    /// <summary>ErrorComparison</summary>
    private enum ErrorComparison
    {
        TakeLeft,
        PreferLeft,
        None,
        PreferRight,
        TakeRight,
    }

    // ------------------------------------------------------------------ Parser - Public

    /// <summary>ts_parser_new</summary>
    public Parser()
    {
        lexer = new Lexer();
        reduceActions = new TsArray<ReduceAction>();
        reduceActions.Reserve(4);
        treePool = new SubtreePool(32);
        stack = new Stack(treePool);
        finishedTree = Subtree.Null;
        reusableNode = ReusableNode.New();
        language = null;
        hasScannerError = false;
        hasError = false;
        canceledBalancing = false;
        externalScannerPayload = null;
        operationCount = 0;
        oldTree = Subtree.Null;
        includedRangeDifferences = new TsArray<Range>();
        includedRangeDifferenceIndex = 0;
        SetCachedToken(0, Subtree.Null, Subtree.Null);
    }

    /// <summary>ts_parser_language</summary>
    public Language? Language => language;

    /// <summary>ts_parser_set_language</summary>
    /// <returns>False when the language cannot be used (wrong ABI version, no lex function).</returns>
    public bool SetLanguage(Language? value)
    {
        Reset();
        language = null;

        if (value is not null)
        {
            if (value.AbiVersion > LanguageVersion || value.AbiVersion < MinCompatibleLanguageVersion)
            {
                return false;
            }

            // ts_language_is_parseable
            if (value.Tables.LexFn is null)
            {
                return false;
            }
        }

        language = value;
        return true;
    }

    /// <summary>ts_parser_logger / ts_parser_set_logger</summary>
    public Logger? Logger
    {
        get => lexer.Logger;
        set => lexer.Logger = value;
    }

    /// <summary>ts_parser_set_included_ranges</summary>
    /// <returns>False when the ranges overlap or are not in order.</returns>
    public bool SetIncludedRanges(ReadOnlySpan<Range> ranges) => lexer.SetIncludedRanges(ranges);

    /// <summary>ts_parser_included_ranges</summary>
    public Range[] IncludedRanges() => lexer.IncludedRanges.ToArray();

    /// <summary>ts_parser_reset</summary>
    public void Reset()
    {
        ExternalScannerDestroy();

        if (!oldTree.IsNull)
        {
            SubtreeOps.Release(treePool, oldTree);
            oldTree = Subtree.Null;
        }

        reusableNode.Clear();
        lexer.Reset(Length.Zero);
        stack.Clear();
        SetCachedToken(0, Subtree.Null, Subtree.Null);
        if (!finishedTree.IsNull)
        {
            SubtreeOps.Release(treePool, finishedTree);
            finishedTree = Subtree.Null;
        }

        acceptCount = 0;
        hasScannerError = false;
        hasError = false;
        canceledBalancing = false;
        parseOptions = null;
        parseState.CurrentByteOffset = 0;
        parseState.HasError = false;
    }

    /// <summary>Releases arena-held subtrees (ts_parser_delete).</summary>
    ~Parser()
    {
        // Release arena-held subtrees (A5). Do not dispose the external scanner here:
        // it is a managed object and may have been finalized already.
        if (!oldTree.IsNull)
        {
            SubtreeOps.Release(treePool, oldTree);
            oldTree = Subtree.Null;
        }

        if (!finishedTree.IsNull)
        {
            SubtreeOps.Release(treePool, finishedTree);
            finishedTree = Subtree.Null;
        }

        // ts_parser__set_cached_token(self, 0, NULL_SUBTREE, NULL_SUBTREE): releases only
        // the cached tokens that are set.
        SetCachedToken(0, Subtree.Null, Subtree.Null);

        stack.Clear();
        reusableNode.Clear();

        // trailing_extras, trailing_extras2 and scratch_trees do not own their elements:
        // ts_parser_delete frees only their memory (array_delete), so nothing is released.
    }

    private SubtreePool? releasePool;

    /// <summary>ts_parser_parse</summary>
    /// <returns>The tree, or null when there is no language, the old tree has another
    /// language, or the parse was stopped by the progress callback.</returns>
    public Tree? Parse(IParseInput input, Tree? oldTree = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        Tree? result = null;
        if (language is null || (oldTree is not null && !ReferenceEquals(oldTree.Language, language)))
        {
            return null;
        }

        // Trees released by finalizers since the last parse go back to this thread's caches.
        // Not through treePool: its tree stack keeps the state of a canceled balancing
        // between parse calls, and a release clears that stack.
        SubtreeArena.ReleasePending(releasePool ??= new SubtreePool(0));

        lexer.SetInput(input);
        includedRangeDifferences.Clear();
        includedRangeDifferenceIndex = 0;

        operationCount = 0;

        uint position = 0, lastPosition = 0, versionCount = 0;
        if (HasOutstandingParse())
        {
            if (IsLogging)
            {
                Log("resume_parsing");
            }

            if (canceledBalancing)
            {
                goto balance;
            }
        }
        else
        {
            ExternalScannerCreate();
            if (hasScannerError)
            {
                goto exit;
            }

            if (oldTree is not null)
            {
                SubtreeOps.Retain(oldTree.Root);
                this.oldTree = oldTree.Root;
                RangeArray.GetChangedRanges(
                    oldTree.IncludedRangesSpan,
                    lexer.IncludedRanges,
                    ref includedRangeDifferences);
                reusableNode.Reset(oldTree.Root);
                if (IsLogging)
                {
                    Log("parse_after_edit");
                }

                LogTree(this.oldTree);

                for (uint i = 0; i < includedRangeDifferences.Size; i++)
                {
                    Range range = includedRangeDifferences.Get(i);
                    if (IsLogging)
                    {
                        Log(Invariant($"different_included_range {range.StartByte} - {range.EndByte}"));
                    }
                }
            }
            else
            {
                reusableNode.Clear();
                if (IsLogging)
                {
                    Log("new_parse");
                }
            }
        }

        do
        {
            for (
                uint version = 0;
                (versionCount = stack.VersionCount) > version;
                version++)
            {
                bool allowNodeReuse = versionCount == 1;
                while (stack.IsActive(version))
                {
                    if (IsLogging)
                    {
                        Log(Invariant(
                            $"process version:{version}, version_count:{stack.VersionCount}, state:{(int)stack.State(version)}, row:{stack.Position(version).Extent.Row}, col:{stack.Position(version).Extent.Column}"));
                    }

                    if (!Advance(version, allowNodeReuse))
                    {
                        if (hasScannerError)
                        {
                            goto exit;
                        }

                        GC.KeepAlive(oldTree);
                        return null;
                    }

                    LogStack();

                    position = stack.Position(version).Bytes;
                    if (position > lastPosition || (version > 0 && position == lastPosition))
                    {
                        lastPosition = position;
                        break;
                    }
                }
            }

            // After advancing each version of the stack, re-sort the versions by their cost,
            // removing any versions that are no longer worth pursuing.
            uint minErrorCost = CondenseStack();

            // If there's already a finished parse tree that's better than any in-progress version,
            // then terminate parsing. Clear the parse stack to remove any extra references to subtrees
            // within the finished tree, ensuring that these subtrees can be safely mutated in-place
            // for rebalancing.
            if (!finishedTree.IsNull && finishedTree.ErrorCost < minErrorCost)
            {
                stack.Clear();
                break;
            }

            while (includedRangeDifferenceIndex < includedRangeDifferences.Size)
            {
                Range range = includedRangeDifferences.Get(includedRangeDifferenceIndex);
                if (range.EndByte <= position)
                {
                    includedRangeDifferenceIndex++;
                }
                else
                {
                    break;
                }
            }
        }
        while (versionCount != 0);

    balance:
        Debug.Assert(!finishedTree.IsNull, "the parse ended without a finished tree");
        if (!BalanceSubtree())
        {
            canceledBalancing = true;
            GC.KeepAlive(oldTree);
            return null;
        }

        canceledBalancing = false;
        if (IsLogging)
        {
            Log("done");
        }

        LogTree(finishedTree);

        result = new Tree(finishedTree, language, lexer.IncludedRanges);
        finishedTree = Subtree.Null;

    exit:
        GC.KeepAlive(oldTree);
        Reset();
        return result;
    }

    /// <summary>ts_parser_parse_with_options</summary>
    public Tree? Parse(IParseInput input, Tree? oldTree, ParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        parseOptions = options;
        Tree? result = Parse(input, oldTree);

        // Reset parser options before further parse calls.
        parseOptions = null;
        return result;
    }

    /// <summary>ts_parser_parse_string</summary>
    public Tree? ParseString(ReadOnlyMemory<byte> text, Tree? oldTree = null) =>
        ParseStringEncoding(text, InputEncoding.Utf8, oldTree);

    /// <summary>ts_parser_parse_string_encoding</summary>
    public Tree? ParseStringEncoding(ReadOnlyMemory<byte> text, InputEncoding encoding, Tree? oldTree = null) =>
        Parse(ParseInputs.FromBytes(text, encoding), oldTree);

    /// <summary>Parses a .NET string (UTF-16).</summary>
    public Tree? ParseString(string text, Tree? oldTree = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(ParseInputs.FromString(text), oldTree);
    }

    // ------------------------------------------------------------------ Parser - Private

    private bool IsLogging => lexer.Logger is not null || dotGraphWriter is not null;

    private Language Lang => language!;

    /// <summary>ts_parser__log: writes to the logger and, when the dot-graph writer is set,
    ///   also to it as a `graph` block.</summary>
    private void Log(string message)
    {
        string truncated = Lexer.TruncateToDebugBuffer(message);
        if (lexer.Logger is not null)
        {
            lexer.Logger.Invoke(LogType.Parse, truncated);
        }

        if (dotGraphWriter is not null)
        {
            dotGraphWriter.Write("graph {\nlabel=\"");
            foreach (char c in truncated)
            {
                if (c == '"' || c == '\\') dotGraphWriter.Write('\\');
                dotGraphWriter.Write(c);
            }

            dotGraphWriter.Write("\"\n}\n\n");
        }
    }

    /// <summary>LOG_STACK: prints the stack dot graph to the dot-graph writer.</summary>
    private void LogStack()
    {
        if (dotGraphWriter is not null)
        {
            stack.PrintDotGraph(Lang, dotGraphWriter);
            dotGraphWriter.Write("\n\n");
        }
    }

    /// <summary>LOG_TREE: prints the tree dot graph to the dot-graph writer.</summary>
    private void LogTree(Subtree tree)
    {
        if (dotGraphWriter is not null)
        {
            SubtreeOps.PrintDotGraph(tree, Lang, dotGraphWriter);
            dotGraphWriter.Write("\n");
        }
    }

    /// <summary>ts_parser_print_dot_graphs: sets the writer the dot graphs are printed to
    ///   (the log messages, the stack graphs and the tree graph). A null writer disables
    ///   the output, like a negative file descriptor in C.</summary>
    /// <param name="writer">The text writer to print the dot graphs to, or null.</param>
    public void PrintDotGraphs(TextWriter? writer)
    {
        dotGraphWriter = writer;
    }

    /// <summary>SYM_NAME</summary>
    private string SymName(ushort symbol) => Lang.SymbolName(symbol) ?? "(null)";

    /// <summary>TREE_NAME</summary>
    private string TreeName(Subtree tree) => SymName(tree.Symbol);

    /// <summary>LOG_LOOKAHEAD</summary>
    private void LogLookahead(string symbolName, uint size)
    {
        System.Text.StringBuilder builder = new System.Text.StringBuilder("lexed_lookahead sym:");
        foreach (char c in symbolName)
        {
            switch (c)
            {
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\v': builder.Append("\\v"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                case '\\': builder.Append("\\\\"); break;
                default: builder.Append(c); break;
            }
        }

        builder.Append(Invariant($", size:{size}"));
        Log(builder.ToString());
    }

    /// <summary>ts_parser__breakdown_top_of_stack</summary>
    private bool BreakdownTopOfStack(uint version)
    {
        bool didBreakDown = false;
        bool pending;
        Language lang = Lang;

        do
        {
            TsArray<StackSlice> pop = stack.PopPending(version);
            if (pop.Size == 0)
            {
                break;
            }

            didBreakDown = true;
            pending = false;
            ref StackSlice popBase = ref MemoryMarshal.GetArrayDataReference(pop.Contents!);
            for (uint i = 0; i < pop.Size; i++)
            {
                StackSlice slice = Unsafe.Add(ref popBase, (nint)i);
                ushort state = stack.State(slice.Version);
                Subtree parent = slice.Subtrees.Front();

                if ((parent.Value & 1UL) == 0)
                {
                    ref SubtreeHeapData parentHeap = ref SubtreeArena.Get(parent);
                    uint n = parentHeap.ChildCount;
                    if (n > 0)
                    {
                        ref Subtree childrenBase = ref MemoryMarshal.GetArrayDataReference(
                            SubtreeArena.GetChildren(parentHeap.ChildrenHandle));
                        for (uint j = 0; j < n; j++)
                        {
                            Subtree child = Unsafe.Add(ref childrenBase, (nint)j);
                            ulong childRaw = child.Value;
                            if ((childRaw & 1UL) != 0)
                            {
                                pending = false;
                                if ((childRaw & (1UL << 3)) == 0)
                                {
                                    ushort childSym = (ushort)((childRaw >> 8) & 0xFF);
                                    state = lang.NextState(state, childSym);
                                }
                            }
                            else
                            {
                                ref SubtreeHeapData childHeap = ref SubtreeArena.Get(child);
                                pending = childHeap.ChildCount > 0;
                                ushort childSym = childHeap.Symbol;
                                if (childSym == BuiltinSymbols.Error)
                                {
                                    state = ErrorCosts.ErrorState;
                                }
                                else if ((childHeap.Flags & SubtreeFlags.Extra) == 0)
                                {
                                    state = lang.NextState(state, childSym);
                                }

                                Interlocked.Increment(ref childHeap.RefCount);
                            }

                            stack.Push(slice.Version, child, pending, state);
                        }
                    }
                }

                if (slice.Subtrees.Size > 1)
                {
                    ref Subtree subtreesBase = ref MemoryMarshal.GetArrayDataReference(slice.Subtrees.Contents!);
                    for (uint j = 1; j < slice.Subtrees.Size; j++)
                    {
                        Subtree tree = Unsafe.Add(ref subtreesBase, (nint)j);
                        stack.Push(slice.Version, tree, false, state);
                    }
                }

                SubtreeOps.Release(treePool, parent);
                slice.Subtrees.Delete();

                if (IsLogging)
                {
                    Log("breakdown_top_of_stack tree:" + TreeName(parent));
                }

                LogStack();
            }
        }
        while (pending);

        return didBreakDown;
    }

    /// <summary>ts_parser__breakdown_lookahead</summary>
    private void BreakdownLookahead(ref Subtree lookahead, ushort state, ref ReusableNode reusableNode)
    {
        bool didDescend = false;
        Subtree tree = reusableNode.Tree;
        while (tree.ChildCount > 0 && tree.ParseState != state)
        {
            if (IsLogging)
            {
                Log("state_mismatch sym:" + TreeName(tree));
            }

            reusableNode.Descend();
            tree = reusableNode.Tree;
            didDescend = true;
        }

        if (didDescend)
        {
            SubtreeOps.Release(treePool, lookahead);
            lookahead = tree;
            SubtreeOps.Retain(lookahead);
        }
    }

    /// <summary>ts_parser__compare_versions</summary>
    private static ErrorComparison CompareVersions(ErrorStatus a, ErrorStatus b)
    {
        if (!a.IsInError && b.IsInError)
        {
            if (a.Cost < b.Cost)
            {
                return ErrorComparison.TakeLeft;
            }
            else
            {
                return ErrorComparison.PreferLeft;
            }
        }

        if (a.IsInError && !b.IsInError)
        {
            if (b.Cost < a.Cost)
            {
                return ErrorComparison.TakeRight;
            }
            else
            {
                return ErrorComparison.PreferRight;
            }
        }

        if (a.Cost < b.Cost)
        {
            if ((b.Cost - a.Cost) * (1 + a.NodeCount) > MaxCostDifference)
            {
                return ErrorComparison.TakeLeft;
            }
            else
            {
                return ErrorComparison.PreferLeft;
            }
        }

        if (b.Cost < a.Cost)
        {
            if ((a.Cost - b.Cost) * (1 + b.NodeCount) > MaxCostDifference)
            {
                return ErrorComparison.TakeRight;
            }
            else
            {
                return ErrorComparison.PreferRight;
            }
        }

        if (a.DynamicPrecedence > b.DynamicPrecedence)
        {
            return ErrorComparison.PreferLeft;
        }

        if (b.DynamicPrecedence > a.DynamicPrecedence)
        {
            return ErrorComparison.PreferRight;
        }

        return ErrorComparison.None;
    }

    /// <summary>ts_parser__version_status</summary>
    private ErrorStatus VersionStatus(uint version)
    {
        uint cost = stack.ErrorCost(version);
        bool isPaused = stack.IsPaused(version);
        if (isPaused)
        {
            cost += ErrorCosts.ErrorCostPerSkippedTree;
        }

        return new ErrorStatus
        {
            Cost = cost,
            NodeCount = stack.NodeCountSinceError(version),
            DynamicPrecedence = stack.DynamicPrecedence(version),
            IsInError = isPaused || stack.State(version) == ErrorCosts.ErrorState,
        };
    }

    /// <summary>ts_parser__better_version_exists</summary>
    private bool BetterVersionExists(uint version, bool isInError, uint cost)
    {
        if (!finishedTree.IsNull && finishedTree.ErrorCost <= cost)
        {
            return true;
        }

        Length position = stack.Position(version);
        ErrorStatus status = new ErrorStatus
        {
            Cost = cost,
            IsInError = isInError,
            DynamicPrecedence = stack.DynamicPrecedence(version),
            NodeCount = stack.NodeCountSinceError(version),
        };

        for (uint i = 0, n = stack.VersionCount; i < n; i++)
        {
            if (i == version ||
                !stack.IsActive(i) ||
                stack.Position(i).Bytes < position.Bytes)
            {
                continue;
            }

            ErrorStatus statusI = VersionStatus(i);
            switch (CompareVersions(status, statusI))
            {
                case ErrorComparison.TakeRight:
                    return true;
                case ErrorComparison.PreferRight:
                    if (stack.CanMerge(i, version))
                    {
                        return true;
                    }

                    break;
                default:
                    break;
            }
        }

        return false;
    }

    /// <summary>ts_parser__call_main_lex_fn</summary>
    private bool CallMainLexFn(LexerMode lexMode) => Lang.Tables.LexFn!(lexer, lexMode.LexState);

    /// <summary>ts_parser__call_keyword_lex_fn</summary>
    private bool CallKeywordLexFn() => Lang.Tables.KeywordLexFn!(lexer, 0);

    /// <summary>ts_parser__external_scanner_create</summary>
    private void ExternalScannerCreate()
    {
        if (language is not null && language.Tables.ExternalScannerStates is not null)
        {
            if (language.Tables.ExternalScannerCreate is not null)
            {
                externalScannerPayload = language.Tables.ExternalScannerCreate();
            }
        }
    }

    /// <summary>ts_parser__external_scanner_destroy</summary>
    private void ExternalScannerDestroy()
    {
        if (language is not null && externalScannerPayload is IDisposable disposable)
        {
            disposable.Dispose();
        }

        externalScannerPayload = null;
    }

    /// <summary>ts_parser__external_scanner_serialize</summary>
    private uint ExternalScannerSerialize()
    {
        uint length = Scanner.Serialize(serializationBuffer);
        Debug.Assert(length <= SerializationBufferSize, "the external scanner state is too long");
        return length;
    }

    /// <summary>ts_parser__external_scanner_deserialize</summary>
    private void ExternalScannerDeserialize(Subtree externalToken)
    {
        if (externalToken.IsNull)
        {
            Scanner.Deserialize(ReadOnlySpan<byte>.Empty);
        }
        else
        {
            ref SubtreeHeapData hd = ref SubtreeArena.Get(externalToken);
            Scanner.Deserialize(ExternalScannerState.DataOf(in hd.ExternalScannerState));
        }
    }

    /// <summary>ts_parser__external_scanner_scan</summary>
    private bool ExternalScannerScan(ushort externalLexState)
    {
        Lang.TryGetEnabledExternalTokens(externalLexState, out ReadOnlySpan<bool> validExternalTokens);
        return Scanner.Scan(lexer, validExternalTokens);
    }

    /// <summary>The payload of the C code, which the external scanner functions receive.</summary>
    private IExternalScanner Scanner => externalScannerPayload
        ?? throw new InvalidOperationException("The language has external tokens but no external scanner.");

    /// <summary>ts_parser__can_reuse_first_leaf</summary>
    private bool CanReuseFirstLeaf(ushort state, Subtree tree, in TableEntry tableEntry)
    {
        ulong raw = tree.Value;
        ushort leafSymbol;
        ushort leafState;
        ushort parseState;
        bool isKeyword;
        uint sizeBytes;
        if ((raw & 1UL) != 0)
        {
            leafSymbol = (ushort)((raw >> 8) & 0xFF);
            leafState = (ushort)((raw >> 16) & 0xFFFF);
            parseState = leafState;
            isKeyword = (raw & (1UL << 6)) != 0;
            sizeBytes = (uint)((raw >> 56) & 0xFF);
        }
        else
        {
            ref SubtreeHeapData heap = ref SubtreeArena.Get(tree);
            parseState = heap.ParseState;
            if (heap.ChildCount == 0)
            {
                leafSymbol = heap.Symbol;
                leafState = parseState;
            }
            else
            {
                leafSymbol = heap.FirstLeafSymbol;
                leafState = heap.FirstLeafParseState;
            }

            isKeyword = (heap.Flags & SubtreeFlags.IsKeyword) != 0;
            sizeBytes = heap.Size.Bytes;
        }

        return CanReuseFirstLeafCore(state, leafSymbol, leafState, parseState, isKeyword, sizeBytes, in tableEntry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CanReuseFirstLeafCore(
        ushort state,
        ushort leafSymbol,
        ushort leafState,
        ushort parseState,
        bool isKeyword,
        uint sizeBytes,
        in TableEntry tableEntry)
    {
        LanguageTables tables = Lang.Tables;
        LexerMode[] lexModes = tables.LexModes!;
        ref LexerMode modesBase = ref MemoryMarshal.GetArrayDataReference(lexModes);
        LexerMode currentLexMode = Unsafe.Add(ref modesBase, (nint)state);

        // At the end of a non-terminal extra node, the lexer normally returns
        // NULL, which indicates that the parser should look for a reduce action
        // at symbol `0`. Avoid reusing tokens in this situation to ensure that
        // the same thing happens when incrementally reparsing.
        if (currentLexMode.LexState == ushort.MaxValue)
        {
            return false;
        }

        // If the token was created in a state with the same set of lookaheads, it is reusable.
        if (tableEntry.ActionCount > 0)
        {
            LexerMode leafLexMode = Unsafe.Add(ref modesBase, (nint)leafState);
            if (leafLexMode == currentLexMode &&
                (leafSymbol != tables.KeywordCaptureToken || (!isKeyword && parseState == state)))
            {
                return true;
            }
        }

        // Empty tokens are not reusable in states with different lookaheads.
        if (sizeBytes == 0 && leafSymbol != BuiltinSymbols.End)
        {
            return false;
        }

        // If the current state allows external tokens or other tokens that conflict with this
        // token, this token is not reusable.
        return currentLexMode.ExternalLexState == 0 && tableEntry.IsReusable;
    }

    /// <summary>ts_parser__lex</summary>
    private Subtree Lex(uint version, ushort parseState)
    {
        LexerMode lexMode = Lang.LexModeForState(parseState);
        if (lexMode.LexState == ushort.MaxValue)
        {
            if (IsLogging)
            {
                Log("no_lookahead_after_non_terminal_extra");
            }

            return Subtree.Null;
        }

        Length startPosition = stack.Position(version);
        Subtree externalToken = stack.LastExternalToken(version);

        bool foundExternalToken = false;
        bool errorMode = parseState == ErrorCosts.ErrorState;
        bool skippedError = false;
        bool calledGetColumn = false;
        int firstErrorCharacter = 0;
        Length errorStartPosition = Length.Zero;
        Length errorEndPosition = Length.Zero;
        uint lookaheadEndByte = 0;
        uint externalScannerStateLen = 0;
        bool externalScannerStateChanged = false;
        lexer.Reset(startPosition);

        for (;;)
        {
            bool foundToken = false;
            Length currentPosition = lexer.CurrentPosition;
            (bool Valid, uint Value) columnData = lexer.ColumnData;

            if (lexMode.ExternalLexState != 0)
            {
                if (IsLogging)
                {
                    Log(Invariant(
                        $"lex_external state:{(int)lexMode.ExternalLexState}, row:{currentPosition.Extent.Row}, column:{currentPosition.Extent.Column}"));
                }

                lexer.Start();
                ExternalScannerDeserialize(externalToken);
                foundToken = ExternalScannerScan(lexMode.ExternalLexState);
                calledGetColumn |= lexer.DidGetColumn;
                if (hasScannerError)
                {
                    return Subtree.Null;
                }

                lexer.Finish(ref lookaheadEndByte);

                if (foundToken)
                {
                    externalScannerStateLen = ExternalScannerSerialize();
                    externalScannerStateChanged = !SubtreeOps.GetExternalScannerState(externalToken)
                        .Eq(serializationBuffer.AsSpan(0, (int)externalScannerStateLen));

                    // Avoid infinite loops caused by the external scanner returning empty tokens.
                    // Empty tokens are needed in some circumstances, e.g. indent/dedent tokens
                    // in Python. Ignore the following classes of empty tokens:
                    //
                    // * Tokens produced during error recovery. When recovering from an error,
                    //   all tokens are allowed, so it's easy to accidentally return unwanted
                    //   empty tokens.
                    // * Tokens that are marked as 'extra' in the grammar. These don't change
                    //   the parse state, so they would definitely cause an infinite loop.
                    if (
                        lexer.TokenEndPosition.Bytes <= currentPosition.Bytes &&
                        !externalScannerStateChanged)
                    {
                        ushort symbol = Lang.Tables.ExternalScannerSymbolMap![lexer.ResultSymbol];
                        ushort nextParseState = Lang.NextState(parseState, symbol);
                        bool tokenIsExtra = nextParseState == parseState;
                        if (errorMode || !stack.HasAdvancedSinceError(version) || tokenIsExtra)
                        {
                            if (IsLogging)
                            {
                                Log("ignore_empty_external_token symbol:" +
                                    SymName(Lang.Tables.ExternalScannerSymbolMap![lexer.ResultSymbol]));
                            }

                            foundToken = false;
                        }
                    }
                }

                if (foundToken)
                {
                    foundExternalToken = true;
                    break;
                }

                lexer.Reset(currentPosition);
                lexer.ColumnData = columnData;
            }

            if (IsLogging)
            {
                Log(Invariant(
                    $"lex_internal state:{(int)lexMode.LexState}, row:{currentPosition.Extent.Row}, column:{currentPosition.Extent.Column}"));
            }

            lexer.Start();
            foundToken = CallMainLexFn(lexMode);
            lexer.Finish(ref lookaheadEndByte);
            if (foundToken)
            {
                break;
            }

            if (!errorMode)
            {
                errorMode = true;
                lexMode = Lang.LexModeForState(ErrorCosts.ErrorState);
                lexer.Reset(startPosition);
                continue;
            }

            if (!skippedError)
            {
                if (IsLogging)
                {
                    Log("skip_unrecognized_character");
                }

                skippedError = true;
                errorStartPosition = lexer.TokenStartPosition;
                errorEndPosition = lexer.TokenStartPosition;
                firstErrorCharacter = lexer.Lookahead;
            }

            if (lexer.CurrentPosition.Bytes == errorEndPosition.Bytes)
            {
                if (lexer.Eof())
                {
                    lexer.ResultSymbol = BuiltinSymbols.Error;
                    break;
                }

                lexer.Advance(false);
            }

            errorEndPosition = lexer.CurrentPosition;
        }

        Subtree result;
        if (skippedError)
        {
            Length padding = Length.Sub(errorStartPosition, startPosition);
            Length size = Length.Sub(errorEndPosition, errorStartPosition);
            uint lookaheadBytes = lookaheadEndByte - errorEndPosition.Bytes;
            result = SubtreeOps.NewError(
                treePool,
                firstErrorCharacter,
                padding,
                size,
                lookaheadBytes,
                parseState,
                Lang);
        }
        else
        {
            bool isKeyword = false;
            ushort symbol = lexer.ResultSymbol;
            Length padding = Length.Sub(lexer.TokenStartPosition, startPosition);
            Length size = Length.Sub(lexer.TokenEndPosition, lexer.TokenStartPosition);
            uint lookaheadBytes = lookaheadEndByte - lexer.TokenEndPosition.Bytes;

            if (foundExternalToken)
            {
                symbol = Lang.Tables.ExternalScannerSymbolMap![symbol];
            }
            else if (symbol == Lang.Tables.KeywordCaptureToken && symbol != 0)
            {
                uint endByte = lexer.TokenEndPosition.Bytes;
                lexer.Reset(lexer.TokenStartPosition);
                lexer.Start();

                isKeyword = CallKeywordLexFn();

                if (
                    isKeyword &&
                    lexer.TokenEndPosition.Bytes == endByte &&
                    (
                        Lang.HasActions(parseState, lexer.ResultSymbol) ||
                        Lang.IsReservedWord(parseState, lexer.ResultSymbol)))
                {
                    symbol = lexer.ResultSymbol;
                }
            }

            result = SubtreeOps.NewLeaf(
                treePool,
                symbol,
                padding,
                size,
                lookaheadBytes,
                parseState,
                foundExternalToken,
                calledGetColumn,
                isKeyword,
                Lang);

            if (foundExternalToken)
            {
                ref SubtreeHeapData mutResult = ref SubtreeArena.Get(result);
                ExternalScannerState extState = ExternalScannerState.Create(
                    serializationBuffer.AsSpan(0, (int)externalScannerStateLen));
                extState.WriteTo(ref mutResult.ExternalScannerState);
                mutResult.SetFlag(SubtreeFlags.HasExternalScannerStateChange, externalScannerStateChanged);
            }
        }

        if (IsLogging)
        {
            LogLookahead(TreeName(result), result.TotalSize.Bytes);
        }

        return result;
    }

    /// <summary>ts_parser__get_cached_token</summary>
    private Subtree GetCachedToken(ushort state, uint position, Subtree lastExternalToken, ref TableEntry tableEntry)
    {
        ref TokenCache cache = ref tokenCache;
        if (
            !cache.Token.IsNull && cache.ByteIndex == position &&
            SubtreeOps.ExternalScannerStateEq(cache.LastExternalToken, lastExternalToken))
        {
            Lang.GetTableEntry(state, cache.Token.Symbol, out tableEntry);
            if (CanReuseFirstLeaf(state, cache.Token, tableEntry))
            {
                SubtreeOps.Retain(cache.Token);
                return cache.Token;
            }
        }

        return Subtree.Null;
    }

    /// <summary>ts_parser__set_cached_token</summary>
    private void SetCachedToken(uint byteIndex, Subtree lastExternalToken, Subtree token)
    {
        ref TokenCache cache = ref tokenCache;
        if (!token.IsNull)
        {
            SubtreeOps.Retain(token);
        }

        if (!lastExternalToken.IsNull)
        {
            SubtreeOps.Retain(lastExternalToken);
        }

        if (!cache.Token.IsNull)
        {
            SubtreeOps.Release(treePool, cache.Token);
        }

        if (!cache.LastExternalToken.IsNull)
        {
            SubtreeOps.Release(treePool, cache.LastExternalToken);
        }

        cache.Token = token;
        cache.ByteIndex = byteIndex;
        cache.LastExternalToken = lastExternalToken;
    }

    /// <summary>ts_parser__has_included_range_difference</summary>
    private bool HasIncludedRangeDifference(uint startPosition, uint endPosition) =>
        RangeArray.Intersects(
            includedRangeDifferences,
            includedRangeDifferenceIndex,
            startPosition,
            endPosition);

    /// <summary>ts_subtree_external_scanner_state_eq without copying ShortBuffer or allocating byte[].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ExternalScannerStatesMatch(Subtree self, Subtree other)
    {
        if (self.Value == other.Value)
        {
            return true;
        }

        return ExternalScannerStatesMatchSlow(self, other);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ExternalScannerStatesMatchSlow(Subtree self, Subtree other)
    {
        ReadOnlySpan<byte> left = ReadOnlySpan<byte>.Empty;
        if (!self.IsNull && (self.Value & 1UL) == 0)
        {
            ref SubtreeHeapData leftHeap = ref SubtreeArena.Get(self);
            if ((leftHeap.Flags & SubtreeFlags.HasExternalTokens) != 0 && leftHeap.ChildCount == 0)
            {
                left = ExternalScannerState.DataOf(in leftHeap.ExternalScannerState);
            }
        }

        ReadOnlySpan<byte> right = ReadOnlySpan<byte>.Empty;
        if (!other.IsNull && (other.Value & 1UL) == 0)
        {
            ref SubtreeHeapData rightHeap = ref SubtreeArena.Get(other);
            if ((rightHeap.Flags & SubtreeFlags.HasExternalTokens) != 0 && rightHeap.ChildCount == 0)
            {
                right = ExternalScannerState.DataOf(in rightHeap.ExternalScannerState);
            }
        }

        return left.SequenceEqual(right);
    }

    /// <summary>ts_parser__reuse_node</summary>
    private Subtree ReuseNode(
        uint version,
        ref ushort state,
        uint position,
        Subtree lastExternalToken,
        ref TableEntry tableEntry)
    {
        Language lang = Lang;
        while (reusableNode.Stack.Size > 0)
        {
            ref ReusableNodeEntry top = ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(reusableNode.Stack.Contents!),
                (nint)(reusableNode.Stack.Size - 1));
            Subtree result = top.Tree;
            if (result.IsNull)
            {
                break;
            }

            uint byteOffset = top.ByteOffset;
            ulong raw = result.Value;

            // Step 1: position check. Most skipped nodes fail byteOffset < position, where C
            // only computes end_byte_offset and checks ts_subtree_is_eof - do not decode flags
            // or first_leaf fields or compute Point.Add until byteOffset == position.
            ushort symbol;
            uint totalBytes;
            if ((raw & 1UL) != 0)
            {
                symbol = (ushort)((raw >> 8) & 0xFF);
                totalBytes = ((uint)((raw >> 48) & 0xFF)) + ((uint)((raw >> 56) & 0xFF));
            }
            else
            {
                ref SubtreeHeapData c = ref SubtreeArena.Get(result);
                symbol = c.Symbol;
                totalBytes = c.Padding.Bytes + c.Size.Bytes;
            }

            bool isEof = symbol == BuiltinSymbols.End;
            uint endByteOffset = isEof ? uint.MaxValue : byteOffset + totalBytes;

            if (byteOffset > position)
            {
                if (IsLogging)
                {
                    Log("before_reusable_node symbol:" + SymName(symbol));
                }

                break;
            }

            if (byteOffset < position)
            {
                if (IsLogging)
                {
                    Log("past_reusable_node symbol:" + SymName(symbol));
                }

                if (endByteOffset <= position || !reusableNode.Descend())
                {
                    reusableNode.Advance();
                }

                continue;
            }

            if (!ExternalScannerStatesMatch(reusableNode.LastExternalToken, lastExternalToken))
            {
                if (IsLogging)
                {
                    Log("reusable_node_has_different_external_scanner_state symbol:" + SymName(symbol));
                }

                reusableNode.Advance();
                continue;
            }

            ushort leafSymbol;
            ushort leafState;
            ushort parseState;
            uint sizeBytes;
            uint lookaheadBytes;
            bool hasChanges;
            bool isMissing;
            bool isFragile;
            bool isKeyword;
            if ((raw & 1UL) != 0)
            {
                leafSymbol = symbol;
                leafState = (ushort)((raw >> 16) & 0xFFFF);
                parseState = leafState;
                sizeBytes = (uint)((raw >> 56) & 0xFF);
                lookaheadBytes = (uint)((raw >> 44) & 0x0F);
                hasChanges = (raw & (1UL << 4)) != 0;
                isMissing = (raw & (1UL << 5)) != 0;
                isFragile = false;
                isKeyword = (raw & (1UL << 6)) != 0;
            }
            else
            {
                ref SubtreeHeapData c = ref SubtreeArena.Get(result);
                SubtreeFlags flags = c.Flags;
                parseState = c.ParseState;
                if (c.ChildCount == 0)
                {
                    leafSymbol = symbol;
                    leafState = parseState;
                }
                else
                {
                    leafSymbol = c.FirstLeafSymbol;
                    leafState = c.FirstLeafParseState;
                }

                sizeBytes = c.Size.Bytes;
                lookaheadBytes = c.LookaheadBytes;
                hasChanges = (flags & SubtreeFlags.HasChanges) != 0;
                isMissing = (flags & SubtreeFlags.IsMissing) != 0;
                isFragile = (flags & (SubtreeFlags.FragileLeft | SubtreeFlags.FragileRight)) != 0;
                isKeyword = (flags & SubtreeFlags.IsKeyword) != 0;
            }

            string? reason = null;
            if (hasChanges)
            {
                reason = "has_changes";
            }
            else if (symbol == BuiltinSymbols.Error)
            {
                reason = "is_error";
            }
            else if (isMissing)
            {
                reason = "is_missing";
            }
            else if (isFragile)
            {
                reason = "is_fragile";
            }
            else if (includedRangeDifferenceIndex < includedRangeDifferences.Size &&
                     HasIncludedRangeDifference(
                         byteOffset,
                         isEof
                             ? endByteOffset
                             : endByteOffset + lookaheadBytes))
            {
                reason = "contains_different_included_range";
            }

            if (reason is not null)
            {
                if (IsLogging)
                {
                    Log("cant_reuse_node_" + reason + " tree:" + SymName(symbol));
                }

                if (!reusableNode.Descend())
                {
                    reusableNode.Advance();
                    BreakdownTopOfStack(version);
                    state = stack.State(version);
                }

                continue;
            }

            lang.GetTableEntry(state, leafSymbol, out tableEntry);
            if (!CanReuseFirstLeafCore(state, leafSymbol, leafState, parseState, isKeyword, sizeBytes, in tableEntry))
            {
                if (IsLogging)
                {
                    Log("cant_reuse_node symbol:" + SymName(symbol) + ", first_leaf_symbol:" + SymName(leafSymbol));
                }

                reusableNode.AdvancePastLeaf();
                break;
            }

            if (IsLogging)
            {
                Log("reuse_node symbol:" + SymName(symbol));
            }

            SubtreeOps.Retain(result);
            return result;
        }

        return Subtree.Null;
    }

    // Determine if a given tree should be replaced by an alternative tree.
    //
    // The decision is based on the trees' error costs (if any), their dynamic precedence,
    // and finally, as a default, by a recursive comparison of the trees' symbols.

    /// <summary>ts_parser__select_tree</summary>
    private bool SelectTree(Subtree left, Subtree right)
    {
        if (left.IsNull)
        {
            return true;
        }

        if (right.IsNull)
        {
            return false;
        }

        if (right.ErrorCost < left.ErrorCost)
        {
            if (IsLogging)
            {
                Log("select_smaller_error symbol:" + TreeName(right) + ", over_symbol:" + TreeName(left));
            }

            return true;
        }

        if (left.ErrorCost < right.ErrorCost)
        {
            if (IsLogging)
            {
                Log("select_smaller_error symbol:" + TreeName(left) + ", over_symbol:" + TreeName(right));
            }

            return false;
        }

        if (right.DynamicPrecedence > left.DynamicPrecedence)
        {
            if (IsLogging)
            {
                Log(Invariant(
                    $"select_higher_precedence symbol:{TreeName(right)}, prec:{right.DynamicPrecedence}, over_symbol:{TreeName(left)}, other_prec:{left.DynamicPrecedence}"));
            }

            return true;
        }

        if (left.DynamicPrecedence > right.DynamicPrecedence)
        {
            if (IsLogging)
            {
                Log(Invariant(
                    $"select_higher_precedence symbol:{TreeName(left)}, prec:{left.DynamicPrecedence}, over_symbol:{TreeName(right)}, other_prec:{right.DynamicPrecedence}"));
            }

            return false;
        }

        if (left.ErrorCost > 0)
        {
            return true;
        }

        int comparison = SubtreeOps.Compare(left, right, treePool);
        switch (comparison)
        {
            case -1:
                if (IsLogging)
                {
                    Log("select_earlier symbol:" + TreeName(left) + ", over_symbol:" + TreeName(right));
                }

                return false;
            case 1:
                if (IsLogging)
                {
                    Log("select_earlier symbol:" + TreeName(right) + ", over_symbol:" + TreeName(left));
                }

                return true;
            default:
                if (IsLogging)
                {
                    Log("select_existing symbol:" + TreeName(left) + ", over_symbol:" + TreeName(right));
                }

                return false;
        }
    }

    // Determine if a given tree's children should be replaced by an alternative
    // array of children.

    /// <summary>ts_parser__select_children</summary>
    private bool SelectChildren(Subtree left, in TsArray<Subtree> children)
    {
        scratchTrees.Assign(children.AsSpan());

        // Create a temporary subtree using the scratch trees array. This node does
        // not perform any allocation except for possibly growing the array to make
        // room for its own heap data. The scratch tree is never explicitly released,
        // so the same 'scratch trees' array can be reused again later.
        Subtree scratchTree = SubtreeOps.NewNode(
            left.Symbol,
            ref scratchTrees,
            0,
            Lang);

        bool selected = SelectTree(left, scratchTree);

        // In C the scratch tree lives in the reused scratch_trees buffer and is never
        // released (it does not retain its children). Here it is an arena slot: give the
        // slot and its children array back without releasing the children.
        SubtreeArena.FreeChildren(scratchTree.Handle);
        SubtreeArena.Free(scratchTree.Handle);
        return selected;
    }

    /// <summary>ts_parser__shift</summary>
    private void Shift(uint version, ushort state, Subtree lookahead, bool extra)
    {
        bool isLeaf = lookahead.ChildCount == 0;
        Subtree subtreeToPush = lookahead;
        if (extra != lookahead.Extra && isLeaf)
        {
            Subtree result = SubtreeOps.MakeMut(treePool, lookahead);
            SubtreeOps.SetExtra(ref result, extra);
            subtreeToPush = result;
        }

        stack.Push(version, subtreeToPush, !isLeaf, state);
        if (subtreeToPush.HasExternalTokens)
        {
            stack.SetLastExternalToken(version, SubtreeOps.LastExternalToken(subtreeToPush));
        }
    }

    /// <summary>ts_parser__reduce</summary>
    private uint Reduce(
        uint version,
        ushort symbol,
        uint count,
        int dynamicPrecedence,
        ushort productionId,
        bool isFragile,
        bool endOfNonTerminalExtra)
    {
        uint initialVersionCount = stack.VersionCount;

        // Pop the given number of nodes from the given version of the parse stack.
        // If stack versions have previously merged, then there may be more than one
        // path back through the stack. For each path, create a new parent node to
        // contain the popped children, and push it onto the stack in place of the
        // children.
        TsArray<StackSlice> pop = stack.PopCount(version, count);
        uint removedVersionCount = 0;
        uint haltedVersionCount = stack.HaltedVersionCount();
        for (uint i = 0; i < pop.Size; i++)
        {
            StackSlice slice = pop.Get(i);
            uint sliceVersion = slice.Version - removedVersionCount;

            // This is where new versions are added to the parse stack. The versions
            // will all be sorted and truncated at the end of the outer parsing loop.
            // Allow the maximum version count to be temporarily exceeded, but only
            // by a limited threshold.
            if (sliceVersion > MaxVersionCount + MaxVersionCountOverflow + haltedVersionCount)
            {
                stack.RemoveVersion(sliceVersion);
                SubtreeOps.ArrayDelete(treePool, ref slice.Subtrees);
                removedVersionCount++;
                while (i + 1 < pop.Size)
                {
                    if (IsLogging)
                    {
                        Log("aborting reduce with too many versions");
                    }

                    StackSlice nextSlice = pop.Get(i + 1);
                    if (nextSlice.Version != slice.Version)
                    {
                        break;
                    }

                    SubtreeOps.ArrayDelete(treePool, ref nextSlice.Subtrees);
                    i++;
                }

                continue;
            }

            // Extra tokens on top of the stack should not be included in this new parent
            // node. They will be re-pushed onto the stack after the parent node is
            // created and pushed.
            TsArray<Subtree> children = slice.Subtrees;
            SubtreeOps.ArrayRemoveTrailingExtras(ref children, ref trailingExtras);

            Subtree parent = SubtreeOps.NewNode(symbol, ref children, productionId, Lang);

            // This pop operation may have caused multiple stack versions to collapse
            // into one, because they all diverged from a common state. In that case,
            // choose one of the arrays of trees to be the parent node's children, and
            // delete the rest of the tree arrays.
            while (i + 1 < pop.Size)
            {
                StackSlice nextSlice = pop.Get(i + 1);
                if (nextSlice.Version != slice.Version)
                {
                    break;
                }

                i++;

                TsArray<Subtree> nextSliceChildren = nextSlice.Subtrees;
                SubtreeOps.ArrayRemoveTrailingExtras(ref nextSliceChildren, ref trailingExtras2);

                if (SelectChildren(parent, nextSliceChildren))
                {
                    SubtreeOps.ArrayClear(treePool, ref trailingExtras);
                    SubtreeOps.Release(treePool, parent);
                    trailingExtras.Swap(ref trailingExtras2);
                    parent = SubtreeOps.NewNode(symbol, ref nextSliceChildren, productionId, Lang);
                }
                else
                {
                    trailingExtras2.Clear();
                    SubtreeOps.ArrayDelete(treePool, ref nextSlice.Subtrees);
                }
            }

            ushort state = stack.State(sliceVersion);
            ushort nextState = Lang.NextState(state, symbol);
            ref SubtreeHeapData parentHeap = ref SubtreeArena.Get(parent);
            if (endOfNonTerminalExtra && nextState == state)
            {
                parentHeap.SetFlag(SubtreeFlags.Extra, true);
            }

            if (isFragile || pop.Size > 1 || initialVersionCount > 1)
            {
                parentHeap.SetFlag(SubtreeFlags.FragileLeft, true);
                parentHeap.SetFlag(SubtreeFlags.FragileRight, true);
                parentHeap.ParseState = TreeStateNone;
            }
            else
            {
                parentHeap.ParseState = state;
            }

            parentHeap.DynamicPrecedence += dynamicPrecedence;

            // Push the parent node onto the stack, along with any extra tokens that
            // were previously on top of the stack.
            stack.Push(sliceVersion, parent, false, nextState);
            for (uint j = 0; j < trailingExtras.Size; j++)
            {
                stack.Push(sliceVersion, trailingExtras.Get(j), false, nextState);
            }

            for (uint j = 0; j < sliceVersion; j++)
            {
                if (j == version)
                {
                    continue;
                }

                if (stack.Merge(j, sliceVersion))
                {
                    removedVersionCount++;
                    break;
                }
            }
        }

        // Return the first new stack version that was created.
        return stack.VersionCount > initialVersionCount
            ? initialVersionCount
            : Stack.VersionNone;
    }

    /// <summary>ts_parser__accept</summary>
    private void Accept(uint version, Subtree lookahead)
    {
        Debug.Assert(lookahead.IsEof, "accept without the end of the input");
        stack.Push(version, lookahead, false, 1);

        TsArray<StackSlice> pop = stack.PopAll(version);
        for (uint i = 0; i < pop.Size; i++)
        {
            TsArray<Subtree> trees = pop.Get(i).Subtrees;

            Subtree root = Subtree.Null;
            for (uint j = trees.Size - 1; j + 1 > 0; j--)
            {
                Subtree tree = trees.Get(j);
                if (!tree.Extra)
                {
                    Debug.Assert(!tree.IsInline, "the root is inline");
                    uint childCount = tree.ChildCount;
                    ReadOnlySpan<Subtree> children = tree.Children;
                    for (int k = 0; k < childCount; k++)
                    {
                        SubtreeOps.Retain(children[k]);
                    }

                    trees.Splice(j, 1, childCount, children);
                    root = SubtreeOps.NewNode(
                        tree.Symbol,
                        ref trees,
                        SubtreeArena.Get(tree).ProductionId,
                        Lang);
                    SubtreeOps.Release(treePool, tree);
                    break;
                }
            }

            Debug.Assert(!root.IsNull, "accept without a root");
            acceptCount++;

            if (!finishedTree.IsNull)
            {
                if (SelectTree(finishedTree, root))
                {
                    SubtreeOps.Release(treePool, finishedTree);
                    finishedTree = root;
                }
                else
                {
                    SubtreeOps.Release(treePool, root);
                }
            }
            else
            {
                finishedTree = root;
            }
        }

        stack.RemoveVersion(pop.Get(0).Version);
        stack.Halt(version);
    }

    /// <summary>ts_parser__process_candidate_recovery_actions</summary>
    private bool ProcessCandidateRecoveryActions(ReadOnlySpan<ParseActionEntry> actions)
    {
        bool hasShiftAction = false;
        for (int i = 0; i < actions.Length; i++)
        {
            ParseActionEntry action = actions[i];
            switch (action.Type)
            {
                case ParseActionType.Shift:
                case ParseActionType.Recover:
                    if (!action.ShiftExtra && !action.ShiftRepetition)
                    {
                        hasShiftAction = true;
                    }

                    break;
                case ParseActionType.Reduce:
                    if (action.ReduceChildCount > 0)
                    {
                        ReduceActionSet.Add(ref reduceActions, new ReduceAction
                        {
                            Symbol = action.ReduceSymbol,
                            Count = action.ReduceChildCount,
                            DynamicPrecedence = action.ReduceDynamicPrecedence,
                            ProductionId = action.ReduceProductionId,
                        });
                    }

                    break;
                default:
                    break;
            }
        }

        return hasShiftAction;
    }

    /// <summary>ts_parser__do_all_potential_reductions</summary>
    private bool DoAllPotentialReductions(uint startingVersion, ushort lookaheadSymbol)
    {
        uint initialVersionCount = stack.VersionCount;

        bool canShiftLookaheadSymbol = false;
        uint version = startingVersion;
        for (uint i = 0; true; i++)
        {
            uint versionCount = stack.VersionCount;
            if (version >= versionCount)
            {
                break;
            }

            bool merged = false;
            for (uint j = initialVersionCount; j < version; j++)
            {
                if (stack.Merge(j, version))
                {
                    merged = true;
                    break;
                }
            }

            if (merged)
            {
                continue;
            }

            ushort state = stack.State(version);
            bool hasShiftAction = false;
            reduceActions.Clear();

            if (lookaheadSymbol != 0)
            {
                Lang.GetTableEntry(state, lookaheadSymbol, out TableEntry entry);
                hasShiftAction = ProcessCandidateRecoveryActions(Actions(entry));
            }
            else
            {
                LanguageLookaheadIterator iter = Lang.Lookaheads(state);
                while (iter.Next())
                {
                    // only terminal tokens are valid lookaheads for reduction decisions
                    if (iter.Symbol == BuiltinSymbols.End || iter.Symbol >= Lang.Tables.TokenCount)
                    {
                        continue;
                    }

                    if (ProcessCandidateRecoveryActions(iter.Actions))
                    {
                        hasShiftAction = true;
                    }
                }

                // Sort reduce_actions by symbol descending to ensure deterministic
                // ordering. The LookaheadIterator may visit symbols in a different
                // order than the original linear scan (group order vs symbol order
                // for small parse states), which can produce different orderings.
                // Since reductions are applied sequentially and the last reduction
                // version survives, the order affects error recovery outcomes.
                ReduceAction[] contents = reduceActions.Contents!;
                for (uint j = 1; j < reduceActions.Size; j++)
                {
                    ReduceAction key = contents[j];
                    int k = (int)j - 1;
                    while (k >= 0 && contents[k].Symbol < key.Symbol)
                    {
                        contents[k + 1] = contents[k];
                        k--;
                    }

                    contents[k + 1] = key;
                }
            }

            uint reductionVersion = Stack.VersionNone;
            for (uint j = 0; j < reduceActions.Size; j++)
            {
                ReduceAction action = reduceActions.Get(j);

                reductionVersion = Reduce(
                    version,
                    action.Symbol,
                    action.Count,
                    action.DynamicPrecedence,
                    action.ProductionId,
                    true,
                    false);
            }

            if (hasShiftAction)
            {
                canShiftLookaheadSymbol = true;
            }
            else if (reductionVersion != Stack.VersionNone && i < MaxVersionCount)
            {
                stack.RenumberVersion(reductionVersion, version);
                continue;
            }
            else if (lookaheadSymbol != 0)
            {
                stack.RemoveVersion(version);

                // Removing this version shifts the next reduction version into its slot.
                // Check that version before advancing.
                continue;
            }

            if (version == startingVersion)
            {
                version = versionCount;
            }
            else
            {
                version++;
            }
        }

        return canShiftLookaheadSymbol;
    }

    /// <summary>ts_parser__recover_to_state</summary>
    private bool RecoverToState(uint version, uint depth, ushort goalState)
    {
        TsArray<StackSlice> pop = stack.PopCount(version, depth);
        uint previousVersion = Stack.VersionNone;

        for (uint i = 0; i < pop.Size; i++)
        {
            StackSlice slice = pop.Get(i);

            if (slice.Version == previousVersion)
            {
                SubtreeOps.ArrayDelete(treePool, ref slice.Subtrees);
                pop.Erase(i--);
                continue;
            }

            if (stack.State(slice.Version) != goalState)
            {
                stack.Halt(slice.Version);
                SubtreeOps.ArrayDelete(treePool, ref slice.Subtrees);
                pop.Erase(i--);
                continue;
            }

            TsArray<Subtree> errorTrees = stack.PopError(slice.Version);
            if (errorTrees.Size > 0)
            {
                Debug.Assert(errorTrees.Size == 1, "more than one error tree");
                Subtree errorTree = errorTrees.Get(0);
                uint errorChildCount = errorTree.ChildCount;
                if (errorChildCount > 0)
                {
                    TsArray<Subtree> nested = new TsArray<Subtree>();
                    nested.Reserve(errorChildCount);
                    for (uint j = 0; j < errorChildCount; j++)
                    {
                        Subtree child = errorTree.Children[(int)j];
                        SubtreeOps.Retain(child);
                        nested.Push(child);
                    }

                    Subtree nestedError = SubtreeOps.NewNode(
                        BuiltinSymbols.ErrorRepeat, ref nested, 0, Lang);
                    slice.Subtrees.Insert(0, nestedError);
                }

                SubtreeOps.ArrayDelete(treePool, ref errorTrees);
            }

            SubtreeOps.ArrayRemoveTrailingExtras(ref slice.Subtrees, ref trailingExtras);

            if (slice.Subtrees.Size > 0)
            {
                Subtree error = SubtreeOps.NewErrorNode(ref slice.Subtrees, true, Lang);
                stack.Push(slice.Version, error, false, goalState);
            }
            else
            {
                slice.Subtrees.Delete();
            }

            for (uint j = 0; j < trailingExtras.Size; j++)
            {
                Subtree tree = trailingExtras.Get(j);
                stack.Push(slice.Version, tree, false, goalState);
            }

            previousVersion = slice.Version;
        }

        return previousVersion != Stack.VersionNone;
    }

    /// <summary>ts_parser__recover</summary>
    private void Recover(uint version, Subtree lookahead)
    {
        bool didRecover = false;
        uint previousVersionCount = stack.VersionCount;
        Length position = stack.Position(version);
        TsArray<StackSummaryEntry>? summary = stack.GetSummary(version);
        uint nodeCountSinceError = stack.NodeCountSinceError(version);
        uint currentErrorCost = stack.ErrorCost(version);

        // When the parser is in the error state, there are two strategies for recovering with a
        // given lookahead token:
        // 1. Find a previous state on the stack in which that lookahead token would be valid. Then,
        //    create a new stack version that is in that state again. This entails popping all of the
        //    subtrees that have been pushed onto the stack since that previous state, and wrapping
        //    them in an ERROR node.
        // 2. Wrap the lookahead token in an ERROR node, push that ERROR node onto the stack, and
        //    move on to the next lookahead token, remaining in the error state.
        //
        // First, try the strategy 1. Upon entering the error state, the parser recorded a summary
        // of the previous parse states and their depths. Look at each state in the summary, to see
        // if the current lookahead token would be valid in that state.
        if (summary is not null && !lookahead.IsError)
        {
            TsArray<StackSummaryEntry> entries = summary.Value;
            for (uint i = 0; i < entries.Size; i++)
            {
                StackSummaryEntry entry = entries.Get(i);

                if (entry.State == ErrorCosts.ErrorState)
                {
                    continue;
                }

                if (entry.Position.Bytes == position.Bytes)
                {
                    continue;
                }

                uint depth = entry.Depth;
                if (nodeCountSinceError > 0)
                {
                    depth++;
                }

                // Do not recover in ways that create redundant stack versions.
                bool wouldMerge = false;
                for (uint j = 0; j < previousVersionCount; j++)
                {
                    if (
                        stack.State(j) == entry.State &&
                        stack.Position(j).Bytes == position.Bytes)
                    {
                        wouldMerge = true;
                        break;
                    }
                }

                if (wouldMerge)
                {
                    continue;
                }

                // Do not recover if the result would clearly be worse than some existing stack version.
                uint newCost =
                    currentErrorCost +
                    (entry.Depth * ErrorCosts.ErrorCostPerSkippedTree) +
                    ((position.Bytes - entry.Position.Bytes) * ErrorCosts.ErrorCostPerSkippedChar) +
                    ((position.Extent.Row - entry.Position.Extent.Row) * ErrorCosts.ErrorCostPerSkippedLine);
                if (BetterVersionExists(version, false, newCost))
                {
                    break;
                }

                // If the current lookahead token is valid in some previous state, recover to that state.
                // Then stop looking for further recoveries.
                if (Lang.HasActions(entry.State, lookahead.Symbol))
                {
                    if (RecoverToState(version, depth, entry.State))
                    {
                        didRecover = true;
                        if (IsLogging)
                        {
                            Log(Invariant($"recover_to_previous state:{entry.State}, depth:{depth}"));
                        }

                        LogStack();
                        break;
                    }
                }
            }
        }

        // In the process of attempting to recover, some stack versions may have been created
        // and subsequently halted. Remove those versions.
        for (uint i = previousVersionCount; i < stack.VersionCount; i++)
        {
            if (!stack.IsActive(i))
            {
                    if (IsLogging)
                    {
                        Log(Invariant($"removed paused version:{i}"));
                    }

                    stack.RemoveVersion(i--);
                    LogStack();
                }
        }

        // If the parser is still in the error state at the end of the file, just wrap everything
        // in an ERROR node and terminate.
        if (lookahead.IsEof)
        {
            if (IsLogging)
            {
                Log("recover_eof");
            }

            TsArray<Subtree> children = new TsArray<Subtree>();
            Subtree parent = SubtreeOps.NewErrorNode(ref children, false, Lang);
            stack.Push(version, parent, false, 1);
            Accept(version, lookahead);
            return;
        }

        // If strategy 1 succeeded, a new stack version will have been created which is able to handle
        // the current lookahead token. Now, in addition, try strategy 2 described above: skip the
        // current lookahead token by wrapping it in an ERROR node.

        // Don't pursue this additional strategy if there are already too many stack versions.
        if (didRecover && stack.VersionCount > MaxVersionCount)
        {
            stack.Halt(version);
            SubtreeOps.Release(treePool, lookahead);
            return;
        }

        if (
            didRecover &&
            lookahead.HasExternalScannerStateChange)
        {
            stack.Halt(version);
            SubtreeOps.Release(treePool, lookahead);
            return;
        }

        // Do not recover if the result would clearly be worse than some existing stack version.
        uint newCost2 =
            currentErrorCost + ErrorCosts.ErrorCostPerSkippedTree +
            (lookahead.TotalBytes * ErrorCosts.ErrorCostPerSkippedChar) +
            (lookahead.TotalSize.Extent.Row * ErrorCosts.ErrorCostPerSkippedLine);
        if (BetterVersionExists(version, false, newCost2))
        {
            stack.Halt(version);
            SubtreeOps.Release(treePool, lookahead);
            return;
        }

        // If the current lookahead token is an extra token, mark it as extra. This means it won't
        // be counted in error cost calculations.
        ReadOnlySpan<ParseActionEntry> actions = Lang.Actions(1, lookahead.Symbol);
        int n = actions.Length;
        if (n > 0 && actions[n - 1].Type == ParseActionType.Shift && actions[n - 1].ShiftExtra)
        {
            Subtree mutableLookahead = SubtreeOps.MakeMut(treePool, lookahead);
            SubtreeOps.SetExtra(ref mutableLookahead, true);
            lookahead = mutableLookahead;
        }

        // Wrap the lookahead token in an ERROR.
        if (IsLogging)
        {
            Log("skip_token symbol:" + TreeName(lookahead));
        }

        TsArray<Subtree> errorChildren = new TsArray<Subtree>();
        errorChildren.Reserve(1);
        errorChildren.Push(lookahead);
        Subtree errorRepeat = SubtreeOps.NewNode(
            BuiltinSymbols.ErrorRepeat,
            ref errorChildren,
            0,
            Lang);

        // If other tokens have already been skipped, so there is already an ERROR at the top of the
        // stack, then pop that ERROR off the stack and wrap the two ERRORs together into one larger
        // ERROR.
        if (nodeCountSinceError > 0)
        {
            TsArray<StackSlice> pop = stack.PopCount(version, 1);

            // TODO: Figure out how to make this condition occur.
            // See https://github.com/atom/atom/issues/18450#issuecomment-439579778
            // If multiple stack versions have merged at this point, just pick one of the errors
            // arbitrarily and discard the rest.
            if (pop.Size > 1)
            {
                for (uint i = 1; i < pop.Size; i++)
                {
                    SubtreeOps.ArrayDelete(treePool, ref pop.Get(i).Subtrees);
                }

                while (stack.VersionCount > pop.Get(0).Version + 1)
                {
                    stack.RemoveVersion(pop.Get(0).Version + 1);
                }
            }

            stack.RenumberVersion(pop.Get(0).Version, version);
            pop.Get(0).Subtrees.Push(errorRepeat);
            errorRepeat = SubtreeOps.NewNode(
                BuiltinSymbols.ErrorRepeat,
                ref pop.Get(0).Subtrees,
                0,
                Lang);
        }

        // Push the new ERROR onto the stack.
        stack.Push(version, errorRepeat, false, ErrorCosts.ErrorState);
        if (lookahead.HasExternalTokens)
        {
            stack.SetLastExternalToken(version, SubtreeOps.LastExternalToken(lookahead));
        }

        bool newHasError = true;
        for (uint i = 0; i < stack.VersionCount; i++)
        {
            ErrorStatus status = VersionStatus(i);
            if (!status.IsInError)
            {
                newHasError = false;
                break;
            }
        }

        hasError = newHasError;
    }

    /// <summary>ts_parser__handle_error</summary>
    private void HandleError(uint version, Subtree lookahead)
    {
        uint previousVersionCount = stack.VersionCount;

        // Perform any reductions that can happen in this state, regardless of the lookahead. After
        // skipping one or more invalid tokens, the parser might find a token that would have allowed
        // a reduction to take place.
        DoAllPotentialReductions(version, 0);
        uint versionCount = stack.VersionCount;
        Length position = stack.Position(version);

        // Push a discontinuity onto the stack. Merge all of the stack versions that
        // were created in the previous step.
        bool didInsertMissingToken = false;
        for (uint v = version; v < versionCount;)
        {
            if (!didInsertMissingToken)
            {
                ushort state = stack.State(v);
                for (
                    ushort missingSymbol = 1;
                    missingSymbol < (ushort)Lang.Tables.TokenCount;
                    missingSymbol++)
                {
                    ushort stateAfterMissingSymbol = Lang.NextState(state, missingSymbol);
                    if (stateAfterMissingSymbol == 0 || stateAfterMissingSymbol == state)
                    {
                        continue;
                    }

                    if (Lang.HasReduceAction(
                        stateAfterMissingSymbol,
                        lookahead.LeafSymbol))
                    {
                        // In case the parser is currently outside of any included range, the lexer will
                        // snap to the beginning of the next included range. The missing token's padding
                        // must be assigned to position it within the next included range.
                        lexer.Reset(position);
                        lexer.MarkEndInternal();
                        Length padding = Length.Sub(lexer.TokenEndPosition, position);
                        uint lookaheadBytes = lookahead.TotalBytes + lookahead.LookaheadBytes;

                        uint versionWithMissingTree = stack.CopyVersion(v);
                        Subtree missingTree = SubtreeOps.NewMissingLeaf(
                            treePool,
                            missingSymbol,
                            state,
                            padding,
                            lookaheadBytes,
                            Lang);
                        stack.Push(
                            versionWithMissingTree,
                            missingTree,
                            false,
                            stateAfterMissingSymbol);

                        if (DoAllPotentialReductions(
                            versionWithMissingTree,
                            lookahead.LeafSymbol))
                        {
                            if (IsLogging)
                            {
                                Log(Invariant(
                                    $"recover_with_missing symbol:{SymName(missingSymbol)}, state:{stack.State(versionWithMissingTree)}"));
                            }

                            didInsertMissingToken = true;
                            break;
                        }
                    }
                }
            }

            stack.Push(v, Subtree.Null, false, ErrorCosts.ErrorState);
            v = (v == version) ? previousVersionCount : v + 1;
        }

        for (uint i = previousVersionCount; i < versionCount; i++)
        {
            bool didMerge = stack.Merge(version, previousVersionCount);
            Debug.Assert(didMerge, "the versions of the error did not merge");
        }

        stack.RecordSummary(version, MaxSummaryDepth);

        // Begin recovery with the current lookahead node, rather than waiting for the
        // next turn of the parse loop. This ensures that the tree accounts for the
        // current lookahead token's "lookahead bytes" value, which describes how far
        // the lexer needed to look ahead beyond the content of the token in order to
        // recognize it.
        if (lookahead.ChildCount > 0)
        {
            BreakdownLookahead(ref lookahead, ErrorCosts.ErrorState, ref reusableNode);
        }

        Recover(version, lookahead);
        LogStack();
    }

    /// <summary>ts_parser__check_progress (without lookahead or position)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CheckProgress(uint operations)
    {
        uint nextCount = operationCount + operations;
        if (nextCount < OpCountPerParserCallbackCheck)
        {
            operationCount = nextCount;
            return true;
        }

        operationCount = 0;
        return parseOptions?.ProgressCallback is not { } callback || !callback(parseState);
    }

    /// <summary>ts_parser__check_progress</summary>
    private bool CheckProgress(ref Subtree lookahead, bool hasLookahead, uint? position, uint operations)
    {
        operationCount += operations;
        if (operationCount >= OpCountPerParserCallbackCheck)
        {
            operationCount = 0;
        }

        if (position is not null)
        {
            parseState.CurrentByteOffset = position.Value;
            parseState.HasError = hasError;
        }

        if (
            operationCount == 0 &&
            (parseOptions?.ProgressCallback is not null && parseOptions.ProgressCallback(parseState)))
        {
            if (hasLookahead && !lookahead.IsNull)
            {
                SubtreeOps.Release(treePool, lookahead);
            }

            return false;
        }

        return true;
    }

    /// <summary>ts_parser__advance</summary>
    private bool Advance(uint version, bool allowNodeReuse)
    {
        ushort state = stack.State(version);
        uint position = stack.Position(version).Bytes;
        Subtree lastExternalToken = stack.LastExternalToken(version);

        bool didReuse = true;
        Subtree lookahead = Subtree.Null;
        TableEntry tableEntry = new TableEntry(0, 0, false);

        // If possible, reuse a node from the previous syntax tree.
        if (allowNodeReuse)
        {
            lookahead = ReuseNode(version, ref state, position, lastExternalToken, ref tableEntry);
        }

        // If no node from the previous syntax tree could be reused, then try to
        // reuse the token previously returned by the lexer.
        if (lookahead.IsNull)
        {
            didReuse = false;
            lookahead = GetCachedToken(state, position, lastExternalToken, ref tableEntry);
        }

        bool needsLex = lookahead.IsNull;
        for (;;)
        {
            // Otherwise, re-run the lexer.
            if (needsLex)
            {
                needsLex = false;
                lookahead = Lex(version, state);
                if (hasScannerError)
                {
                    return false;
                }

                if (!lookahead.IsNull)
                {
                    SetCachedToken(position, lastExternalToken, lookahead);
                    Lang.GetTableEntry(state, lookahead.Symbol, out tableEntry);
                }

                // When parsing a non-terminal extra, a null lookahead indicates the
                // end of the rule. The reduction is stored in the EOF table entry.
                // After the reduction, the lexer needs to be run again.
                else
                {
                    Lang.GetTableEntry(state, BuiltinSymbols.End, out tableEntry);
                }
            }

            // If a progress callback was provided, then check every
            // time a fixed number of parse actions has been processed.
            if (!CheckProgress(ref lookahead, true, position, 1))
            {
                return false;
            }

            // Process each parse action for the current lookahead token in
            // the current state. If there are multiple actions, then this is
            // an ambiguous state. REDUCE actions always create a new stack
            // version, whereas SHIFT actions update the existing stack version
            // and terminate this loop.
            bool didReduce = false;
            uint lastReductionVersion = Stack.VersionNone;
            for (uint i = 0; i < tableEntry.ActionCount; i++)
            {
                ParseActionEntry action = Lang.Tables.ParseActions![tableEntry.ActionsStart + (int)i];

                switch (action.Type)
                {
                    case ParseActionType.Shift:
                    {
                        if (action.ShiftRepetition)
                        {
                            break;
                        }

                        ushort nextState;
                        if (action.ShiftExtra)
                        {
                            nextState = state;
                            if (IsLogging)
                            {
                                Log("shift_extra");
                            }
                        }
                        else
                        {
                            nextState = action.ShiftState;
                            if (IsLogging)
                            {
                                Log(Invariant($"shift state:{nextState}"));
                            }
                        }

                        if (lookahead.ChildCount > 0)
                        {
                            BreakdownLookahead(ref lookahead, state, ref reusableNode);
                            nextState = Lang.NextState(state, lookahead.Symbol);
                        }

                        Shift(version, nextState, lookahead, action.ShiftExtra);
                        if (didReuse)
                        {
                            reusableNode.Advance();
                        }

                        return true;
                    }

                    case ParseActionType.Reduce:
                    {
                        bool isFragile = tableEntry.ActionCount > 1;
                        bool endOfNonTerminalExtra = lookahead.IsNull;
                        if (IsLogging)
                        {
                            Log(Invariant(
                                $"reduce sym:{SymName(action.ReduceSymbol)}, child_count:{action.ReduceChildCount}"));
                        }

                        uint reductionVersion = Reduce(
                            version,
                            action.ReduceSymbol,
                            action.ReduceChildCount,
                            action.ReduceDynamicPrecedence,
                            action.ReduceProductionId,
                            isFragile,
                            endOfNonTerminalExtra);
                        didReduce = true;
                        if (reductionVersion != Stack.VersionNone)
                        {
                            lastReductionVersion = reductionVersion;
                        }

                        break;
                    }

                    case ParseActionType.Accept:
                    {
                        if (IsLogging)
                        {
                            Log("accept");
                        }

                        Accept(version, lookahead);
                        return true;
                    }

                    case ParseActionType.Recover:
                    {
                        if (lookahead.ChildCount > 0)
                        {
                            BreakdownLookahead(ref lookahead, ErrorCosts.ErrorState, ref reusableNode);
                        }

                        Recover(version, lookahead);
                        if (didReuse)
                        {
                            reusableNode.Advance();
                        }

                        return true;
                    }
                }
            }

            // If a reduction was performed, then replace the current stack version
            // with one of the stack versions created by a reduction, and continue
            // processing this version of the stack with the same lookahead symbol.
            if (lastReductionVersion != Stack.VersionNone)
            {
                stack.RenumberVersion(lastReductionVersion, version);
                LogStack();
                state = stack.State(version);

                // At the end of a non-terminal extra rule, the lexer will return a
                // null subtree, because the parser needs to perform a fixed reduction
                // regardless of the lookahead node. After performing that reduction,
                // (and completing the non-terminal extra rule) run the lexer again based
                // on the current parse state.
                if (lookahead.IsNull)
                {
                    needsLex = true;
                }
                else
                {
                    Lang.GetTableEntry(
                        state,
                        lookahead.LeafSymbol,
                        out tableEntry);
                }

                continue;
            }

            // A reduction was performed, but was merged into an existing stack version.
            // This version can be discarded.
            if (didReduce)
            {
                if (!lookahead.IsNull)
                {
                    SubtreeOps.Release(treePool, lookahead);
                }

                stack.Halt(version);
                return true;
            }

            // If the current lookahead token is a keyword that is not valid, but the
            // default word token *is* valid, then treat the lookahead token as the word
            // token instead.
            if (
                lookahead.IsKeyword &&
                lookahead.Symbol != Lang.Tables.KeywordCaptureToken &&
                !Lang.IsReservedWord(state, lookahead.Symbol))
            {
                Lang.GetTableEntry(
                    state,
                    Lang.Tables.KeywordCaptureToken,
                    out tableEntry);
                if (tableEntry.ActionCount > 0)
                {
                    if (IsLogging)
                    {
                        Log("switch from_keyword:" + TreeName(lookahead) + ", to_word_token:" +
                            SymName(Lang.Tables.KeywordCaptureToken));
                    }

                    Subtree mutableLookahead = SubtreeOps.MakeMut(treePool, lookahead);
                    SubtreeOps.SetSymbol(ref mutableLookahead, Lang.Tables.KeywordCaptureToken, Lang);
                    lookahead = mutableLookahead;
                    continue;
                }
            }

            // If the current lookahead token is not valid and the parser is
            // already in the error state, restart the error recovery process.
            if (state == ErrorCosts.ErrorState)
            {
                Recover(version, lookahead);
                return true;
            }

            // If the current lookahead token is not valid and the previous subtree on
            // the stack was reused from an old tree, then it wasn't actually valid to
            // reuse that previous subtree. Remove it from the stack, and in its place,
            // push each of its children. Then try again to process the current lookahead.
            if (BreakdownTopOfStack(version))
            {
                state = stack.State(version);
                SubtreeOps.Release(treePool, lookahead);
                needsLex = true;
                continue;
            }

            // Otherwise, there is definitely an error in this version of the parse stack.
            // Mark this version as paused and continue processing any other stack
            // versions that exist. If some other version advances successfully, then
            // this version can simply be removed. But if all versions end up paused,
            // then error recovery is needed.
            if (IsLogging)
            {
                Log("detect_error lookahead:" + TreeName(lookahead));
            }

            stack.Pause(version, lookahead);
            return true;
        }
    }

    /// <summary>ts_parser__condense_stack</summary>
    private uint CondenseStack()
    {
        bool madeChanges = false;
        uint minErrorCost = uint.MaxValue;
        for (uint i = 0; i < stack.VersionCount; i++)
        {
            // Prune any versions that have been marked for removal.
            if (stack.IsHalted(i))
            {
                stack.RemoveVersion(i);
                i--;
                continue;
            }

            // Keep track of the minimum error cost of any stack version so
            // that it can be returned.
            ErrorStatus statusI = VersionStatus(i);
            if (!statusI.IsInError && statusI.Cost < minErrorCost)
            {
                minErrorCost = statusI.Cost;
            }

            // Examine each pair of stack versions, removing any versions that
            // are clearly worse than another version. Ensure that the versions
            // are ordered from most promising to least promising.
            for (uint j = 0; j < i; j++)
            {
                ErrorStatus statusJ = VersionStatus(j);

                switch (CompareVersions(statusJ, statusI))
                {
                    case ErrorComparison.TakeLeft:
                        madeChanges = true;
                        stack.RemoveVersion(i);
                        i--;
                        j = i;
                        break;

                    case ErrorComparison.PreferLeft:
                    case ErrorComparison.None:
                        if (stack.Merge(j, i))
                        {
                            madeChanges = true;
                            i--;
                            j = i;
                        }

                        break;

                    case ErrorComparison.PreferRight:
                        madeChanges = true;
                        if (stack.Merge(j, i))
                        {
                            i--;
                            j = i;
                        }
                        else
                        {
                            stack.SwapVersions(i, j);
                        }

                        break;

                    case ErrorComparison.TakeRight:
                        madeChanges = true;
                        stack.RemoveVersion(j);
                        i--;
                        j--;
                        break;
                }
            }
        }

        // Enforce a hard upper bound on the number of stack versions by
        // discarding the least promising versions.
        while (stack.VersionCount > MaxVersionCount)
        {
            stack.RemoveVersion(MaxVersionCount);
            madeChanges = true;
        }

        // If the best-performing stack version is currently paused, or all
        // versions are paused, then resume the best paused version and begin
        // the error recovery process. Otherwise, remove the paused versions.
        if (stack.VersionCount > 0)
        {
            bool hasUnpausedVersion = false;
            for (uint i = 0, n = stack.VersionCount; i < n; i++)
            {
                if (stack.IsPaused(i))
                {
                    if (!hasUnpausedVersion && acceptCount < MaxVersionCount)
                    {
                        if (IsLogging)
                        {
                            Log(Invariant($"resume version:{i}"));
                        }

                        minErrorCost = stack.ErrorCost(i);
                        Subtree lookahead = stack.Resume(i);
                        HandleError(i, lookahead);
                        hasUnpausedVersion = true;
                    }
                    else
                    {
                        stack.RemoveVersion(i);
                        madeChanges = true;
                        i--;
                        n--;
                    }
                }
                else
                {
                    hasUnpausedVersion = true;
                }
            }
        }

        if (madeChanges)
        {
            if (IsLogging)
            {
                Log("condense");
            }

            LogStack();
        }

        return minErrorCost;
    }

    /// <summary>ts_parser__balance_subtree</summary>
    private bool BalanceSubtree()
    {
        ref TsArray<Subtree> treeStack = ref treePool.TreeStack;

        // If we haven't canceled balancing in progress before, then we want to clear the tree stack and
        // push the initial finished tree onto it. Otherwise, if we're resuming balancing after a
        // cancellation, we don't want to clear the tree stack.
        if (!canceledBalancing)
        {
            treeStack.Clear();
            Subtree finished = finishedTree;
            if (!finished.IsNull && (finished.Value & 1UL) == 0)
            {
                ref SubtreeHeapData finishedHeap = ref SubtreeArena.Get(finished);
                if (finishedHeap.ChildCount > 0 && finishedHeap.RefCount == 1)
                {
                    treeStack.Push(finished);
                }
            }
        }

        Language lang = Lang;
        while (treeStack.Size > 0)
        {
            if (!CheckProgress(1))
            {
                return false;
            }

            Subtree tree = treeStack.Back();
            ref SubtreeHeapData treeHeap = ref SubtreeArena.Get(tree);
            uint childCount = treeHeap.ChildCount;
            Subtree[] treeChildren = SubtreeArena.GetChildren(treeHeap.ChildrenHandle);

            if (treeHeap.RepeatDepth > 0)
            {
                ref Subtree childrenBase0 = ref MemoryMarshal.GetArrayDataReference(treeChildren);
                Subtree child1 = childrenBase0;
                Subtree child2 = Unsafe.Add(ref childrenBase0, (nint)(childCount - 1));
                long repeatDelta = (long)child1.RepeatDepth - (long)child2.RepeatDepth;
                if (repeatDelta > 0)
                {
                    uint n = (uint)repeatDelta;

                    for (uint i = n / 2; i > 0; i /= 2)
                    {
                        SubtreeOps.Compress(tree, i, lang, ref treeStack);
                        n -= i;

                        // We scale the operation count increment in `ts_parser__check_progress` proportionately to the compression
                        // size since larger values of i take longer to process. Shifting by 4 empirically provides good check
                        // intervals (e.g. 193 operations when i=3100) to prevent blocking during large compressions.
                        byte operations = (byte)((i >> 4) > 0 ? (i >> 4) : 1);
                        if (!CheckProgress(operations))
                        {
                            return false;
                        }
                    }
                }
            }

            treeStack.Size--;

            ref Subtree childrenBase = ref MemoryMarshal.GetArrayDataReference(treeChildren);
            for (uint i = 0; i < childCount; i++)
            {
                Subtree child = Unsafe.Add(ref childrenBase, (nint)i);
                if ((child.Value & 1UL) == 0)
                {
                    ref SubtreeHeapData childHeap = ref SubtreeArena.Get(child);
                    if (childHeap.ChildCount > 0 && childHeap.RefCount == 1)
                    {
                        treeStack.Push(child);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>ts_parser_has_outstanding_parse</summary>
    private bool HasOutstandingParse() =>
        canceledBalancing ||
        externalScannerPayload is not null ||
        stack.State(0) != 1 ||
        stack.NodeCountSinceError(0) != 0;

    /// <summary>The actions of a table entry (TableEntry.actions).</summary>
    private ReadOnlySpan<ParseActionEntry> Actions(in TableEntry entry) =>
        Lang.Tables.ParseActions.AsSpan(entry.ActionsStart, (int)entry.ActionCount);
}
