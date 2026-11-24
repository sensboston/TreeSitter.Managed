using System.Globalization;
using System.Text;
using TreeSitter.Tests.Subtrees;

namespace TreeSitter.Tests.Parsing;

/// <summary>
/// The managed side of oracle_parse (tests/Oracle/FORMATS.md, section "oracle_parse"):
/// must print exactly what the native function prints.
/// </summary>
internal static class ParseRunner
{
    internal const uint LogFlag = 1;
    internal const uint ReparseFlag = 2;
    internal const uint CancelFlag = 4;
    internal const uint RangesFlag = 8;
    internal const uint ReparseRangesFlag = 16;

    private const int MaxRanges = 64;

    internal static string Run(string grammar, byte[] utf8, uint flags)
    {
        StringBuilder output = new StringBuilder();
        Language? language = ScriptLanguages.Get(grammar);
        if (language is null)
        {
            return "error: unknown language\n";
        }

        Parser parser = new Parser();
        parser.SetLanguage(language);
        if ((flags & LogFlag) != 0)
        {
            parser.Logger = (type, message) =>
                output.Append(type == LogType.Parse ? "parse " : "lex ").Append(message).Append('\n');
        }

        Range[] ranges = EvenLines(utf8);
        if ((flags & RangesFlag) != 0)
        {
            output.Append(CultureInfo.InvariantCulture, $"set_included_ranges = {(parser.SetIncludedRanges(ranges) ? 1 : 0)}\n");
        }

        Tree? tree = RunParse(output, parser, null, utf8, flags);
        PrintTree(output, tree);

        if ((flags & (ReparseFlag | ReparseRangesFlag)) != 0 && tree is not null)
        {
            output.Append("reparse\n");
            if ((flags & ReparseRangesFlag) != 0)
            {
                output.Append(CultureInfo.InvariantCulture, $"set_included_ranges = {(parser.SetIncludedRanges(ranges) ? 1 : 0)}\n");
            }

            Tree? newTree = RunParse(output, parser, tree, utf8, flags);
            PrintTree(output, newTree);
            newTree?.Dispose();
        }

        tree?.Dispose();
        return output.ToString();
    }

    // oracle_parse_run
    private static Tree? RunParse(StringBuilder output, Parser parser, Tree? oldTree, byte[] utf8, uint flags)
    {
        if ((flags & CancelFlag) == 0)
        {
            return parser.ParseString(utf8, oldTree);
        }

        int calls = 0;
        ParseOptions options = new ParseOptions
        {
            ProgressCallback = _ =>
            {
                calls++;
                return calls == 1 || calls == 3;
            },
        };
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Tree? tree = parser.Parse(ParseInputs.FromBytes(utf8, InputEncoding.Utf8), oldTree, options);
            if (tree is not null)
            {
                return tree;
            }

            output.Append("canceled\n");
        }

        return null;
    }

    // oracle_parse_even_lines
    private static Range[] EvenLines(byte[] input)
    {
        List<Range> ranges = new List<Range>();
        uint start = 0;
        uint row = 0;
        uint length = (uint)input.Length;
        while (start < length && ranges.Count < MaxRanges)
        {
            uint end = start;
            while (end < length && input[end] != (byte)'\n')
            {
                end++;
            }

            bool hasBreak = end < length;
            if (hasBreak)
            {
                end++;
            }

            if (row % 2 == 0)
            {
                ranges.Add(new Range(
                    new Point(row, 0),
                    hasBreak ? new Point(row + 1, 0) : new Point(row, end - start),
                    start,
                    end));
            }

            start = end;
            row++;
        }

        return ranges.ToArray();
    }

    internal static void PrintTree(StringBuilder output, Tree? tree)
    {
        if (tree is null)
        {
            output.Append("tree = null\n");
            return;
        }

        output.Append("string = ").Append(tree.RootNode.ToString()).Append('\n');
        output.Append(SubtreeScriptRunner.Dump(tree.Root));
    }
}
