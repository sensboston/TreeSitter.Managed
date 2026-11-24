using System.Collections.Concurrent;
using TreeSitter.Grammars.C;
using TreeSitter.Grammars.Cpp;
using TreeSitter.Grammars.CSharp;
using TreeSitter.Grammars.Bash;
using TreeSitter.Grammars.TypeScript;
using TreeSitter.Grammars.Tsx;
using TreeSitter.Grammars.Yaml;
using TreeSitter.Grammars.Toml;
using TreeSitter.Grammars.Markdown;
using TreeSitter.Grammars.MarkdownInline;
using TreeSitter.Grammars.Css;
using TreeSitter.Grammars.Rust;
using TreeSitter.Grammars.JavaScript;
using TreeSitter.Grammars.Html;
using TreeSitter.Grammars.Go;
using TreeSitter.Grammars.Java;
using TreeSitter.Grammars.Json;
using TreeSitter.Grammars.Python;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// The languages of the subtree script (the <c>lang</c> command): the generated
/// managed grammars. The native oracle knows the same three names.
/// </summary>
internal static class ScriptLanguages
{
    private static readonly ConcurrentDictionary<string, Language?> Languages =
        new ConcurrentDictionary<string, Language?>(StringComparer.Ordinal);

    /// <summary>Returns the language of the script, or null when the name is unknown.</summary>
    /// <param name="name">The name of the grammar, for example "json".</param>
    internal static Language? Get(string name)
    {
        return Languages.GetOrAdd(name, static key => key switch
        {
            "json" => JsonGrammar.Language,
            "c" => CGrammar.Language,
            "python" => PythonGrammar.Language,
            "go" => GoGrammar.Language,
            "java" => JavaGrammar.Language,
            "cpp" => CppGrammar.Language,
            "c_sharp" => CSharpGrammar.Language,
            "bash" => BashGrammar.Language,
            "typescript" => TypeScriptGrammar.Language,
            "tsx" => TsxGrammar.Language,
            "yaml" => YamlGrammar.Language,
            "toml" => TomlGrammar.Language,
            "markdown" => MarkdownGrammar.Language,
            "markdown_inline" => MarkdownInlineGrammar.Language,
            "css" => CssGrammar.Language,
            "rust" => RustGrammar.Language,
            "javascript" => JavaScriptGrammar.Language,
            "html" => HtmlGrammar.Language,
            _ => null,
        });
    }
}
