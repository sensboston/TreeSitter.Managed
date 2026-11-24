// ts2cs - converts a generated tree-sitter grammar (parser.c) into C# source.

using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Dump;
using TreeSitter.Generator.Emit;
using TreeSitter.Generator.Model;

namespace TreeSitter.Generator;

internal static class Program
{
    private const string Usage =
        "usage:\n" +
        "  ts2cs generate <parser.c> --name <Name> --namespace <Namespace> --out <dir> [--tables literal|blob] [--source <text>]\n" +
        "  ts2cs dump-tables <parser.c>\n";

    internal static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Write(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            switch (args[0])
            {
                case "generate":
                    return Generate(args);
                case "dump-tables":
                    if (args.Length != 2)
                    {
                        Console.Error.Write(Usage);
                        return 1;
                    }
                    Console.Out.Write(TableDumper.Dump(GrammarModelBuilder.Build(args[1]).Tables));
                    return 0;
                default:
                    Console.Error.WriteLine($"ts2cs: unknown command '{args[0]}'");
                    return 1;
            }
        }
        catch (CSourceException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (NotSupportedException e)
        {
            Console.Error.WriteLine($"ts2cs: {e.Message}");
            return 1;
        }
    }

    private static int Generate(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.Write(Usage);
            return 1;
        }

        string parserC = args[1];
        string? name = null, ns = null, outDir = null, source = null, tables = "literal";
        for (int i = 2; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : throw new NotSupportedException($"missing value for {args[i]}");
            switch (args[i])
            {
                case "--name": name = value; break;
                case "--namespace": ns = value; break;
                case "--out": outDir = value; break;
                case "--source": source = value; break;
                case "--tables": tables = value; break;
                default: throw new NotSupportedException($"unknown option {args[i]}");
            }
            i++;
        }
        if (name is null || ns is null || outDir is null)
        {
            Console.Error.Write(Usage);
            return 1;
        }

        ITableEmitter emitter = tables switch
        {
            "literal" => new LiteralEmitter(),
            "blob" => new BlobEmitter(),
            _ => throw new NotSupportedException($"unknown --tables value '{tables}'"),
        };
        string code = emitter.Emit(GrammarModelBuilder.Build(parserC), new EmitOptions(name, ns, source));
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, name + "Language.g.cs"), code, new System.Text.UTF8Encoding(false));
        return 0;
    }
}
