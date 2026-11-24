# TreeSitter.Managed - Architecture

Status: M0 draft. This document maps the upstream C runtime to C# types and fixes the
main design decisions before code is written.

## 1. Reference version

- Upstream: https://github.com/tree-sitter/tree-sitter, tag `v0.27.2`,
  commit `3fb7be49145acfb07e33b61ed5689e2b6771b2e9` (2026-11-07). See `UPSTREAM.md`.
- Language ABI: only ABI 15 is supported at first. Every bundled grammar is regenerated
  with the `tree-sitter` CLI of the same tag (`tree-sitter generate`), so `ts2cs` sees one
  input format. Grammar repos often ship parser.c from older CLIs (tree-sitter-json ships
  ABI 14 today). ABI 13/14 support can be added later if users need to convert old files.
- Runtime size to port (lines, without `wasm_store.c`):

  | File | Lines | File | Lines |
  |---|---|---|---|
  | query.c | 4881 | tree_cursor.c | 733 |
  | parser.c | 2315 | get_changed_ranges.c | 557 |
  | subtree.c | 1096 | lexer.c | 511 |
  | stack.c | 912 | language.c | 394 |
  | node.c | 870 | tree.c | 182 |

  Headers with real logic: `subtree.h` (399), `array.h` (336), `language.h` (313),
  `parser.h` (286, grammar ABI), `reusable_node.h` (95), `unicode.h` + `unicode/utf8.h` (ICU).

## 2. Assemblies and layout

```
src/TreeSitter.Managed/             runtime (net8.0; net10.0)
src/TreeSitter.Managed.Generator/   ts2cs console tool (net10.0)
grammars/<Name>/                    generated code + ported scanner + queries, one package each
tests/TreeSitter.Managed.Tests/     unit + oracle + corpus + fuzz tests
tests/Oracle/                       native harness: C runtime + grammars, test only
bench/                              BenchmarkDotNet
docs/
```

