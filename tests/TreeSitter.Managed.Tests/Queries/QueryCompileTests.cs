using System.Globalization;
using System.Text;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Queries;

/// <summary>
/// Compiles queries natively and with the port (ts_query_new): the error and its offset, or
/// everything the public query API returns, must be equal (tests/Oracle/FORMATS.md,
/// section "oracle_query").
/// </summary>
public class QueryCompileTests
{
    /// <summary>The query files of the grammars.</summary>
    public static TheoryData<string, string> QueryFiles()
    {
        TheoryData<string, string> data = new TheoryData<string, string>();
        foreach ((string grammar, string directory) in new[] { ("json", "Json"), ("c", "C"), ("python", "Python") })
        {
            string path = Path.Combine(FindRoot(), "grammars", directory, "upstream", "queries");
            foreach (string file in Directory.GetFiles(path, "*.scm").Order(StringComparer.Ordinal))
            {
                data.Add(grammar, Path.GetFileName(file));
            }
        }

        return data;
    }

    /// <summary>Small queries, one or more per feature and per error of query.c.</summary>
    public static TheoryData<string, string> Queries() => new TheoryData<string, string>
    {
        // Patterns, captures, fields, wildcards, anchors, quantifiers, alternations.
        { "json", "(pair key: (string) @key value: (_) @value)" },
        { "json", "(object (pair) @p)" },
        { "json", "(array . (number) @first)" },
        { "json", "(array (number) @last .)" },
        { "json", "(array (_) @a . (_) @b)" },
        { "json", "(document (_) @x)" },
        { "json", "(_) @any" },
        { "json", "_ @any" },
        { "json", "(array (number)* @n)" },
        { "json", "(array (number)+ @n)" },
        { "json", "(array (number)? @n)" },
        { "json", "(array ((number) @a (string) @b)*)" },
        { "json", "[(number) (string)] @literal" },
        { "json", "[(number) @n (string) @s (true) @t]" },
        { "json", "(pair key: (_) @k !value)" },
        { "json", "\"[\" @bracket" },
        { "json", "[\"{\" \"}\" \"[\" \"]\"] @punctuation" },
        { "json", "(ERROR) @error" },
        { "json", "(MISSING) @missing" },
        { "json", "(MISSING \"]\") @missing" },
        { "json", "((string) @s (#eq? @s \"x\"))" },
        { "json", "((string) @a (string) @b (#not-eq? @a @b))" },
        { "json", "((string) @s (#match? @s \"^[a-z]+$\"))" },
        { "json", "((string) @s (#set! kind \"text\") (#any-of? @s \"a\" \"b\" \"c\"))" },
        { "json", "((string) @s (#eq? @s \"tab\\tnew\\nline\\\\quote\\\"\"))" },
        { "json", "; a comment\n(number) @n ; another\n(string) @s" },
        { "json", "(pair\n  key: (string) @key\n  value: [(number) (string)] @value)" },
        { "json", "(object (pair key: (string) @k) @p (#eq? @k \"x\")) @o" },
        { "c", "(function_definition declarator: (function_declarator declarator: (identifier) @name))" },
        { "c", "(call_expression function: (identifier) @f arguments: (argument_list (_)* @args))" },
        { "c", "(expression/identifier) @expr_identifier" },
        { "c", "(_expression) @expr" },
        { "c", "[\"if\" \"else\" \"while\" \"for\" \"return\"] @keyword" },
        { "c", "(compound_statement . (_) @first (_) @last .)" },
        { "c", "(binary_expression left: (_) @l operator: _ @op right: (_) @r)" },
        { "c", "((identifier) @constant (#match? @constant \"^[A-Z][A-Z0-9_]*$\"))" },
        { "c", "(declaration type: (_) @t declarator: (identifier) @d !default_value)" },
        { "c", "(translation_unit (comment)* @comments (function_definition) @f)" },
        { "python", "(function_definition name: (identifier) @name body: (block . (expression_statement (string)) @doc))" },
        { "python", "(call function: (attribute object: (identifier) @obj attribute: (identifier) @method))" },
        { "python", "[(true) (false) (none)] @constant.builtin" },
        { "python", "(decorated_definition (decorator)+ @decorators definition: (_) @def)" },

        // Errors: syntax, node type, field, capture, structure.
        { "json", "(pair" },
        { "json", "(pair))" },
        { "json", "(pair key: )" },
        { "json", "(pair @)" },
        { "json", "\"unterminated" },
        { "json", "(no_such_node) @x" },
        { "json", "\"no-such-token\" @x" },
        { "json", "(pair no_such_field: (string))" },
        { "json", "((string) @s (#eq? @t \"x\"))" },
        { "json", "(pair value: (pair))" },
        { "json", "(number (string))" },
        { "json", "[(number) (pair key: (number))]" },
        { "json", "((string) @s (#eq? @s" },
        { "json", "(array . )" },
        { "json", "(array (number) @a @b @c @d @e)" },
        { "json", "(MISSING no_such_node)" },
        { "json", "(expression/number) @x" },
        { "c", "(expression/no_such_node) @x" },
        { "c", "(function_definition body: (identifier))" },
        { "c", "(binary_expression operator: (identifier))" },
        { "json", "[(number) (string)]* @lit" },
        { "json", "(array (number)? (string)? @s)" },
        { "json", "(object (pair key: (string) @k value: (string) @v) @o)" },
        { "c", "(function_definition declarator: (function_declarator) @d body: (compound_statement) @b)" },
        { "python", "(block (expression_statement) @stmt)" },
    };

