// A smoke test over every bundled grammar, published three ways by CI (JIT, NativeAOT,
// browser-wasm) so that one line of output of each run proves the whole stack survives
// ahead-of-time compilation and trimming. See .github/workflows/build.yml.
//
// The snippets are the ones checked against the native runtime by the oracle tests; what is
// verified here is the compilation mode, so the output is deterministic and printable: one
// line per grammar with the node count, the number of query captures and the beginning of
// the s-expression of the tree. All 18 bundled grammars are in the run, the two Markdown
// ones since their external scanners landed in main.

using System.Globalization;
using System.Text;
using TreeSitter;
using TreeSitter.Grammars.Bash;
using TreeSitter.Grammars.C;
using TreeSitter.Grammars.CSharp;
using TreeSitter.Grammars.Cpp;
using TreeSitter.Grammars.Css;
using TreeSitter.Grammars.Go;
using TreeSitter.Grammars.Html;
using TreeSitter.Grammars.Java;
using TreeSitter.Grammars.JavaScript;
using TreeSitter.Grammars.Json;
using TreeSitter.Grammars.Markdown;
using TreeSitter.Grammars.MarkdownInline;
using TreeSitter.Grammars.Python;
using TreeSitter.Grammars.Rust;
using TreeSitter.Grammars.Toml;
using TreeSitter.Grammars.Tsx;
using TreeSitter.Grammars.TypeScript;
using TreeSitter.Grammars.Yaml;

namespace TreeSitter.Smoke;

/// <summary>One grammar of the smoke run: what to parse, and where its language and query come from.</summary>
/// <param name="Name">The name printed as the first word of the line.</param>
/// <param name="Language">The language to parse with.</param>
/// <param name="Highlights">The highlights query of the grammar, null when it ships no such file.</param>
/// <param name="Source">The snippet to parse.</param>
internal readonly record struct GrammarCase(
    string Name,
    Language Language,
    string? Highlights,
    string Source);

/// <summary>Parses one snippet per grammar, checks the tree, runs the highlights query, prints a line.</summary>
internal static class Program
{
    /// <summary>How many characters of the root s-expression are printed.</summary>
    private const int SexpLength = 60;

    private static readonly GrammarCase[] Cases =
    {
        new("bash", BashGrammar.Language, BashGrammar.Query("highlights"), """
            #!/usr/bin/env bash

            for f in *.txt; do
              echo "$f"
            done
            """),
        new("c", CGrammar.Language, CGrammar.Query("highlights"), """
            int main(void) {
                return 0;
            }
            """),
        new("cpp", CppGrammar.Language, CppGrammar.Query("highlights"), """
            #include <string>
            #include <vector>

            std::vector<std::string> names = {"a", "b"};

            int main() {
                return (int)names.size();
            }
            """),
        new("csharp", CSharpGrammar.Language, CSharpGrammar.Query("highlights"), """
            using System;

            namespace Smoke
            {
                internal static class Program
                {
                    private static int Add(int a, int b)
                    {
                        return a + b;
                    }
                }
            }
            """),
        new("css", CssGrammar.Language, CssGrammar.Query("highlights"), """
            :root {
              --gap: 4px;
            }

            .card {
              color: red;
              padding: var(--gap);
            }
            """),
        new("go", GoGrammar.Language, GoGrammar.Query("highlights"), """
            package main

            func add(a int, b int) int {
                return a + b
            }

            func main() {
                println(add(1, 2))
            }
            """),
        new("html", HtmlGrammar.Language, HtmlGrammar.Query("highlights"), """
            <!DOCTYPE html>
            <html lang="en">
              <body class="main">
                <p id="a">Hello</p>
              </body>
            </html>
            """),
        new("java", JavaGrammar.Language, JavaGrammar.Query("highlights"), """
            public class Smoke {
                private static int add(int a, int b) {
                    return a + b;
                }

                public static void main(String[] args) {
                    System.out.println(add(1, 2));
                }
            }
            """),
        new("javascript", JavaScriptGrammar.Language, JavaScriptGrammar.Query("highlights"), """
            function add(a, b) {
              return a + b;
            }

            const total = add(1, 2)
            export default total
            """),
        new("json", JsonGrammar.Language, JsonGrammar.Query("highlights"), """
            {"name": "smoke", "values": [1, 2, 3], "ok": true, "none": null}
            """),
        new("markdown", MarkdownGrammar.Language, MarkdownGrammar.Query("highlights"), """
            # Title

            A *paragraph* with `code`.

            - one
            - two
            """),
        new("markdown_inline", MarkdownInlineGrammar.Language, MarkdownInlineGrammar.Query("highlights"), """
            A *paragraph* with `code`.
            """),
        new("python", PythonGrammar.Language, PythonGrammar.Query("highlights"), """
            def add(a, b):
                # sum of two numbers
                return a + b


            print(add(1, 2))
            """),
        new("rust", RustGrammar.Language, RustGrammar.Query("highlights"), """
            fn add(a: i32, b: i32) -> i32 {
                a + b
            }

            fn main() {
                println!("{}", add(1, 2));
            }
            """),
        new("toml", TomlGrammar.Language, TomlGrammar.Query("highlights"), """
            title = "smoke"

            [owner]
            name = "someone"
            """),
        new("tsx", TsxGrammar.Language, TsxGrammar.Query("highlights"), """
            type Props = { title: string }

            export const Card = ({ title }: Props) => {
              return <h1 className="t">{title}</h1>
            }
            """),
        new("typescript", TypeScriptGrammar.Language, TypeScriptGrammar.Query("highlights"), """
            interface Point {
              x: number
              y: number
            }

            function length(p: Point): number {
              return p.x + p.y
            }

            export const value: number = length({ x: 1, y: 2 })
            """),
        new("yaml", YamlGrammar.Language, YamlGrammar.Query("highlights"), """
            name: smoke
            version: 1
            items:
              - one
              - two
            map:
              key: value
            """),
    };

