using System.Text;
using TreeSitter.Tests.Parsing;
using TreeSitter.Tests.Subtrees;
using Xunit;

namespace TreeSitter.Tests.Queries;

/// <summary>
/// The predicate layer of the port (QueryPredicates.cs and QueryCursorPredicates.cs), which is
/// the binding layer of tree-sitter v0.27.2 (lib/binding_rust/lib.rs) and not the C runtime. Most
/// of the tests here are ports of the predicate tests of crates/cli/src/tests/query_test.rs of the
/// same version and keep the expected values of that file; where such a test uses a node type that
/// only javascript or java has, the C node type of the same shape is used and the comment says so.
/// The other tests use expected lists built from the C runtime itself: the matches and captures
/// that ts_query_cursor gives for the same query and source (the raw side, which QueryExecTests
/// checks against the native library), with the ones that do not satisfy the text predicates of the
/// pattern taken away.
/// </summary>
public class QueryPredicateTests
{
    private const string JsonObject = "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }";

    /// <summary>
    /// test_query_errors_on_invalid_predicates (javascript; its three patterns use only
    /// (identifier), which the C grammar has too). The first and the third error come from
    /// ts_query_new, so the port reports them the way the C code does: an error type and a byte
    /// offset, where Rust turns an offset into a row and a column of its own. The second one is a
    /// predicate error, which TSQueryError has no code for: its message and its row are the Rust
    /// ones, and its type is QueryError.Syntax, the nearest of the ones that exist.
    /// </summary>
    [Fact]
    public void InvalidPredicateErrors()
    {
        Language language = ScriptLanguages.Get("c")!;

        QueryException syntax = Assert.Throws<QueryException>(
            () => new Query(language, "((identifier) @id (@id))"));
        Assert.Equal(QueryError.Syntax, syntax.Error);
        Assert.Equal(19u, syntax.Offset);

        QueryException predicate = Assert.Throws<QueryException>(
            () => new Query(language, "((identifier) @id (#eq? @id))"));
        Assert.Equal(QueryError.Syntax, predicate.Error);
        Assert.Equal(0u, predicate.Offset);
        Assert.Equal(0u, predicate.Row);
        Assert.Equal("Wrong number of arguments to #eq? predicate. Expected 2, got 1.", predicate.Message);

        QueryException capture = Assert.Throws<QueryException>(
            () => new Query(language, "((identifier) @id (#eq? @id @ok))"));
        Assert.Equal(QueryError.Capture, capture.Error);
        Assert.Equal(29u, capture.Offset);
    }

