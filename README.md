# TreeSitter.Managed

<img src="https://raw.githubusercontent.com/sensboston/TreeSitter.Managed/main/assets/icon.png" align="right" width="96" alt="">

[![NuGet](https://img.shields.io/nuget/v/TreeSitter.Managed)](https://www.nuget.org/packages/TreeSitter.Managed)
[![Downloads](https://img.shields.io/nuget/dt/TreeSitter.Managed)](https://www.nuget.org/packages/TreeSitter.Managed)
[![CI](https://github.com/sensboston/TreeSitter.Managed/actions/workflows/build.yml/badge.svg)](https://github.com/sensboston/TreeSitter.Managed/actions/workflows/build.yml)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com)
[![tree-sitter](https://img.shields.io/badge/tree--sitter-v0.27.2-2e7d32)](https://github.com/tree-sitter/tree-sitter/releases/tag/v0.27.2)
[![Grammars](https://img.shields.io/badge/grammars-18-6a4ce6)](#the-grammar-packages)
[![NativeAOT](https://img.shields.io/badge/NativeAOT-ready-brightgreen)](#supported-targets)
[![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20WebAssembly-lightgrey)](#supported-targets)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

A fully managed C# port of the [tree-sitter](https://github.com/tree-sitter/tree-sitter)
incremental parsing runtime (the code of `lib/src` of tag `v0.27.2`), plus `ts2cs`, a
converter that turns a generated tree-sitter grammar (`src/parser.c`) into C# code. There
are no native binaries and no P/Invoke: the parser tables, the parser itself and the
external scanners of the grammars are C#. One assembly runs wherever the .NET runtime runs.

```
dotnet add package TreeSitter.Managed
dotnet add package TreeSitter.Managed.Grammars.Python
```

All packages are on nuget.org: the runtime
[TreeSitter.Managed](https://www.nuget.org/packages/TreeSitter.Managed), one package per grammar
(see [the grammar packages](#the-grammar-packages)) and the converter tool
[TreeSitter.Managed.Generator](https://www.nuget.org/packages/TreeSitter.Managed.Generator)
(`dotnet tool install -g TreeSitter.Managed.Generator`).

## tree-sitter

tree-sitter is the work of **Max Brunsfeld** and its many contributors. It is a parser
generator and an incremental parsing library: it builds a concrete syntax tree for a source
file and keeps it up to date as the file is edited, fast enough to run on every keystroke, and
it stays useful when the code has syntax errors. Editors, code hosts and analysis tools use it
for highlighting, navigation, folding and structural search.

Everything in this package comes from that work: the parsing algorithm, the error recovery,
the query language, the structure of the API and the grammars, each written and maintained by
its own authors (see the table below). The original projects live at
[github.com/tree-sitter](https://github.com/tree-sitter) and
[github.com/tree-sitter-grammars](https://github.com/tree-sitter-grammars). Thank you all for
tree-sitter and for making it free for everyone to use.

The design of the port is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), the pinned upstream
versions are in [UPSTREAM.md](UPSTREAM.md), and the changes per version are in
[CHANGELOG.md](CHANGELOG.md).

## Why a managed port

- NativeAOT. The parser and the grammars are ordinary managed code, so an AOT publish
  compiles them into the app like any other library: no native library per platform to
  build, ship and load next to the executable.
- Trimming. Nothing is looked up by name through reflection, so an app that parses one grammar
  can be trimmed down to the code of that grammar.
- browser-wasm and Blazor WebAssembly. There is no way to load a native library in a browser;
  the wasm VM is the only runtime there, and the port runs on it.
- One package for every platform. A native tree-sitter build needs one binary per operating
  system and architecture, and a grammar is usually shipped as a separate native package per
  RID. Here a grammar is a managed assembly: the same file on every platform .NET runs on.
- A new grammar is a converter run, not a new port: `ts2cs` reads the same `parser.c` that
  the C build of the grammar compiles.

## The grammar packages

The runtime is `TreeSitter.Managed`. Each grammar is its own package, and it references the
runtime. `Query("highlights")` returns the `queries/highlights.scm` file of the upstream
repository, embedded in the grammar assembly (null when the grammar has no such file); the
upstream license ships with the package.

| Grammar | Package | Upstream repository | Commit | License |
|---|---|---|---|---|
| Bash | [`TreeSitter.Managed.Grammars.Bash`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Bash) | [tree-sitter-bash](https://github.com/tree-sitter/tree-sitter-bash) | `a06c2e4415e9bc0346c6b86d401879ffb44058f7` | MIT |
| C | [`TreeSitter.Managed.Grammars.C`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.C) | [tree-sitter-c](https://github.com/tree-sitter/tree-sitter-c) | `b780e47fc780ddc8da13afa35a3f4ed5c157823d` | MIT |
| C# | [`TreeSitter.Managed.Grammars.CSharp`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.CSharp) | [tree-sitter-c-sharp](https://github.com/tree-sitter/tree-sitter-c-sharp) | `e3971fcb53bf5573a7e0d4ff6db46d2a77a4bf71` | MIT |
| C++ | [`TreeSitter.Managed.Grammars.Cpp`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Cpp) | [tree-sitter-cpp](https://github.com/tree-sitter/tree-sitter-cpp) | `c009222808634c1014f82438d4883753516a2c24` | MIT |
| CSS | [`TreeSitter.Managed.Grammars.Css`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Css) | [tree-sitter-css](https://github.com/tree-sitter/tree-sitter-css) | `dda5cfc5722c429eaba1c910ca32c2c0c5bb1a3f` | MIT |
| Go | [`TreeSitter.Managed.Grammars.Go`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Go) | [tree-sitter-go](https://github.com/tree-sitter/tree-sitter-go) | `2346a3ab1bb3857b48b29d779a1ef9799a248cd7` | MIT |
| HTML | [`TreeSitter.Managed.Grammars.Html`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Html) | [tree-sitter-html](https://github.com/tree-sitter/tree-sitter-html) | `73a3947324f6efddf9e17c0ea58d454843590cc0` | MIT |
| Java | [`TreeSitter.Managed.Grammars.Java`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Java) | [tree-sitter-java](https://github.com/tree-sitter/tree-sitter-java) | `e10607b45ff745f5f876bfa3e94fbcc6b44bdc11` | MIT |
| JavaScript | [`TreeSitter.Managed.Grammars.JavaScript`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.JavaScript) | [tree-sitter-javascript](https://github.com/tree-sitter/tree-sitter-javascript) | `58404d8cf191d69f2674a8fd507bd5776f46cb11` | MIT |
| JSON | [`TreeSitter.Managed.Grammars.Json`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Json) | [tree-sitter-json](https://github.com/tree-sitter/tree-sitter-json) | `254c42a6476413b776221e03982ac8ae159eeb72` | MIT |
| Markdown | [`TreeSitter.Managed.Grammars.Markdown`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Markdown) | [tree-sitter-markdown](https://github.com/tree-sitter-grammars/tree-sitter-markdown) | `41ff24a6c775492c543b09228aeb98d1cd2b3a0f` | MIT |
| Markdown inline | [`TreeSitter.Managed.Grammars.MarkdownInline`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.MarkdownInline) | [tree-sitter-markdown](https://github.com/tree-sitter-grammars/tree-sitter-markdown) | `41ff24a6c775492c543b09228aeb98d1cd2b3a0f` | MIT |
| Python | [`TreeSitter.Managed.Grammars.Python`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Python) | [tree-sitter-python](https://github.com/tree-sitter/tree-sitter-python) | `26855eabccb19c6abf499fbc5b8dc7cc9ab8bc64` | MIT |
| Rust | [`TreeSitter.Managed.Grammars.Rust`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Rust) | [tree-sitter-rust](https://github.com/tree-sitter/tree-sitter-rust) | `77a3747266f4d621d0757825e6b11edcbf991ca5` | MIT |
| TOML | [`TreeSitter.Managed.Grammars.Toml`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Toml) | [tree-sitter-toml](https://github.com/tree-sitter-grammars/tree-sitter-toml) | `64b56832c2cffe41758f28e05c756a3a98d16f41` | MIT |
| TSX | [`TreeSitter.Managed.Grammars.Tsx`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Tsx) | [tree-sitter-typescript](https://github.com/tree-sitter/tree-sitter-typescript) | `75b3874edb2dc714fb1fd77a32013d0f8699989f` | MIT |
| TypeScript | [`TreeSitter.Managed.Grammars.TypeScript`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.TypeScript) | [tree-sitter-typescript](https://github.com/tree-sitter/tree-sitter-typescript) | `75b3874edb2dc714fb1fd77a32013d0f8699989f` | MIT |
| YAML | [`TreeSitter.Managed.Grammars.Yaml`](https://www.nuget.org/packages/TreeSitter.Managed.Grammars.Yaml) | [tree-sitter-yaml](https://github.com/tree-sitter-grammars/tree-sitter-yaml) | `a1c4812a73ec5e089de8e441fdea3a921e8d5079` | MIT |

The two Markdown grammars come from one repository with two subdirectories, and they are
parsed as two separate languages: an inline snippet is not tied to the block tree, and the
block grammar does not call into the inline one. Each of them has its own ported external
scanner (`grammars/Markdown/MarkdownScanner.cs`, `grammars/MarkdownInline/MarkdownInlineScanner.cs`).

## Quick start

Every grammar exposes a static class with the language and its query files:
`JsonGrammar.Language`, `PythonGrammar.Language`, and so on.

```csharp
using TreeSitter;
using TreeSitter.Grammars.Json;

Parser parser = new Parser();
if (!parser.SetLanguage(JsonGrammar.Language))
{
    throw new InvalidOperationException("the language is not usable by this parser");
}

// Parse is nullable on purpose: it returns null when the parser has no language, when the
// old tree belongs to another language, or when a progress callback stopped the parse.
Tree tree = parser.ParseString("""{"name": "tree-sitter", "tags": ["parser", "csharp"]}""")!;
Node root = tree.RootNode;

Console.WriteLine(root);                 // the tree as an S-expression
Console.WriteLine(root.ChildCount);      // the number of children of the root
Console.WriteLine(root.StartPoint.Row);  // zero-based row and column (in bytes)
```

`Tree` is `IDisposable`, and a `Node` is a cheap handle into the tree it came from, so keep
the tree alive as long as its nodes are used:

```csharp
using Tree tree = parser.ParseString(text)!;
```

### Walking the nodes

```csharp
private static void PrintTree(Node node, int depth)
{
    for (uint index = 0; index < node.ChildCount; index++)
    {
        Node child = node.Child(index);
        Console.WriteLine(new string(' ', depth * 2) + child.Type + " [" + child.StartByte + ".." + child.EndByte + "]");
        PrintTree(child, depth + 1);
    }
}
```

`NamedChild`, `NamedChildCount`, `ChildByFieldName`, `NextSibling`, `FirstChildForByte` and
`DescendantForByteRange` do the same job with the same rules as the C API. To walk a tree
without recursion, use `TreeCursor`, which keeps its own position:

```csharp
TreeCursor cursor = new TreeCursor(root);
if (cursor.GotoFirstChild())
{
    do
    {
        Node child = cursor.CurrentNode;
        Console.WriteLine(child.Type + (child.IsNamed ? string.Empty : " (anonymous)"));
    }
    while (cursor.GotoNextSibling());
}
```

### A query with captures

```csharp
Query query = new Query(JsonGrammar.Language, """
    (pair key: (string (string_content) @key) value: (_) @value)
    """);

QueryCursor cursor = new QueryCursor();
cursor.Exec(query, tree.RootNode);

while (cursor.NextMatch(out QueryMatch match))
{
    foreach (QueryCapture capture in match.Captures)
    {
        Console.WriteLine(query.CaptureNameForId(capture.Index) + " = " + capture.Node);
    }
}
```

`NextCapture` walks the captures in order instead of the matches, `cursor.SetByteRange` limits
the run to a range, and `query.DisableCapture` or `query.DisablePattern` turn parts of the
query off. A pattern that is not valid for the language throws `QueryException`;
`Query.TryCreate` reports the error offset and the error kind instead.

### An incremental edit

Edit the text, tell the tree where the edit happened, and parse again with the old tree: the
parser reuses the parts of the tree that the edit cannot have touched.

```csharp
string oldText = """{"name": "tree-sitter"}""";
string insertion = ", \"version\": 1";                        // 14 bytes, inserted at offset 22
string newText = oldText[..22] + insertion + oldText[22..];   // {"name": "tree-sitter", "version": 1}

using Tree oldTree = parser.ParseString(oldText)!;

// The edit is described for the old and for the new text, in bytes and in points, and the
// offsets and the points describe the same position. An insertion changes nothing before it,
// so StartByte and OldEndByte are equal. The members of InputEdit are uint, like the C struct.
oldTree.Edit(new InputEdit(
    StartByte: 22,
    OldEndByte: 22,
    NewEndByte: 36,
    StartPoint: new Point(0, 22),
    OldEndPoint: new Point(0, 22),
    NewEndPoint: new Point(0, 36)));

using Tree tree = parser.ParseString(newText, oldTree)!;
Range[] changed = Tree.GetChangedRanges(oldTree, tree);
```

A node of the old tree is only valid while that tree lives, so read what you need from the
edited tree before you replace it. The byte offsets and the points of an `InputEdit` must
describe the same position in the old text and in the new one: an edit that lies about them
does not fail, it only makes the parser reuse the wrong parts of the tree.
`tests/TreeSitter.Managed.Tests/Parsing` (EditRunner, IncrementalParseTests) parses edited
trees both natively and by the port, so the rule is checked, not just documented.

## Another grammar with ts2cs

The converter is also a dotnet tool, so a grammar that is not bundled here can be used in the
same way:

```
dotnet tool install -g TreeSitter.Managed.Generator

# in the grammar repository, with the tree-sitter CLI v0.27.2 (ABI 15):
tree-sitter generate
ts2cs generate src/parser.c --name MyLang --namespace MyCompany.Grammars.MyLang --out Generated
```

Add the generated `Generated/MyLangLanguage.g.cs` to a project that references
`TreeSitter.Managed`, and use `MyLangGrammar.Language`. If the grammar has an external scanner
(`src/scanner.c`), the generated code expects a class `MyLangScanner : IExternalScanner` in the
same namespace: a port of that C file, one method per C function. The scanners in `grammars/`
show how (`grammars/Css/CssScanner.cs` is a small one).

## How the port is checked

The tests do not carry hand-written expectations for the parser: they use the native runtime
of the same pinned version as an oracle. `tools/build-oracle.sh` compiles `lib.c` of
`tests/Oracle/tree-sitter/lib/src` and the `parser.c` and `scanner.c` of every bundled
grammar into one shared library, and a test runs the same input through the C runtime and
through the port, then compares the text they print, line by line:

- the parse log and the lex log of the parser, byte for byte (the same logger, the same
  message text),
- the tree: the S-expression, the subtree dump, the node and cursor accessors,
- the ranges an incremental edit changed, and the state the external scanner serialized,
- the queries: compiled pattern tables, matches, captures, ranges, and the results of the
  predicates.

The inputs are the corpus files that each grammar repository ships, the same corpora parsed
incrementally with the old tree, and random mutations of those inputs (deleted, repeated and
inserted pieces, punctuation of the grammar, broken UTF-8). Most mutated inputs contain
syntax errors on purpose: they are what compares the error recovery and the cost model of the
port. `tests/Oracle/FORMATS.md` fixes the dump formats the two sides must agree on.

On top of that, the samples and the CI jobs below check the compilation modes rather than the
parser: `samples/TreeSitter.Managed.Smoke` parses one snippet per grammar (all 18 of them),
fails if any tree carries an `ERROR` or `MISSING` node, and prints a deterministic line per
grammar with the node count, the number of captures of the grammar's `highlights` query and
the beginning of the S-expression.

## Supported targets

| Target | Checked by |
|---|---|
| net8.0 and net10.0 | the `test` job of [`.github/workflows/build.yml`](.github/workflows/build.yml), on ubuntu and on windows |
| NativeAOT (`linux-x64`) | the `aot` job: `dotnet publish -r linux-x64 -p:PublishAot=true` must produce a warning-free log, and the binary must print the same bytes as the JIT run |
| browser-wasm, trimmed | the `wasm` job: a trimmed `browser-wasm` publish of the same sources, run under node; the output of the app must be the same bytes as the JIT run |

The runtime and every grammar set `IsTrimmable` and `IsAotCompatible`, so a reflection or
trimming regression shows up as a build warning in the library itself, not as a crash in an
app. The wasm target is what Blazor WebAssembly runs on; the smoke app goes through the same
browser-wasm build of the runtime, started from node instead of from a page.

Both jobs fail the build on any `IL2xxx`, `IL3xxx`, `TRxxxx` or `MTxxxx` warning in the
publish log, and no warning is suppressed anywhere in the repository. If you meet such a
warning in your own app, please open an issue: the fix belongs in the runtime or in the
grammar.

Notes on the wasm job: `browser-wasm` needs the `wasm-tools` workload even without wasm AOT,
so the job installs it; the wasm sample targets net10.0 only (the runtime and the grammars
keep both targets); the loader is `samples/TreeSitter.Managed.Smoke.Wasm/main.mjs`.

## Building and regenerating a grammar

```
dotnet build TreeSitter.Managed.slnx -c Release
dotnet test tests/TreeSitter.Managed.Tests -c Release
bash tools/build-oracle.sh        # the native oracle the tests compare against
```

A grammar in this repository is generated code plus the port of its external scanner. To
regenerate one, or to add one:

1. `bash tools/regen-grammar.sh <Name> <repo-url> <commit> [<subdir>]` clones the grammar
   repository at that commit, runs `tree-sitter generate` of the pinned CLI (`v0.27.2`, which
   is the ABI 15 the port supports), copies `grammar.js`, `src/`, `queries/`, `test/corpus/`
   and `LICENSE` into `grammars/<Name>/upstream/` unchanged, and updates the row of the
   grammar in `UPSTREAM.md`. `<subdir>` is the directory of the grammar inside a repository
   that holds more than one (TypeScript and TSX).
2. Convert the tables to C# with `ts2cs`:

   ```
   dotnet run --project src/TreeSitter.Managed.Generator -c Release -f net10.0 -- \
     generate grammars/<Name>/upstream/src/parser.c \
     --name <Name> --namespace TreeSitter.Grammars.<Name> \
     --out grammars/<Name>/Generated \
     --source "<repo-name> <commit>"
   ```

   It writes `grammars/<Name>/Generated/<Name>Language.g.cs`: the language tables, and the
   `TreeSitter.Grammars.<Name>.<Name>Grammar` class with `Language` and `Query`.
   The output is deterministic, so CI can require that the committed file is up to date.
3. Port `src/scanner.c` by hand to `grammars/<Name>/<Name>Scanner.cs` when the grammar has an
   external scanner. It implements `IExternalScanner`, one method per C function, following
   the same rules as the runtime: see `grammars/Css/CssScanner.cs` for a small one and
   `grammars/Python/PythonScanner.cs` for one with a stack.
4. Add `grammars/<Name>/TreeSitter.Managed.Grammars.<Name>.csproj`, the row in
   `TreeSitter.Managed.slnx`, and the grammar to the projects that list them all
   (`tests/TreeSitter.Managed.Tests`, `samples/TreeSitter.Managed.Smoke` and its
   `samples/TreeSitter.Managed.Smoke.Wasm` twin, which compiles the same sources). The query files of
   `upstream/queries/` are embedded as resources by the shared build props; nothing else has
   to be configured.
5. Run the tests. `bash tools/build-oracle.sh` first, so that the oracle comparisons are
   enabled: they cover the tables, the corpus, the incremental edits and the queries of the
   new grammar against the native build of the same commit.

## Status

1.0.0 is the first stable version. Every grammar in the table is checked against the native
runtime as described above, and the API follows the C API of tree-sitter; it follows semantic
versioning from here on. Bug reports and grammar requests are welcome in the
[issues](https://github.com/sensboston/TreeSitter.Managed/issues).

## License

MIT, see [LICENSE](LICENSE). The port keeps the license file of every bundled grammar next to
its sources, and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists the upstream projects
with their licenses.
