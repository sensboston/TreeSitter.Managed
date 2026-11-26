# Subtree arena: design

Status: implemented in 1.0.0 (option 1 of docs/PERFORMANCE.md). Results: docs/BENCHMARKS.md. The parse algorithm stays one to one;
only the representation of subtrees changes.

Goal: the speed of the native runtime or better, with results that stay identical to it
(every oracle test byte for byte). After the arena, the remaining gap is the parser logic
(`Advance`, `Reduce`), tuned by profile; the lexer is already faster than native.

## Why

Profile of a full parse of `lib/src/query.c` (170 KB, C grammar), perf on Linux ARM64:
port 45.5 ms, native 28.8 ms. Per parse, port / native:

| Part | Port | Native |
|---|---:|---:|
| lexer | 6.7 ms | 9.2 ms |
| table lookups | 2.9 ms | 3.2 ms |
| stack | 3.5 ms | 3.9 ms |
| subtrees (summarize, release, new) | 3.6 ms | 5.0 ms |
| parser (advance, reduce) | 6.8 ms | 3.0 ms |
| memory: GC, write barriers, allocation | 12.5 ms | 1.6 ms |

The lexer, the tables and the stack are at native speed. About 70% of the gap is memory
management: a parse allocates 11 MB (after caching the stack delegates), 60% `Subtree[]`
child arrays and 40% `SubtreeHeap` objects. Almost all of it is the result tree, which lives
until `Tree.Dispose`, so pooling freed objects does not help (measured: allocations -20%,
time -5%, GC time unchanged). The GC time is spent marking and compacting the live tree in
gen1/gen2, and every store of a `Subtree` into an array goes through a write barrier,
because `Subtree` holds a reference.

The fix is to make the tree invisible to the GC: no object per node, no reference in
`Subtree`.

## Decisions

### A1. Subtree is an 8-byte value without references

`Subtree` becomes `readonly struct Subtree { ulong Value; }`, the same as the C union:

- bit 0 set: an inline leaf, the bit layout of today's `Inline` field, unchanged;
- bit 0 clear, value 0: NULL_SUBTREE;
- bit 0 clear, value not 0: a heap subtree, `Value >> 1` is the handle of its heap data.

`Subtree` has no reference field, so `Subtree[]` is an array of an unmanaged type: the GC
never scans it and stores into it have no write barrier. The public API does not change
(`Node`, `Tree`, `TreeCursor` keep their shape; `Node` already carries its `Tree`).

### A2. Heap data lives in a global arena of structs

`SubtreeHeap` (class) becomes `SubtreeHeapData` (struct) stored in `SubtreeArena`: a list of
fixed-size segments (`SubtreeHeapData[]`, 4096 entries each). A handle is
`segment << 12 | slot`. Segments never move, so a handle stays valid while the slot is
allocated, and readers on other threads never see a moved element.

`SubtreeHeapData` must be unmanaged too, or the GC would scan every segment. So the two
references it has today move out of it:

- the children: `int ChildrenHandle` into the children arena (A3);
- the long data of an external scanner state (more than 24 bytes, `byte[]`): `int` handle
  into a side table of `byte[]` with a free list. Short data stays inline
  (`[InlineArray(24)]`, unmanaged). Long states are rare.

`ref SubtreeHeapData SubtreeArena.Get(Subtree s)` is the only accessor: two array loads
(segment table, segment) instead of one pointer load. All 65 uses of `.Heap` in six files
(Subtree.cs, SubtreeOps.cs, Parser.cs, Node.cs, TreeCursor.cs, ChangedRanges.cs) go through it.

### A3. Children live in a slab arena

The children of a node are a run of `Subtree` values in `ChildrenArena`: segments of
`ulong`, allocated by size classes (powers of two up to 256 elements; a larger run gets a
dedicated `Subtree[]` kept in a side table, like the long scanner state). A freed run goes
to the free list of its class. `ts_subtree_new_node` takes the children of a
`SubtreeArray`: the parser builds them in its own `TsArray<Subtree>` as today, and
`NewNode` copies them into a run of the exact class (C reallocs the array in place; the
copy is a memcpy of 8-byte values without barriers).

