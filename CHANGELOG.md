# Changelog

## 1.0.0

Speed, with results that stay identical to the native runtime (every oracle test byte for byte).

- Subtrees live in an arena of structs (docs/ARENA.md): a heap subtree is the address of its
  data in pinned segments, like the C pointer, and the node layout is the union of subtree.h.
  A parse allocates about 1 MB instead of 14 MB for 170 KB of C, and the garbage collector
  is almost idle.
- Child arrays come from a per-thread pool; trees that are not disposed are released by the
  next parse on its own thread.
- Faster tree cursor and query cursor (node fields read once per step, no per-step
  allocations), faster reuse of the old tree in an incremental parse.

## 0.1.0

First public version.

- The tree-sitter runtime v0.27.2 in C#: parsing of UTF-8 and UTF-16 input, incremental
  reparse with `Tree.Edit`, changed ranges, included ranges, error recovery, `Node` and
  `TreeCursor`, field names, queries with predicates, `LookaheadIterator`, the parser log and
  the dot graphs of the parser and of the tree.
- `ts2cs`, a converter from a generated grammar (`src/parser.c`) to C#, as the dotnet tool
  `TreeSitter.Managed.Generator`.
- 18 grammar packages with ported external scanners and the query files of each grammar:
  Bash, C, C#, C++, CSS, Go, HTML, Java, JavaScript, JSON, Markdown, Markdown inline, Python,
  Rust, TOML, TSX, TypeScript, YAML.
- net8.0 and net10.0; trimming, NativeAOT and browser-wasm are checked in CI.