- Targets: net8.0 and net10.0. netstandard2.1 is dropped: generated tables need
  `RuntimeHelpers.CreateSpan` (net7+), and the runtime uses `InlineArray` (C# 12).
- `IsTrimmable`, `IsAotCompatible` set on runtime and grammar projects from day one.
  A CI smoke job publishes a tiny NativeAOT console app and a browser-wasm app that parse
  JSON. This catches reflection or trimming regressions early, not at the end.
- Safe code by default. `unsafe` only where a benchmark shows a real gain.

## 3. Core decisions

### D1. Subtree: struct handle, GC memory, explicit ref count

C `Subtree` is a union of an 8-byte inline leaf (`SubtreeInlineData`) and a pointer to
`SubtreeHeapData`. The low bit tells which one it is. Children are stored in memory right
before the heap header.

C#:

```csharp
internal readonly struct Subtree        // 16 bytes
{
    internal readonly ulong Inline;     // SubtreeInlineData bits, bit 0 = is_inline
    internal readonly SubtreeHeap? Heap;// null for inline leaves and for NULL_SUBTREE
}

internal sealed class SubtreeHeap       // SubtreeHeapData
{
    internal int RefCount;              // changed only with Interlocked
    internal Length Padding, Size;
    internal uint LookaheadBytes, ErrorCost, ChildCount;
    internal ushort Symbol, ParseState;
    internal SubtreeFlags Flags;        // the 11 bit fields
    internal Subtree[]? Children;       // length >= ChildCount (C capacity semantics)
    // Union part: kept as separate fields, not overlapped. Only one group is meaningful,
    // exactly as in C (non-terminal / external token / error leaf).
    internal uint VisibleChildCount, NamedChildCount, VisibleDescendantCount;
    internal int DynamicPrecedence;
    internal ushort RepeatDepth, ProductionId, FirstLeafSymbol, FirstLeafParseState;
    internal ExternalScannerState ExternalScannerState;
    internal int LookaheadChar;
}
```

- `NULL_SUBTREE` = `Heap == null && (Inline & 1) == 0`.
- The inline bit layout copies the little-endian C layout. It is internal, so endianness
  does not matter, but keeping the same layout makes debugging against native easier.
- Memory is freed by the GC. The ref count is kept because behavior depends on it:
  `ts_subtree_make_mut` mutates in place when `ref_count == 1` and copies otherwise;
  `ts_subtree_compress` and `ts_parser__balance_subtree` only rebalance nodes with
  `ref_count == 1`. Without the count the internal tree shape would differ from C.
  Retain/release calls are ported 1:1 at the same places as in C.
- `ts_subtree_release` in C pushes freed nodes into `SubtreePool.free_trees` for reuse.
  We do not recycle `SubtreeHeap` objects at first: a stale `Node` from a disposed tree
  would then silently point at a reused object. Recycling is a later benchmark decision.
- `Tree` implements `IDisposable`: `Dispose` releases the root (as `ts_tree_delete`).
  If the user never disposes, nothing leaks; the only effect is that later edits of a
  shared subtree copy instead of mutating in place. No finalizer (it would run on another
  thread and race with the parser). This is documented in the API docs.

### D2. Node identity

`TSNode.id` in C is the address of a `Subtree` slot: `&tree->root` or `&children[i]`
(see `tree.c:56`, `node.c:96`, `tree_cursor.c:505`). Equality (`ts_node_eq`) and
"is this the same child" checks (`node.c:207`, `node.c:550-575`, `query.c:3671`) compare
these addresses. C# keeps the same identity:

```csharp
public readonly struct Node
{
    internal readonly Tree Tree;
    internal readonly object? Owner;    // SubtreeHeap (child slot) or Tree (root slot)
    internal readonly int Slot;         // index in Owner.Children, 0 for root
    internal readonly uint StartByte, StartRow, StartColumn; // context[0..2]
    internal readonly ushort Alias;     // context[3]
}
```

`Subtree` is read as `Owner.Children[Slot]` (or `tree.Root`). Equality = same `Tree`,
`Owner` and `Slot`. Size is 32 bytes on 64-bit, the same as `TSNode`.

### D3. Language and tables

- `Language` is an immutable sealed class. It is shared by all threads.
- The runtime reads tables through arrays (`ushort[]`, `uint[]`, struct arrays). It cannot
  keep `ReadOnlySpan<T>` over RVA data in a class without `unsafe`, so arrays are needed
  anyway. The open question is only how the generated assembly fills them:
  - (a) array initializers / `ReadOnlySpan<T>` literals (RVA data + copy),
  - (b) one embedded resource blob, optionally compressed (Deflate/Brotli from
    `System.IO.Compression`, works in AOT and wasm), decoded on first use.
- Decision rule (measured in M2, not guessed): pick (a) if Roslyn compile time and memory
  stay reasonable for the largest grammar (tree-sitter-c-sharp parser.c is 39.7 MB,
  tree-sitter-cpp 25.9 MB, TypeScript 8.7 MB); otherwise (b). JSON in M1 uses (a) because
  it is simpler to debug. `ts2cs` keeps both emitters behind one switch until M2 ends.
- Literal emitter (M1a): numeric tables are `ReadOnlySpan<T>` RVA literals; struct tables are
  packed into primitives (for example 4 x `ushort` per parse action) and unpacked by
  `TreeSitter.Abi.TableDecoder`, so the data does not depend on the byte order. Strings are
  C# literals. `<Name>Grammar.Language` is a lazy singleton over `CreateTables()`.
- Parse actions (`TSParseActionEntry` union) become a struct with explicit fields, packed
  the same way as C (`type`, `state`/`child_count`, `symbol`, `dynamic_precedence`,
  `production_id`, `extra`, `repetition`; entry header `count`, `reusable`).
- The table types (`LanguageTables`, `ParseActionEntry`, `LexerMode`, `SymbolMetadata`,
  `FieldMapEntry`, `MapSlice`, `CharacterRange`, `LanguageMetadata`) are public in the
  `TreeSitter.Abi` namespace and hidden from IntelliSense. They play the role of the public
  `parser.h` in C: generated grammar assemblies must create them. `ParseActionEntry` uses
  an explicit struct layout that mirrors the C union.
- `language.c` / `language.h` (table lookup, `ts_language_table_entry`,
  `ts_language_lookup`, `ts_language_next_state`, alias and field maps, reserved words,
  supertypes, `LookaheadIterator`) are ported 1:1 as methods on `Language`.

### D4. Generated lexer functions

C lexers are a `switch (state)` inside a `goto next_state` loop (`START_LEXER`,
`ADVANCE`, `ADVANCE_MAP`, `SKIP`, `ACCEPT_TOKEN`, `END_STATE`, `set_contains`).

- M1 emits C#: the `next_state` / `start` labels of `START_LEXER` around one `switch (state)`,
  each `ADVANCE` / `SKIP` is `state = n; goto next_state;` (Lex/LexEmitter.cs).
  `ADVANCE_MAP` becomes a static `ReadOnlySpan<ushort>` per call site with the same linear
  search. `set_contains` becomes a shared helper with the same binary search.
  Condition order is copied as is: the first matching branch wins, as in C.
- Risk: RyuJIT compiles methods above about 60 KB of IL with MinOpts (no optimization).
  Measured in M1b: `ts_lex` of C is 19 KB of IL, its keyword lexer 13 KB, Python 11 KB.
  Bigger grammars (M6) may exceed the limit. M2 compares two fixes:
  - split the switch: each chunk of states is its own method, returning a small result
    (next state + skip flag, or accept/end), the loop stays in one driver method;
  - a table-driven DFA.
- Hard rule for the table option: the table must be lossless. gotreesitter's DFA blob
  loses part of the C source and needs marked heuristics where it differs from C
  (end of input, lookahead -1, end accept). We do not accept any such difference.
- `keyword_lex_fn` uses the same emitter.

### D5. Lexer and input

- `Lexer` is one public sealed class, a 1:1 port of `lexer.c`. Generated lex functions
  and external scanners see only its public members (`Lookahead`, `ResultSymbol`,
  `Advance(bool skip)`, `MarkEnd()`, `GetColumn()`, `IsAtIncludedRangeStart`, `Eof`,
  `Log(...)`); the parser uses internal members. Sealed, no interface, so calls devirtualize.
- Decoding copies ICU `U8_NEXT` (`unicode/utf8.h`) and `U16_NEXT` exactly, including
  `TS_DECODE_ERROR = -1`, the size<4 retry at chunk ends (`lexer.c:133`) and the BOM rule
  (`lexer.c:215`, `lexer.c:439`). `System.Text.Rune` is not used: its error handling is
  not guaranteed to match ICU byte for byte.
- Positions are bytes, also for UTF-16 input (2 bytes per code unit), and columns are
  bytes, as in C.
- Input API. The lexer keeps the current chunk between calls, so it cannot hold a
  `ReadOnlySpan<T>`. Public overloads:
  - `Parse(ReadOnlyMemory<byte> utf8, Tree? old)` and `byte[]`;
  - `Parse(string / ReadOnlyMemory<char> utf16, Tree? old)`: a separate UTF-16 path in
    the lexer, no conversion copy;
  - `Parse(ReadOnlySpan<byte>)` / `Parse(ReadOnlySpan<char>)`: convenience, copies into a
    pooled buffer first (a copy is far cheaper than a parse);
  - `Parse(IParseInput input, Tree? old)`: callback reader for large documents, returns
    `ReadOnlyMemory<byte>` chunks for a byte index + point (the `TSInput.read` contract),
    with `InputEncoding` UTF8 / UTF16LE / UTF16BE / Custom (decode delegate).
- `TSParseOptions.progress_callback` -> `ParseOptions.ProgressCallback`
  (`Func<ParseState, bool>`), plus a `CancellationToken` overload built on top of it.

### D6. Parse stack

`stack.c` is used by one parser on one thread. Port it 1:1 with a node pool:

- `StackNode` is a class with manual ref count (not atomic, as in C), `links` as
  `[InlineArray(8)] StackLinkArray` (`MAX_LINK_COUNT`), pool of `MAX_NODE_POOL_SIZE = 50`
  exactly as C. Here recycling is safe: stack nodes never escape the parser.
- `StackHead`, `StackIterator`, `StackSlice`, `StackSummary` are structs in growable
  arrays. Iteration order and version numbering must stay identical, because error
  recovery and `ts_parser__condense_stack` depend on the order of versions.

### D7. Arrays

`array.h` is a macro library (`Array(T)` with size/capacity, `array_push`, `array_splice`,
`array_search_sorted_by`, ...). Port as one internal `struct ArrayBuilder<T>`-like type
`TsArray<T>` with the same operations and the same growth policy (growth affects only
speed, but keeping it makes allocation profiles comparable). Passed by `ref`.

### D8. External scanners

```csharp
public interface IExternalScanner
{
    bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols);
    int Serialize(Span<byte> buffer);   // buffer length = 1024 (TREE_SITTER_SERIALIZATION_BUFFER_SIZE)
    void Deserialize(ReadOnlySpan<byte> buffer);
}
// Create = factory delegate on Language; Destroy = IDisposable when the scanner owns
// resources, otherwise nothing.
```

- Serialized bytes must be identical to the C scanner for the same state. The oracle
  harness exposes the native scanner state of each external token, so this is tested.
- `ExternalScannerState` keeps the C short/long split: a `[InlineArray(24)]` short buffer
  plus a `byte[]` for longer states, with the same equality and copying rules.

### D9. Queries

- `query.c` is ported in M4 as `Query` (parser of S-expression patterns + analysis) and
  `QueryCursor` (matching state machine). It is the largest file, but it depends only on
  `Node`, `TreeCursor` and `Language`.
- The C runtime only parses predicates (`#eq?`, `#match?`, `#any-of?`, `#set!`, ...) and
  returns them as steps; each binding evaluates them. We implement evaluation following the
  Rust binding (`lib/binding_rust/lib.rs`), which is what the CLI and highlight tests use.
  `#match?` uses Rust `regex` syntax there; we use .NET `Regex` with
  `RegexOptions.CultureInvariant` and list known syntax differences in the docs and tests.
  Regexes are built without `RegexOptions.Compiled` by default (AOT/wasm friendly).

### D10. Logging and debug output

- `TSLogger` is ported with the same message texts and the same call sites.
  This is a key test tool (section 6).
- `ts_subtree_print_dot_graph` / `ts_stack_print_dot_graph`: ported last, debug only,
  behind a `TextWriter` parameter.

## 4. File mapping

| Upstream | C# type(s) | Notes |
|---|---|---|
| `api.h` | public types: `Parser, Tree, Node, TreeCursor, Query, QueryCursor, QueryMatch, QueryCapture, Language, LookaheadIterator, InputEdit, Point, Range, InputEncoding, ParseOptions, ParseState, LogType` | PascalCase names, same members |
| `parser.h` | `TreeSitter.Abi`: `LanguageTables`, `ParseActionEntry`, `LexerMode`, `SymbolMetadata`, `FieldMapEntry`, `MapSlice`, `CharacterRange` | the grammar ABI, used by generated code |
| `alloc.c/h` | none | GC; allocator hooks dropped |
| `array.h` | `TsArray<T>` | D7 |
| `atomic.h` | `Interlocked` | |
| `clock.h` | none | the timeout API is already removed upstream in v0.27.2 |
| `point.c/h` | `Point` (public struct) + operators | |
| `length.h` | `Length` (internal struct) | |
| `error_costs.h` | `ErrorCosts` constants | |
| `host.h` | none | |
| `unicode.h`, `unicode/*.h` | `Unicode` static class | ICU macros ported, ICU license in notices |
| `subtree.c/h` | `Subtree`, `SubtreeHeap`, `SubtreePool`, `ExternalScannerState`, `SubtreeOps` | D1 |
| `language.c/h` | `Language`, `LookaheadIterator`, `TableEntry` | D3 |
| `lexer.c/h` | `Lexer` (public sealed, internal members for the parser) | D5 |
| `stack.c/h` | `Stack`, `StackNode`, `StackHead`, `StackSlice`, `StackSummaryEntry` | D6 |
| `reduce_action.h` | `ReduceAction`, `ReduceActionSet` | |
| `reusable_node.h` | `ReusableNode` (struct) | |
| `parser.c` | `Parser` | 1:1 functions, same names in camel case (`ts_parser__recover` -> `Recover`) |
| `tree.c/h` | `Tree` | `IDisposable` (D1) |
| `node.c` | `Node` | D2 |
| `tree_cursor.c/h` | `TreeCursor` (public class, `Copy`, `Reset`) | |
| `get_changed_ranges.c/h` | `ChangedRanges` (internal static) | |
| `query.c` | `Query`, `QueryCursor`, internal `QueryStep`, `QueryState`, `CaptureListPool`, analysis types | D9 |
| `wasm_store.c`, `wasm/` | out of scope | |

Each ported file starts with a comment naming the upstream file and the tag/commit.
Internal method names keep the C name in a `// ts_parser__recover` comment above them,
so a search for the C name finds the port.

## 5. ts2cs converter

Input: `src/parser.c` (ABI 15, from the pinned CLI), `src/node-types.json`, optional
`src/scanner.c` (only to check the external token list), `queries/*.scm`, `LICENSE`.

parser.c is machine generated with fixed patterns, so ts2cs uses a small purpose-built
reader (C tokenizer + recognizers for the known declarations), not a general C parser.
Any unknown construct is a hard error with file and line, never a silent skip.

Recognized blocks: `#define` counts, `enum ts_symbol_identifiers`, `ts_symbol_names`,
`ts_symbol_map`, `ts_symbol_metadata`, `ts_field_names`, `ts_field_map_slices`,
`ts_field_map_entries`, `ts_alias_sequences`, `ts_non_terminal_alias_map`,
`ts_primary_state_ids`, `ts_lex`, `ts_lex_keywords`, `ts_lex_modes`, `ts_parse_table`,
`ts_small_parse_table`, `ts_small_parse_table_map`, `ts_parse_actions`,
`ts_reserved_words`, supertype tables, `ts_external_scanner_states`,
`ts_external_scanner_symbol_map`, the `TSLanguage` initializer, metadata, character set
arrays used by `set_contains`.

Output per grammar: `<Name>Language.g.cs` (tables + `Language` factory),
`<Name>Lexer.g.cs` (lex functions), `.csproj` with queries as embedded resources.
The external scanner is a hand-written `<Name>Scanner.cs` next to it.

Self check: ts2cs can dump every table as text; the oracle harness dumps the same tables
from the native `TSLanguage` struct. Both dumps must be equal (test in M1).

## 6. Testing

"Done" means: oracle tests pass and coverage targets are met.

### 6.1 Oracle harness

- `tests/Oracle`: a C shim + the pinned runtime (`lib/src/lib.c`) + the same regenerated
  grammar sources, built with clang or MSVC into one native DLL per platform. Test only,
  never shipped. Built by a script; CI caches the DLL.
- TreeSitter.DotNet is not used as the oracle: it pins runtime commit `cd4b6e2`
  (January 2026) and ships its own grammar sources, so differences would be noise.
- The shim exposes: parse (UTF-8/16, with old tree + edits), `ts_node_string`, a full
  node walk (symbol, alias, field, byte range, points, flags), changed ranges, logger
  messages, external scanner states, table dumps, query matches/captures.
- The shim also runs a *subtree script* (`oracle_subtree_script`, format in
  `tests/Oracle/FORMATS.md`): a small command language that calls the `subtree.c`
  functions of the pinned runtime (leaf, error, missing, node, clone, release,
  summarize, S-expression). The managed port of `subtree.c` runs the same script
  and must print the same text, so every part of the port is compared with the
  native one, including the reference counts and the summaries.

### 6.2 Test layers

1. Unit tests for leaf types (Length, Point, TsArray, Unicode decode incl. invalid bytes).
2. Table equality: ts2cs dump == native `TSLanguage` dump, per grammar.
3. Logger equality: the full parser log (lex, shift, reduce, recover, skip, condense)
   compared line by line with native for each input. This finds the first point of
   divergence, which an S-expression diff cannot.
4. Tree equality: `ts_node_string` byte for byte + full node walk equality.
5. Corpus: every `test/corpus/*.txt` of each grammar, plus upstream
   `test/fixtures/error_corpus` and `test/fixtures/test_grammars`.
6. Upstream runtime tests: port the relevant parts of `crates/cli/src/tests/`
   (`parser_test.rs`, `node_test.rs`, `tree_test.rs`, `query_test.rs`,
   `language_test.rs`, `pathological_test.rs`; about 14.6K lines in total) to xUnit.
   These encode upstream's own expectations and edge cases.
7. Incremental fuzzing, same scheme as upstream `corpus_test.rs`: random edits on corpus
   inputs, reparse with old tree, compare with a fresh parse (managed) and with native
   (tree, changed ranges, logger). Fixed seeds in CI, long random runs on demand.
8. Invalid input fuzzing: random byte mutations, compare with native (error recovery).
9. Query tests: each grammar's own `.scm` files over its corpus, matches and captures
   equal to native; predicate tests from `query_test.rs`.
10. Platform smoke: NativeAOT console app and browser-wasm app parse and query JSON.

### 6.3 Coverage

- Coverlet on the runtime. Target: 95% lines and 90% branches for
  `TreeSitter.Managed`, with an explicit list of excluded code (dot graph printers,
  debug asserts). The gate starts at M1 for the files ported so far.
- Every branch that coverage shows as unreached gets either a test or a written reason.

## 7. Milestones

Each step ends with a checkable result. Large changes are proposed and approved first.

| Step | Content | Done when |
|---|---|---|
| M0 | This document, `UPSTREAM.md`, repo skeleton | reviewed |
| M1a | Leaf types, Subtree, Language, ts2cs tables for JSON (ABI 15), oracle harness + table dump | table dumps equal; unit tests green |
| M1b | Lexer + lex function emitter | JSON token stream (logger `lexed_lookahead`) equal to native on corpus + random input |
| M1c | Stack + parser: full parse with error recovery; Tree, Node, TreeCursor basics | logger + tree equality on JSON corpus, error corpus, byte fuzzing |
| M1d | Tree.Edit, incremental reparse with reuse, changed ranges, included ranges | edit fuzzing equal to fresh parse and native |
| M1e | Full Node / TreeCursor API, UTF-16 input, callback input, progress callback | port of node_test.rs / tree_test.rs parts that apply; coverage gate |
| M2 | C grammar: big tables, table storage and lexer emitter measurements, baseline benchmarks vs native | decisions D3/D4 closed by measurement |
| M3 | Python: external scanner contract | corpus + fuzz equal, scanner state bytes equal |
| M4 | Query, QueryCursor, predicates, LookaheadIterator, highlight sample | query_test.rs port + `.scm` oracle tests |
| M5 | Hardening: performance, allocation profile, AOT/trim/wasm, API docs, NuGet packaging | benchmarks recorded, packages build |
| M6 | Grammar set for 1.0 | per grammar: license checked, corpus + fuzz + queries green |

Order note: incremental parsing is in M1 because node reuse (`reusable_node`,
`ts_parser__reuse_node`, `ts_parser__breakdown_*`) is part of the main parse loop and
shapes the memory model. Queries come later because `query.c` is self-contained.

Scanner complexity for M6 planning (to check per grammar): simple - JSON (none), TOML,
CSS, Go, Java; medium - Python (indent), JavaScript/TypeScript (template strings,
automatic semicolons), Rust (raw strings, block comments), C++ (raw strings),
C# (interpolated/raw strings), HTML (tags); large - Bash (heredocs), YAML (about 1500
lines), Markdown (two grammars, block + inline, with injections).