This step is separate (phase B below): phase A keeps `Subtree[]` children in a side table
indexed by `ChildrenHandle`, which already removes the `SubtreeHeap` objects and the write
barriers of the stack and of child arrays.

### A4. Threads

The arena is one per process. The rules of upstream stay: a `Parser` is used by one thread,
a `Tree` can be read by many threads, `Tree.Copy` and `Tree.Dispose` can run on any thread.

- Allocation: each thread takes free handles from a thread-local batch (64 handles); an
  empty batch is refilled from the global free list under a lock, and a new segment is
  added under the same lock (the segment table is replaced, copy on grow, and published
  with a volatile write).
- Release: ref counts stay `Interlocked`, at the same places as in C. A handle whose count
  reaches 0 goes to a thread-local batch, flushed to the global free list under the lock
  when it is full.
- Readers never take the lock.

### A5. Lifetime

Today a forgotten `Tree` is collected by the GC. With an arena, a tree that is never
released keeps its slots forever, as in C. To keep the .NET behavior:

- `Tree` gets a finalizer that releases its root (the release is thread-safe, A4).
  `Dispose` releases it and suppresses the finalizer. `Node` and `TreeCursor` hold the
  `Tree`, so a tree is not finalized while a node of it is reachable.
- `Parser` gets a finalizer that releases what its stack, its reusable node, its cached
  token and its finished tree hold (`ts_parser_reset` / `ts_parser_delete`).

Segments are kept for reuse; returning fully free segments to the GC is a later option.

### A6. Oracle tests stay as they are

The subtree dumps number heap subtrees in the order they are printed, not by address, and
the ref counts, the flags and the layout of inline leaves do not change. So the parser logs,
the subtree dumps and every other oracle comparison stay byte for byte the same. Any
difference is a bug of the arena (a slot reused while still referenced, or a missing reset).

Debug builds add a generation counter per slot, checked on every access, so a stale handle
fails at once instead of reading another node.

## Phases

| Phase | Content | Check |
|---|---|---|
| A | `Subtree` as `ulong`, `SubtreeHeapData` struct in `SubtreeArena` (A1, A2, A4); children still `Subtree[]` through a side table; external long state through a side table | all oracle tests; perf on Linux: GC time and write barriers |
| B | children in `ChildrenArena` (A3) | same, plus allocations per parse |
| C | finalizers of `Tree` and `Parser` (A5), a test that a forgotten tree is reclaimed and that concurrent `Dispose` on many threads keeps the arena consistent | stress test, NativeAOT and wasm smoke jobs |

Expected result: the memory part of the profile (12.5 ms of 45.5 ms) shrinks to a few
milliseconds, which brings a full parse to about 1.1 - 1.25 times native. Measured after
each phase with the same perf method, before deciding on the next.

## Risks

- A slot reused while still referenced (a ref count bug that the GC hides today). The debug
  generation check (A6) and the oracle tests are the safety net.
- Contention on the arena lock when many threads parse at once: the thread-local batches
  keep the lock off the hot path; measured with a parallel parse test in phase C.
- Memory is not returned to the OS after a very large parse (segments are kept). Acceptable
  for 1.x; trimming can follow.

## Done and next

Implemented: A1, A2, A4, A5 (finalizers release through a queue drained by the next parse),
and instead of the slab arena of A3, a per-thread pool of child arrays. Beyond the design:
the Subtree value holds the address of its data (pinned segments) instead of a handle, and
SubtreeHeapData uses the union layout of C (84 bytes).

Next: the nodes of the parse stack (StackNode) are still objects with references; moving them
into an arena of structs removes the remaining write barriers (about 4% of a parse).

