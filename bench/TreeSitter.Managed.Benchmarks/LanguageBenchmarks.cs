using BenchmarkDotNet.Attributes;
using TreeSitter.Abi;
using TreeSitter.Grammars.C;
using TreeSitter.Grammars.Json;
using TreeSitter.Grammars.Python;

namespace TreeSitter.Benchmarks;

/// <summary>
/// The cost of creating a language from the generated tables (decision D3): CreateTables
/// copies the RVA literals into arrays and decodes the packed struct tables.
/// </summary>
[MemoryDiagnoser]
public class LanguageBenchmarks
{
    [Params("json", "c", "python")]
    public string Grammar { get; set; } = "json";

    [Benchmark]
    public Language CreateLanguage()
    {
        LanguageTables tables = Grammar switch
        {
            "json" => JsonGrammar.CreateTables(),
            "c" => CGrammar.CreateTables(),
            _ => PythonGrammar.CreateTables(),
        };
        return Language.FromTables(tables);
    }
}