    /// <summary>
    /// Every message the Rust binding produces for a predicate it cannot build, one query each.
    /// The query has to be valid for the C runtime, which is what makes a predicate error possible
    /// at all: ts_query_new takes any shape of predicate it does not know, and the binding is the
    /// one that counts the arguments, checks the argument kinds and compiles the regular
    /// expression. The row is the line the pattern starts on (Rust: the '\n' count before
    /// ts_query_start_byte_for_pattern), and the offset stays 0, as in Rust.
    /// </summary>
    [Theory]
    [InlineData("((identifier) @x (#eq? @x))", 0u, "Wrong number of arguments to #eq? predicate. Expected 2, got 1.")]
    [InlineData("((identifier) @x (#eq?))", 0u, "Wrong number of arguments to #eq? predicate. Expected 2, got 0.")]
    [InlineData("((identifier) @x (#eq? @x @x @x))", 0u, "Wrong number of arguments to #eq? predicate. Expected 2, got 3.")]
    [InlineData("((identifier) @x (#not-eq? \"a\" @x))", 0u, "First argument to #eq? predicate must be a capture name. Got literal \"a\".")]
    [InlineData("((identifier) @x (#match? @x))", 0u, "Wrong number of arguments to #match? predicate. Expected 2, got 1.")]
    [InlineData("((identifier) @x (#any-match? \"a\" @x))", 0u, "First argument to #match? predicate must be a capture name. Got literal \"a\".")]
    [InlineData("((identifier) @x (#match? @x @x))", 0u, "Second argument to #match? predicate must be a literal. Got capture @x.")]
    [InlineData("((identifier) @x (#match? @x \"(\"))", 0u, "Invalid regex '('")]
    [InlineData("((identifier) @x (#set!))", 0u, "Wrong number of arguments to set! predicate. Expected 1 to 3, got 0.")]
    [InlineData("((identifier) @x (#set! a b c d))", 0u, "Wrong number of arguments to set! predicate. Expected 1 to 3, got 4.")]
    [InlineData("((identifier) @x (#set! @x @x a))", 0u, "Invalid arguments to set! predicate. Unexpected second capture name @x")]
    [InlineData("((identifier) @x (#is? a b c))", 0u, "Invalid arguments to is? predicate. Unexpected third argument @c")]
    [InlineData("((identifier) @x (#is-not? @x))", 0u, "Invalid arguments to is-not? predicate. Missing key argument")]
    [InlineData("((identifier) @x (#any-of?))", 0u, "Wrong number of arguments to #any-of? predicate. Expected at least 1, got 0.")]
    [InlineData("((identifier) @x (#any-of? \"a\"))", 0u, "First argument to #any-of? predicate must be a capture name. Got literal \"a\".")]
    [InlineData("((identifier) @x (#not-any-of? @x @x))", 0u, "Arguments to #any-of? predicate must be literals. Got capture @x.")]
    [InlineData("\n\n((identifier) @x (#eq? @x))", 2u, "Wrong number of arguments to #eq? predicate. Expected 2, got 1.")]
    public void PredicateErrorMessages(string query, uint row, string message)
    {
        Language language = ScriptLanguages.Get("c")!;
        QueryException error = Assert.Throws<QueryException>(() => new Query(language, query));
        Assert.Equal(QueryError.Syntax, error.Error);
        Assert.Equal(row, error.Row);
        Assert.Equal(0u, error.Offset);
        Assert.Equal(message, error.Message);

        // The same query through TryCreate, which is Query::new_raw and does no predicate
        // analysis: it compiles and reports nothing, and the error shows up when the predicates
        // of the query are read.
        Query compiled = Query.TryCreate(language, Encoding.UTF8.GetBytes(query), out _, out QueryError compileError)!;
        Assert.Equal(QueryError.None, compileError);
        Assert.Throws<QueryException>(() => ReadPredicates(compiled));
    }

    /// <summary>
    /// test_query_captures_with_predicates (javascript): the property settings, the property
    /// predicates and the general predicates of a pattern. The second pattern of the Rust test is
    /// (property_identifier), which the C grammar has as field_identifier; the ids of the captures
    /// are query-wide and stay the same.
    /// </summary>
    [Fact]
    public void PropertyAndGeneralPredicates()
    {
        Language language = ScriptLanguages.Get("c")!;
        Query query = new Query(language, "((call_expression (identifier) @foo)\n (#set! name something)\n (#set! cool)\n (#something! @foo omg))\n\n((field_identifier) @bar\n (#is? cool)\n (#is-not? name something))");

        Assert.Equal(
            new[]
            {
                new QueryProperty("name", "something", null),
                new QueryProperty("cool", null, null),
            },
            query.PropertySettings(0).ToArray());
        Assert.Equal(
            new[]
            {
                new QueryPredicate(
                    "something!",
                    new[]
                    {
                        QueryPredicateArg.FromCapture(0),
                        QueryPredicateArg.FromString("omg"),
                    }),
            },
            query.GeneralPredicates(0).ToArray());
        Assert.Empty(query.PropertySettings(1).ToArray());
        Assert.Empty(query.PropertyPredicates(0).ToArray());
        Assert.Equal(
            new[]
            {
                new QueryPropertyPredicate(new QueryProperty("cool", null, null), true),
                new QueryPropertyPredicate(new QueryProperty("name", "something", null), false),
            },
            query.PropertyPredicates(1).ToArray());
    }

    /// <summary>
    /// test_query_captures_with_quoted_predicate_args (javascript, as it is): a double-quoted
    /// predicate argument holds the escape sequences \n and \r, an escaped quote with \" and a
    /// literal backslash with \\, and an unknown escape keeps its backslash.
    /// </summary>
    [Fact]
    public void QuotedPredicateArgs()
    {
        Language language = ScriptLanguages.Get("c")!;
        Query query = new Query(language, "((call_expression (identifier) @foo)\n (#set! one \"\\\"something\\ngreat\\\"\"))\n\n((identifier)\n (#set! two \"\\\\s(\\r?\\n)*$\"))\n\n((function_definition)\n (#set! three \"\\\"something\\ngreat\\\"\"))\n");

        Assert.Equal(
            new[]
            {
                new QueryProperty("one", "\"something\ngreat\"", null),
            },
            query.PropertySettings(0).ToArray());
        Assert.Equal(
            new[]
            {
                new QueryProperty("two", "\\s(\r?\n)*$", null),
            },
            query.PropertySettings(1).ToArray());
        Assert.Equal(
            new[]
            {
                new QueryProperty("three", "\"something\ngreat\"", null),
            },
            query.PropertySettings(2).ToArray());
    }