## 8. Risks and open questions

- R1. Error recovery must match C exactly: version ordering, cost comparisons,
  `MAX_VERSION_COUNT`, summaries. Mitigation: logger equality (6.2.3) from M1c.
- R2. Table storage and JIT limits for huge grammars (D3, D4). Measured in M2.
- R3. GC pressure from `SubtreeHeap` allocations during parse. C reuses freed nodes via
  `SubtreePool`. If allocation dominates in M2 profiles, options: recycle with a
  generation stamp checked in debug builds, or a page store with integer handles
  (gotreesitter `ccore` uses that). Decided by measurement. Measured in M2: allocation
  is the main cost of a parse. The options in detail: `docs/PERFORMANCE.md`.
- R4. `#match?` regex differences between Rust `regex` and .NET (D9).
- R5. Grammar regeneration needs Node.js and the grammar's `grammar.js`; the pinned CLI
  binary comes from the upstream release assets (`tree-sitter-cli-<os>-<arch>.zip`).
- R6. Thread safety: `Tree.Copy` and `Dispose` from different threads touch the same
  ref counts; all Subtree ref count changes use `Interlocked`, as `atomic.h` in C.
- Cancellation: v0.27.2 has no timeout or cancellation-flag API any more, only the
  progress callback in `TSParseOptions`. We follow that (D5).
