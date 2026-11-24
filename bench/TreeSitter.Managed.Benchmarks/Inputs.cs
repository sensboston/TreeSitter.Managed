using System.Globalization;
using System.Text;
using TreeSitter.Grammars.C;
using TreeSitter.Grammars.Json;
using TreeSitter.Grammars.Python;

namespace TreeSitter.Benchmarks;

/// <summary>The benchmark inputs: one realistic source per grammar, the same bytes for the port and for native.</summary>
internal static class Inputs
{
    public static string RepositoryRoot { get; } = FindRoot();

    public static Language Language(string grammar) => grammar switch
    {
        "json" => JsonGrammar.Language,
        "c" => CGrammar.Language,
        "python" => PythonGrammar.Language,
        _ => throw new ArgumentOutOfRangeException(nameof(grammar)),
    };

    /// <summary>The source of a grammar: C - query.c of the runtime (170 KB); JSON and Python - generated (about 200 KB).</summary>
    public static byte[] Source(string grammar) => grammar switch
    {
        "json" => Encoding.UTF8.GetBytes(Json()),
        "c" => File.ReadAllBytes(Path.Combine(RepositoryRoot, "tests", "Oracle", "tree-sitter", "lib", "src", "query.c")),
        "python" => Encoding.UTF8.GetBytes(Python()),
        _ => throw new ArgumentOutOfRangeException(nameof(grammar)),
    };

    public static byte[] HighlightsQuery(string grammar)
    {
        string directory = grammar switch
        {
            "json" => "Json",
            "c" => "C",
            "python" => "Python",
            _ => throw new ArgumentOutOfRangeException(nameof(grammar)),
        };
        return File.ReadAllBytes(Path.Combine(RepositoryRoot, "grammars", directory, "upstream", "queries", "highlights.scm"));
    }

    private static string Json()
    {
        Random random = new Random(1);
        StringBuilder builder = new StringBuilder();
        builder.Append("[\n");
        for (int i = 0; i < 1500; i++)
        {
            if (i > 0)
            {
                builder.Append(",\n");
            }

            builder.Append(CultureInfo.InvariantCulture,
                $"  {{\"id\": {i}, \"name\": \"item {random.Next(100000)}\", \"price\": {random.NextDouble() * 1000:F2}, " +
                $"\"tags\": [\"a{random.Next(50)}\", \"b{random.Next(50)}\"], \"active\": {(random.Next(2) == 0 ? "true" : "false")}, " +
                $"\"owner\": {{\"first\": \"n{random.Next(1000)}\", \"last\": null, \"age\": {random.Next(18, 90)}}}}}");
        }

        builder.Append("\n]\n");
        return builder.ToString();
    }

    private static string Python()
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("import os\nimport sys\nfrom typing import List, Optional\n\n");
        for (int i = 0; i < 300; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $$"""


                class Worker{{i}}(object):
                    '''A worker that processes items.'''

                    def __init__(self, name: str, limit: int = {{i}}) -> None:
                        self.name = name
                        self.limit = limit
                        self.items: List[int] = []

                    def add(self, value: int) -> Optional[int]:
                        if value < 0 or value > self.limit:
                            return None
                        self.items.append(value * 2 + 1)
                        return len(self.items)

                    def report(self) -> str:
                        total = sum(x for x in self.items if x % 2 == 1)
                        return f"{self.name}: {total} ({len(self.items)} items)"

                    @staticmethod
                    def parse(text):
                        try:
                            return [int(part) for part in text.split(",")]
                        except ValueError as error:
                            print("bad input", error, file=sys.stderr)
                            return []

                """);
        }

        return builder.ToString();
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