    [OracleTheory]
    [MemberData(nameof(QueryFiles))]
    public void QueryFileMatchesOracle(string grammar, string file)
    {
        NativeOracle.EnsureAvailable();
        string directory = grammar switch { "json" => "Json", "c" => "C", _ => "Python" };
        byte[] source = File.ReadAllBytes(Path.Combine(FindRoot(), "grammars", directory, "upstream", "queries", file));
        AssertSame(grammar, file, source);
    }

    [OracleTheory]
    [MemberData(nameof(Queries))]
    public void QueryMatchesOracle(string grammar, string query)
    {
        NativeOracle.EnsureAvailable();
        AssertSame(grammar, query, Encoding.UTF8.GetBytes(query));
    }

    /// <summary>Random edits of the query files reach the error paths and the odd corners.</summary>
    [OracleTheory]
    [InlineData("json", "Json", 1000)]
    [InlineData("c", "C", 2000)]
    [InlineData("python", "Python", 2000)]
    [InlineData("go", "Go", 1000)]
    [InlineData("java", "Java", 1000)]
    [InlineData("css", "Css", 1000)]
    [InlineData("html", "Html", 1000)]
    [InlineData("javascript", "JavaScript", 1000)]
    [InlineData("rust", "Rust", 1000)]
    [InlineData("cpp", "Cpp", 1000)]
    [InlineData("c_sharp", "CSharp", 1000)]
    [InlineData("bash", "Bash", 1000)]
    [InlineData("toml", "Toml", 1000)]
    [InlineData("typescript", "TypeScript", 1000)]
    [InlineData("tsx", "Tsx", 1000)]
    [InlineData("yaml", "Yaml", 1000)]
    [InlineData("markdown_inline", "MarkdownInline", 1000)]
    [InlineData("markdown", "Markdown", 1000)]
    public void MutatedQueriesMatchOracle(string grammar, string directory, int count)
    {
        NativeOracle.EnsureAvailable();
        string path = Path.Combine(FindRoot(), "grammars", directory, "upstream", "queries");
        List<byte[]> sources = Directory.GetFiles(path, "*.scm").Order(StringComparer.Ordinal)
            .Select(File.ReadAllBytes)
            .ToList();
        string[] pieces =
        [
            "(", ")", "[", "]", "@", "@x", ".", "_", "*", "+", "?", "!", ":", "\"", "#eq?", "#match?",
            "(#set! a \"b\")", "ERROR", "MISSING", "field:", " ", "\n", ";", "/",
        ];
        int errors = 0;
        int checkedCount = 0;
        for (int seed = 0; seed < count; seed++)
        {
            Random random = new Random(seed);
            byte[] source = Mutate(random, sources[random.Next(sources.Count)], pieces);
            if (HasFieldBeforePredicate(source))
            {
                continue;
            }

            string expected = AssertSame(grammar, "seed " + seed.ToString(CultureInfo.InvariantCulture), source);
            checkedCount++;
            if (expected.StartsWith("error", StringComparison.Ordinal))
            {
                errors++;
            }
        }

        // Both kinds must be common: queries with errors and queries that compile.
        Assert.True(checkedCount > count * 9 / 10, $"only {checkedCount} of {count} queries were checked");
        Assert.True(
            errors > checkedCount / 10 && errors < checkedCount * 19 / 20,
            $"{errors} of {checkedCount} queries have errors");
    }

    /// <summary>
    /// A field followed by a predicate, as in `function: (#eq? ...)`, makes ts_query__parse_pattern
    /// of v0.27.2 read past the end of the steps (array_get at the "Add the field name" step).
    /// The native oracle is built with asserts and aborts there; without asserts the C code
    /// has undefined behavior, which the port does not copy. Such queries are skipped.
    /// </summary>
    private static bool HasFieldBeforePredicate(byte[] source)
    {
        string text = Encoding.UTF8.GetString(source);
        return System.Text.RegularExpressions.Regex.IsMatch(text, @":\s*\(\s*#");
    }

    private static byte[] Mutate(Random random, byte[] source, string[] pieces)
    {
        List<byte> bytes = new List<byte>(source);
        for (int e = random.Next(1, 4); e > 0; e--)
        {
            int position = random.Next(bytes.Count + 1);
            if (random.Next(2) == 0 && bytes.Count > 0)
            {
                int start = random.Next(bytes.Count);
                bytes.RemoveRange(start, Math.Min(random.Next(1, 6), bytes.Count - start));
            }
            else
            {
                bytes.InsertRange(position, Encoding.UTF8.GetBytes(pieces[random.Next(pieces.Length)]));
            }
        }

        return bytes.ToArray();
    }

    private static string AssertSame(string grammar, string name, byte[] source)
    {
        string expected = NativeOracle.OracleQuery(grammar, source);
        string actual = QueryRunner.Run(grammar, source);
        if (expected == actual)
        {
            return expected;
        }

        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
        {
            i++;
        }

        Assert.Fail(
            $"{grammar} '{name}': first difference at line {i + 1}: oracle '{(i < e.Length ? e[i] : "<end>")}', " +
            $"managed '{(i < a.Length ? a[i] : "<end>")}'\nquery: {Encoding.UTF8.GetString(source)}");
        return expected;
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "TreeSitter.Managed.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
