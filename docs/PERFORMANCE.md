# Performance: notes and options

Notes for future work on the speed of the runtime. Nothing here is implemented yet.
The current port follows the C data structures one to one, which made the oracle tests
possible (parser logs and internal subtree dumps compared byte for byte). That choice
also decides where the time goes.

## Why the port is slower than native

Image or codec ports (Leptonica, Stb) spend their time in loops over byte arrays: the JIT
compiles them close to C, sometimes with vectors, and they allocate nothing. tree-sitter
is the opposite: it creates many small nodes, counts references, follows pointers and
branches a lot. Measured in M2 (BenchmarkDotNet against the oracle library): the main
cost is allocation, roughly 80-130 bytes per input byte on a full parse.

1. Memory model. In C, `SubtreeHeapData` comes from a pool with a free list, and
   `Subtree` is an 8-byte union (a pointer or an inline leaf). In the port, `SubtreeHeap`
   is a GC object, every node has its own children array (another object), `Subtree` is
   16 bytes (a `ulong` and a reference), and every store of a child reference goes
   through a GC write barrier.
2. Two ways of tracking memory. Reference counts are kept because the C code mutates a
   node in place only when `ref_count == 1`, and balancing (so the shape of the tree that
   the oracle compares) depends on that. The port pays for reference counting and for
   the GC.
3. Nodes of the graph-structured stack are heap objects as well.
4. Smaller costs: bounds checks, struct copies, the lexer reads characters through
   method calls.

The JIT problem of huge lex functions (MinOpts) is solved by D4 (chunk methods).

## Options, the algorithm unchanged

The parse algorithm (GLR over the generated tables, error recovery) decides the tree.
Error recovery is heuristic (costs, version selection, MISSING insertion); any change
gives other ERROR nodes on invalid input, and invalid input is the common case in an
editor. So the algorithm stays one to one. What the user sees through the API is: the
S-expression, node ranges and points, fields, changed ranges and query results. The
internal representation is free, as long as that stays the same.

Directly-executable LR (the tables compiled into a `switch` per state) is known to be
faster than a table-driven parser, but tree-sitter tables are huge (parser.c of C# is
39.7 MB): the code would grow far beyond the instruction cache. Not a priority.

From the least to the most radical:

1. Arena with integer handles. Nodes live in large arrays of structs, children in a
   shared buffer, freed slots are reused through a free list as in C. The same for stack
   nodes. No object references on the hot path: no allocation, no write barriers,
   nothing for the GC to trace, denser data. The C structures stay almost the same, so
   almost all oracle tests stay as they are (parser logs, subtree dumps). Realistic
   target: parity with native.
2. No reference counting. "Can be mutated in place" is decided another way (a generation
   or an owner instead of a counter). Balancing of repetition nodes is not visible
   through the API and can be done differently or lazily. The internal subtree dumps
   then no longer match C; the parser logs still do.
3. A flat tape (as in simdjson, Roslyn green nodes, rowan of rust-analyzer). An LR
   parser produces nodes in postorder: on a reduce the children are already the last
   entries of the buffer. A node is a record in an array of structs, a subtree is a
   range, and reuse in an incremental parse copies a range. This is where a full parse
   could be faster than C (C allocates from the pool, counts references and summarizes
   every node; the tape appends to an array). Not measured.

## Costs of a different representation

- GLR and error recovery. Stack versions share a common prefix, and error recovery moves
  subtrees around. A tape needs a fallback for that; this is the hardest part.
- Changed ranges and reuse. The C code compares subtrees by pointer identity and walks
  hidden nodes. With another structure the changed ranges can differ from C even when
  the visible tree is the same. The tests catch it, but it may need emulation.
- Upstream updates. Today a new tree-sitter version is an almost mechanical review of
  the diff. Behind a facade, every update of the core is a port of ideas, not of lines.
- Diagnosis. The byte-for-byte comparison of internal structures finds bugs that the
  public API hides (for example, `repeat_depth` of an external scanner leaf is read from
  a union with the scanner state in C). Public-level tests are still strong, but a
  difference there is harder to trace back.

## Order

1. Benchmarks against TreeSitter.DotNet (P/Invoke) as well: that is the real alternative
   for .NET users, and tree walks and queries cross the native boundary on every call.
2. A CPU and allocation profile of a full C parse (dotnet-trace): how much is
   representation, how much is error recovery, lexer and GLR splits.
3. If representation dominates: option 1 (arena, algorithm one to one), designed so that
   option 3 can replace it later without rewriting the parser.
4. Option 3 only if "faster than C" still matters after option 1.
