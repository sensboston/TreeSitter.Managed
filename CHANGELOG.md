# Changelog

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