    /// <summary>
    /// The matching half of test_query_captures_with_predicates (javascript, source
    /// "const a = window.b"): #match? keeps only the one identifier it names, and #is-not? is a
    /// property predicate, which the binding does not apply while matching, so it changes nothing.
    /// The same pattern on "int a = window.b;" with the C grammar keeps window and drops a, and
    /// the pattern without the predicate keeps both.
    /// </summary>
    [Fact]
    public void MatchesFilterByTheTextOfTheCapturedNodes()
    {
        AssertMatches(
            "c",
            "((identifier) @variable.builtin\n  (#match? @variable.builtin \"^(arguments|module|console|window|document)$\")\n  (#is-not? local))",
            "int a = window.b;",
            Match(0, Capture("variable.builtin", "window")));
        AssertMatches(
            "c",
            "((identifier) @variable.builtin)",
            "int a = window.b;",
            Match(0, Capture("variable.builtin", "a")),
            Match(0, Capture("variable.builtin", "window")));
    }

    /// <summary>
    /// test_query_quantified_captures (C, as in Rust): with #match? a quantified capture keeps a
    /// match only when every node it captured matches, #any-match? is satisfied by one of them,
    /// and a capture with no predicate of its own keeps all of them.
    /// </summary>
    [Fact]
    public void QuantifiedCapturesWithMatchAndAnyMatch()
    {
        AssertCaptures(
            "c",
            "((comment)+ @comment.documentation\n  (#match? @comment.documentation \"^///\"))",
            "/// foo\n/// bar\n/// baz\n\nvoid main() {}\n\n/// qux\n/// quux\n// quuz\n",
            Capture("comment.documentation", "/// foo"),
            Capture("comment.documentation", "/// bar"),
            Capture("comment.documentation", "/// baz"));
        AssertCaptures(
            "c",
            "((comment)+ @comment.documentation\n  (#any-match? @comment.documentation \"^///\"))",
            "/// foo\n/// bar\n/// baz\n\nvoid main() {}\n\n/// qux\n/// quux\n// quuz\n",
            Capture("comment.documentation", "/// foo"),
            Capture("comment.documentation", "/// bar"),
            Capture("comment.documentation", "/// baz"),
            Capture("comment.documentation", "/// qux"),
            Capture("comment.documentation", "/// quux"),
            Capture("comment.documentation", "// quuz"));
        AssertCaptures(
            "c",
            "((comment) ?+ @comment)",
            "// foo\n// bar\n// baz\n",
            Capture("comment", "// foo"),
            Capture("comment", "// foo"),
            Capture("comment", "// foo"),
            Capture("comment", "// bar"),
            Capture("comment", "// baz"));
    }

    /// <summary>
    /// test_query_with_first_child_in_group_is_anchor (C, as in Rust): the . anchor of the first
    /// child of a node, with a #match? on the capture of that child.
    /// </summary>
    [Fact]
    public void AnchorBeforeTheFirstChildWithAPredicate()
    {
        AssertMatches(
            "c",
            "(parameter_list\n  .\n  ((parameter_declaration) @constant\n    (#match? @constant \"^int\")))",
            "void fun(int a, char b, int c) { };",
            Match(0, Capture("constant", "int a")));
    }

    /// <summary>
    /// test_query_alternative_predicate_prefix (C, as in Rust): .eq? is the same predicate as
    /// #eq?, only with the other prefix, and a literal it does not match rejects the match.
    /// </summary>
    [Fact]
    public void PredicateWithTheAlternativePrefix()
    {
        AssertMatches(
            "c",
            "((call_expression\n  function: (identifier) @keyword\n  arguments: (argument_list (string_literal) @function))\n (.eq? @keyword \"DEFUN\"))",
            " DEFUN (\"identity\", Fidentity, Sidentity, 1, 1, 0, doc: /* x */ attributes: const) (Lisp_Object arg) { return arg; }",
            Match(0, Capture("keyword", "DEFUN"), Capture("function", "\"identity\"")));
        AssertMatches(
            "c",
            "((call_expression\n  function: (identifier) @keyword\n  arguments: (argument_list (string_literal) @function))\n (.eq? @keyword \"OTHER\"))",
            " DEFUN (\"identity\", Fidentity, Sidentity, 1, 1, 0, doc: /* x */ attributes: const) (Lisp_Object arg) { return arg; }");
    }