    private static int Main() => RunCases(Cases) == 0 ? 0 : 1;

    private static int RunCases(GrammarCase[] cases)
    {
        int failures = 0;
        foreach (GrammarCase grammarCase in cases)
        {
            failures += Run(grammarCase);
        }

        return failures;
    }

    /// <summary>Parses the snippet of one grammar, checks its tree and counts the captures of its query.</summary>
    private static int Run(GrammarCase grammarCase)
    {
        Parser parser = new Parser();
        if (!parser.SetLanguage(grammarCase.Language))
        {
            return Fail(grammarCase.Name, "SetLanguage rejected the language");
        }

        Tree? tree = parser.ParseString(grammarCase.Source);
        if (tree is null)
        {
            return Fail(grammarCase.Name, "ParseString returned no tree");
        }

        using (tree)
        {
            Node root = tree.RootNode;

            // RootNode.HasError is the check of the task; the walk names the node it is about.
            if (root.HasError || ContainsBadNode(root))
            {
                return Fail(grammarCase.Name, "the tree contains an ERROR or MISSING node");
            }

            uint captures = 0;
            if (grammarCase.Highlights is string querySource)
            {
                Query query = new Query(grammarCase.Language, querySource);
                QueryCursor cursor = new QueryCursor();
                cursor.Exec(query, root);
                while (cursor.NextCapture(out QueryMatch match, out _))
                {
                    _ = match;
                    captures++;
                }
            }

            string sexp = root.ToString();
            StringBuilder line = new StringBuilder(128);
            line.Append(CultureInfo.InvariantCulture, $"{grammarCase.Name} nodes={root.DescendantCount} captures={captures} sexp=");
            line.Append(sexp.AsSpan(0, Math.Min(SexpLength, sexp.Length)));
            Console.WriteLine(line.ToString());
        }

        return 0;
    }

    /// <summary>True when the node or one of its descendants is an ERROR node or a MISSING node.</summary>
    private static bool ContainsBadNode(Node node)
    {
        if (node.IsError || node.IsMissing)
        {
            return true;
        }

        for (uint index = 0; index < node.ChildCount; index++)
        {
            if (ContainsBadNode(node.Child(index)))
            {
                return true;
            }
        }

        return false;
    }

    private static int Fail(string name, string reason)
    {
        Console.WriteLine("FAIL " + name + ": " + reason);
        return 1;
    }
}