    /// <summary>
    /// The text predicates of the binding, one assertion per operator, on one JSON document. The
    /// Rust tests cover #eq?, #not-eq?, #match? and .eq? (above); these are the same shapes for
    /// the operators those tests do not use, and the any- variants. An any- predicate only ever
    /// confirms a match - it returns true as soon as one captured node works and true at the end
    /// of the list - so a match whose single captured node does not work is kept: failing all of
    /// them is the only way to reject one. #eq? of a capture against itself and against another
    /// capture are the capture-to-capture form, where the two lists of nodes have to be equally
    /// long as well as equal element by element.
    /// </summary>
    [Fact]
    public void EveryTextPredicateShapesTheSameSource()
    {
        AssertMatches(
            "json",
            "((object (pair key: (string) @key) @pair) (#eq? @key \"\\\"aa\\\"\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("key", "\"aa\"")));
        AssertMatches(
            "json",
            "((object (pair key: (string) @key) @pair) (#not-eq? @key \"\\\"aa\\\"\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("key", "\"bb\"")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("key", "\"cc\"")));
        AssertMatches(
            "json",
            "((object (pair key: (string) @key) @pair) (#any-eq? @key \"\\\"aa\\\"\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("key", "\"aa\"")),
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("key", "\"bb\"")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("key", "\"cc\"")));
        AssertMatches(
            "json",
            "((object (pair key: (string) @key) @pair) (#any-not-eq? @key \"\\\"aa\\\"\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("key", "\"aa\"")),
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("key", "\"bb\"")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("key", "\"cc\"")));
        AssertMatches(
            "json",
            "((object (pair key: (string) @key) @pair) (#eq? @key @key))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("key", "\"aa\"")),
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("key", "\"bb\"")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("key", "\"cc\"")));
        AssertMatches(
            "json",
            "((object (pair key: (string) @key value: (_) @value) @pair) (#eq? @key @value))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }");
        AssertMatches(
            "json",
            "((object (pair key: (string) @key value: (_) @value) @pair) (#not-eq? @key @value))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("key", "\"aa\""), Capture("value", "1")),
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("key", "\"bb\""), Capture("value", "\"aa\"")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("key", "\"cc\""), Capture("value", "3")));
        AssertMatches(
            "json",
            "((object (pair key: (string) @key value: (_) @value) @pair) (#any-eq? @key @value))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("key", "\"aa\""), Capture("value", "1")),
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("key", "\"bb\""), Capture("value", "\"aa\"")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("key", "\"cc\""), Capture("value", "3")));
        AssertMatches(
            "json",
            "((object (pair value: (_) @number) @pair) (#match? @number \"^[0-9]+$\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("number", "1")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("number", "3")));
        AssertMatches(
            "json",
            "((object (pair value: (_) @value) @pair) (#not-match? @value \"^[0-9]+$\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("value", "\"aa\"")));
        AssertMatches(
            "json",
            "((object (pair value: (_) @value) @pair) (#any-match? @value \"^[0-9]+$\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("value", "1")),
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("value", "\"aa\"")),
            Match(0, Capture("pair", "\"cc\": 3"), Capture("value", "3")));
        AssertMatches(
            "json",
            "((object (pair value: (_) @value) @pair) (#any-of? @value \"1\" \"\\\"aa\\\"\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"aa\": 1"), Capture("value", "1")),
            Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("value", "\"aa\"")));
        AssertMatches(
            "json",
            "((object (pair value: (_) @value) @pair) (#not-any-of? @value \"1\" \"\\\"aa\\\"\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Match(0, Capture("pair", "\"cc\": 3"), Capture("value", "3")));
    }

    /// <summary>
    /// The captures of a match that the text predicates reject are gone, both of them, while the
    /// captures of the other matches stay: this is the QueryCaptures iterator of Rust, which also
    /// calls remove(match.id) so that the cursor forgets the rejected match. The first pattern
    /// here keeps one pair and the second keeps the other two.
    /// </summary>
    [Fact]
    public void CapturesOfARejectedMatchAreGone()
    {
        AssertCaptures(
            "json",
            "((object (pair key: (string) @key value: (_) @value)) (#eq? @key \"\\\"bb\\\"\"))\n((object (pair key: (string) @other)) (#not-eq? @other \"\\\"bb\\\"\"))",
            "{ \"aa\": 1, \"bb\": \"aa\", \"cc\": 3 }",
            Capture("other", "\"aa\""),
            Capture("key", "\"bb\""),
            Capture("value", "\"aa\""),
            Capture("other", "\"cc\""));
    }

    /// <summary>
    /// An invalid regular expression is an error of the binding, not of the C runtime: the query
    /// compiles, so TryCreate returns it and says nothing (like Query::new_raw), while the
    /// constructor throws (like Query::new). The query that TryCreate gave keeps the error and
    /// reports it when its predicates are read or when it is run.
    /// </summary>
    [Fact]
    public void AnInvalidRegexIsAQueryErrorOfTheBindingOnly()
    {
        Language language = ScriptLanguages.Get("c")!;
        const string Source = "((identifier) @x (#match? @x \"[\"))";

        QueryException error = Assert.Throws<QueryException>(() => new Query(language, Source));
        Assert.Equal(QueryError.Syntax, error.Error);
        Assert.Equal(0u, error.Row);
        Assert.Equal("Invalid regex '['", error.Message);

        byte[] sourceBytes = Encoding.UTF8.GetBytes(Source);
        Query query = Query.TryCreate(language, sourceBytes, out _, out QueryError compileError)!;
        Assert.Equal(QueryError.None, compileError);
        Assert.Throws<QueryException>(() => ReadPredicates(query));

        Parser parser = new Parser();
        parser.SetLanguage(language);
        Tree tree = parser.ParseString("a;")!;
        QueryCursor cursor = new QueryCursor();
        Assert.Throws<QueryException>(() => cursor.Matches(query, tree.RootNode, sourceBytes));
        Assert.Throws<QueryException>(() => cursor.Captures(query, tree.RootNode, sourceBytes));
    }

    /// <summary>
    /// The text of a node can also come from a QueryTextProvider, the shape of Rust's TextProvider
    /// (test_query_text_callback_returns_chunks checks the chunk-by-chunk form for javascript).
    /// Both providers here give the same text for every node, one from the source and one through
    /// a copy, and both runs of the query give the same single match.
    /// </summary>
    [Fact]
    public void ATextProviderCanSupplyTheNodeText()
    {
        Language language = ScriptLanguages.Get("json")!;
        byte[] source = Encoding.UTF8.GetBytes(JsonObject);
        Parser parser = new Parser();
        parser.SetLanguage(language);
        Tree tree = parser.ParseString(source)!;
        // One capture only, so that the expectation below lists the whole match.
        Query query = new Query(language, "((object (pair value: (_) @value)) (#eq? @value \"1\"))");
        byte[] Copy(Node node) => source[(int)node.StartByte..(int)node.EndByte];

        List<string> direct = new List<string>();
        foreach (QueryMatch match in new QueryCursor().Matches(query, tree.RootNode, source))
        {
            direct.Add(MatchText(query, match, source));
        }

        List<string> copied = new List<string>();
        foreach (QueryMatch match in new QueryCursor().Matches(query, tree.RootNode, node => new ReadOnlyMemory<byte>(Copy(node))))
        {
            copied.Add(MatchText(query, match, source));
        }

        Assert.Equal(new[] { Match(0, Capture("value", "1")) }, direct);
        Assert.Equal(direct, copied);
    }

    /// <summary>
    /// A cursor used twice: the predicate check does not touch the state of the cursor, so the
    /// same execution through the same cursor again gives the same filtered matches, and the
    /// second one starts fresh because Matches runs ts_query_cursor_exec again.
    /// </summary>
    [Fact]
    public void AReusedCursorRunsTheQueryAgain()
    {
        Language language = ScriptLanguages.Get("json")!;
        byte[] source = Encoding.UTF8.GetBytes(JsonObject);
        Query query = new Query(
            language,
            "((object (pair key: (string) @key) @pair) (#not-eq? @key \"\\\"aa\\\"\"))");
        Parser parser = new Parser();
        parser.SetLanguage(language);
        Tree tree = parser.ParseString(source)!;
        QueryCursor cursor = new QueryCursor();

        List<string> first = new List<string>();
        foreach (QueryMatch match in cursor.Matches(query, tree.RootNode, source))
        {
            first.Add(MatchText(query, match, source));
        }

        List<string> second = new List<string>();
        foreach (QueryMatch match in cursor.Matches(query, tree.RootNode, source))
        {
            second.Add(MatchText(query, match, source));
        }

        Assert.Equal(
            new[]
            {
                Match(0, Capture("pair", "\"bb\": \"aa\""), Capture("key", "\"bb\"")),
                Match(0, Capture("pair", "\"cc\": 3"), Capture("key", "\"cc\"")),
            },
            first);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// The two iterator types are sequences in both shapes the Rust binding gives them: the
    /// generic one, which foreach uses, and the plain System.Collections.IEnumerable one, which a
    /// non-generic consumer sees (the explicit implementation of the interface). The query keeps
    /// one of the three pairs of the document, with its two captures.
    /// </summary>
    [Fact]
    public void TheIteratorsEnumerateWithoutAGenericTypeToo()
    {
        Language language = ScriptLanguages.Get("json")!;
        byte[] source = Encoding.UTF8.GetBytes(JsonObject);
        Query query = new Query(
            language,
            "((object (pair key: (string) @key) @pair) (#eq? @key \"\\\"bb\\\"\"))");
        Parser parser = new Parser();
        parser.SetLanguage(language);
        Tree tree = parser.ParseString(source)!;
        QueryCursor cursor = new QueryCursor();

        int matchCount = 0;
        foreach (object match in (System.Collections.IEnumerable)cursor.Matches(query, tree.RootNode, source))
        {
            Assert.IsType<QueryMatch>(match);
            matchCount++;
        }

        Assert.Equal(1, matchCount);

        int captureCount = 0;
        List<string> names = new List<string>();
        foreach (object capture in (System.Collections.IEnumerable)cursor.Captures(query, tree.RootNode, source))
        {
            QueryCaptureMatch pair = Assert.IsType<QueryCaptureMatch>(capture);
            names.Add(query.CaptureNameForId(pair.Capture.Index));
            captureCount++;
        }

        Assert.Equal(2, captureCount);
        Assert.Equal(new[] { "pair", "key" }, names);
    }

    /// <summary>
    /// The query files of the three grammars on every entry of their own corpus, run through the
    /// predicate layer: they compile with no exception of their own, every result of the filtered
    /// enumeration is a result of the raw cursor, and the predicates do reject something (the
    /// highlights of C and Python ask for a #match? on the name of a constant).
    /// </summary>
    [Theory]
    [InlineData("json", "Json")]
    [InlineData("c", "C")]
    [InlineData("python", "Python")]
    public void QueryFilesOnTheCorpusRunWithPredicates(string grammar, string directory)
    {
        Language language = ScriptLanguages.Get(grammar)!;
        List<byte[]> queryFiles = Directory
            .GetFiles(Path.Combine(FindRoot(), "grammars", directory, "upstream", "queries"), "*.scm")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllBytes)
            .ToList();
        int matches = 0;
        int captures = 0;
        int rejected = 0;

        // Whether the query files ask for a text at all: the JSON grammar has no predicate in
        // its highlights.scm, so there is nothing to reject there.
        bool asksForText = queryFiles.Any(bytes =>
        {
            string text = Encoding.UTF8.GetString(bytes);
            return text.Contains("#match?", StringComparison.Ordinal) ||
                text.Contains("#eq?", StringComparison.Ordinal) ||
                text.Contains("any-of?", StringComparison.Ordinal) ||
                text.Contains("any-match?", StringComparison.Ordinal);
        });

        foreach (byte[] source in ParserCorpusTests.ReadCorpus(directory).Select(entry => Encoding.UTF8.GetBytes(entry.Input)))
        {
            Parser parser = new Parser();
            parser.SetLanguage(language);
            Tree tree = parser.ParseString(source)!;

            foreach (byte[] querySource in queryFiles)
            {
                // The constructor, so that a predicate the binding cannot build fails the test.
                Query query = new Query(language, querySource);

                QueryCursor raw = new QueryCursor();
                HashSet<string> rawMatches = new HashSet<string>();
                raw.Exec(query, tree.RootNode);
                while (raw.NextMatch(out QueryMatch match))
                {
                    rawMatches.Add(MatchText(query, match, source));
                }

                HashSet<string> filteredMatches = new HashSet<string>(CollectMatches(query, source));
                Assert.True(rawMatches.IsSupersetOf(filteredMatches), "a match that the raw cursor does not give");
                matches += filteredMatches.Count;
                rejected += rawMatches.Count - filteredMatches.Count;

                HashSet<string> rawCaptures = new HashSet<string>();
                raw.Exec(query, tree.RootNode);
                while (raw.NextCapture(out QueryMatch captureMatch, out uint captureIndex))
                {
                    rawCaptures.Add(CaptureText(query, captureMatch.Captures[(int)captureIndex], source));
                }

                HashSet<string> filteredCaptures = new HashSet<string>(CollectCaptures(query, source));
                Assert.True(rawCaptures.IsSupersetOf(filteredCaptures), "a capture that the raw cursor does not give");
                captures += filteredCaptures.Count;
            }
        }

        Assert.True(matches > 0, "no match at all");
        Assert.True(captures > 0, "no capture at all");
        if (asksForText)
        {
            Assert.True(
                rejected > 0,
                $"no match was rejected by a predicate ({matches} matches, {captures} captures)");
        }
    }

    // The three public accessors of the predicates of a pattern, for the Assert.Throws above.
    private static void ReadPredicates(Query query)
    {
        Assert.NotNull(query.GeneralPredicates(0).ToArray());
        Assert.NotNull(query.PropertySettings(0).ToArray());
        Assert.NotNull(query.PropertyPredicates(0).ToArray());
    }

    private static string Text(Node node, byte[] source) =>
        Encoding.UTF8.GetString(source, (int)node.StartByte, (int)(node.EndByte - node.StartByte));

    private static string Escape(string text) =>
        text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Capture(string name, string text) => $"(\"{name}\", \"{Escape(text)}\")";

    private static string Match(uint pattern, params string[] captures) =>
        $"{pattern}: {string.Join(", ", captures)}";

    private static string CaptureText(Query query, QueryCapture capture, byte[] source) =>
        Capture(query.CaptureNameForId(capture.Index), Text(capture.Node, source));

    private static string MatchText(Query query, QueryMatch match, byte[] source) =>
        Match(match.PatternIndex, Texts(query, match, source).ToArray());

    private static IEnumerable<string> Texts(Query query, QueryMatch match, byte[] source)
    {
        foreach (QueryCapture capture in match.Captures)
        {
            yield return CaptureText(query, capture, source);
        }
    }

    // The matches of a query on a source, as collect_matches of the Rust test helpers builds them.
    private static void AssertMatches(string grammar, string query, string source, params string[] expected)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        Query compiled = new Query(ScriptLanguages.Get(grammar)!, query);
        Assert.Equal(expected, CollectMatches(compiled, sourceBytes));
    }

    // The captures of a query on a source, as collect_captures of the Rust test helpers does.
    private static void AssertCaptures(string grammar, string query, string source, params string[] expected)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        Query compiled = new Query(ScriptLanguages.Get(grammar)!, query);
        Assert.Equal(expected, CollectCaptures(compiled, sourceBytes));
    }

    private static List<string> CollectMatches(Query query, byte[] source)
    {
        Parser parser = new Parser();
        parser.SetLanguage(query.Language);
        Tree tree = parser.ParseString(source)!;
        QueryCursor cursor = new QueryCursor();
        List<string> result = new List<string>();
        foreach (QueryMatch match in cursor.Matches(query, tree.RootNode, source))
        {
            result.Add(MatchText(query, match, source));
        }

        return result;
    }

    private static List<string> CollectCaptures(Query query, byte[] source)
    {
        Parser parser = new Parser();
        parser.SetLanguage(query.Language);
        Tree tree = parser.ParseString(source)!;
        QueryCursor cursor = new QueryCursor();
        List<string> result = new List<string>();
        foreach (QueryCaptureMatch capture in cursor.Captures(query, tree.RootNode, source))
        {
            result.Add(CaptureText(query, capture.Capture, source));
        }

        return result;
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
