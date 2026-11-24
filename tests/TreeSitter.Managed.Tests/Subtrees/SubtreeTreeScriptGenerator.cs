using System.Globalization;
using System.Text;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// Generates valid random subtree scripts that use the tree commands
/// (tests/Oracle/FORMATS.md, section "Tree commands") for differential testing.
/// Deterministic for a seed (new Random(seed)).
/// </summary>
/// <remarks>
/// The generator builds one tree, depth first: leaves (inline and heap, zero length,
/// multi-line, extra, MISSING, ERROR), nodes with the real production ids of the
/// grammar (mostly the ones with aliases or fields, so the alias sequences and field
/// maps are used), and error nodes. Every subtree ends in the root, so `tree` gets
/// the whole structure. Then registers r0..r3 hold nodes: each starts at `root` and
/// moves with the navigation commands, and the properties are printed on the way.
/// The script ends with `tdump` and the cursor commands, with goals inside and
/// just outside the tree.
/// </remarks>
internal static class SubtreeTreeScriptGenerator
{
    private const int NodeRegisterCount = 4;
    private const int FirstTreeRegister = 8;
    private const int MaxDepth = 4;

    /// <summary>The number of scripts to generate per grammar.</summary>
    internal const int CountPerGrammar = 3000;

    private static readonly string[] PropertyCommands =
    [
        "start_byte", "start_point", "end_byte", "end_point", "type", "symbol", "grammar_type",
        "grammar_symbol", "child_count", "named_child_count", "descendant_count", "is_null",
        "is_named", "is_missing", "is_extra", "has_error", "is_error", "parse_state",
        "next_parse_state",
    ];

    private static readonly string[] MoveCommands =
    [
        "parent", "next_sibling", "prev_sibling", "next_named_sibling", "prev_named_sibling",
    ];

    /// <param name="seed">Random seed.</param>
    /// <param name="language">"json", "c" or "python".</param>
    /// <param name="commandCount">Number of node commands after the tree is created.</param>
    internal static string Generate(int seed, string language, int commandCount)
    {
        Language grammar = ScriptLanguages.Get(language)
            ?? throw new ArgumentOutOfRangeException(nameof(language), language, "unknown language");
        ScriptBuilder builder = new ScriptBuilder(new Random(seed), grammar);
        return builder.Build(language, commandCount);
    }

    private sealed class ScriptBuilder
    {
        private readonly Random random;
        private readonly Language language;
        private readonly StringBuilder output = new StringBuilder();
        private readonly List<int> interestingProductions = new List<int>();
        private int nextRegister = FirstTreeRegister;
        private int nodeCount;
        private int totalBytes;
        private int totalRows;

        internal ScriptBuilder(Random random, Language language)
        {
            this.random = random;
            this.language = language;
            for (int production = 0; production < language.Tables.ProductionIdCount; production++)
            {
                bool hasAlias = false;
                if (language.TryGetAliasSequence((uint)production, out ReadOnlySpan<ushort> aliases))
                {
                    foreach (ushort alias in aliases)
                    {
                        hasAlias |= alias != 0;
                    }
                }

                if (hasAlias || !language.FieldMap((uint)production).IsEmpty)
                {
                    interestingProductions.Add(production);
                }
            }
        }

        internal string Build(string languageName, int commandCount)
        {
            Line("lang " + languageName);
            int root = BuildSubtree(0, out _, out _);
            if (random.Next(2) == 0)
            {
                StringBuilder ranges = new StringBuilder("tranges");
                int start = 0;
                int row = 0;
                for (int i = random.Next(4); i > 0; i--)
                {
                    int startByte = start + random.Next(4);
                    int startRow = row + random.Next(2);
                    int endByte = startByte + random.Next(6);
                    int endRow = startRow + random.Next(2);
                    ranges.Append(' ').Append(Len(startByte, startRow, random.Next(5))).Append('-')
                        .Append(Len(endByte, endRow, random.Next(5)));
                    start = endByte;
                    row = endRow;
                }

                Line(ranges.ToString());
            }

            Line("tree " + Reg(root));
            Line("tincluded");

            for (int i = 0; i < NodeRegisterCount; i++)
            {
                if (random.Next(5) == 0)
                {
                    Line("toffset " + Reg(i) + " " + Len(random.Next(20), random.Next(3), random.Next(10)));
                }
                else
                {
                    Line("root " + Reg(i));
                }
            }

            for (int i = 0; i < commandCount; i++)
            {
                NodeCommand(random.Next(NodeRegisterCount));
            }

            Line("tdump");
            Line("cwalk");
            Line("cinternal");
            int descendants = nodeCount + 2;
            Line("cdesc " + Num(random.Next(descendants)));
            Line("cdesc " + Num(random.Next(descendants)));
            Line("ccopy " + Num(random.Next(descendants)));
            Line("cbyte " + Num(random.Next(totalBytes + 2)));
            Line("cpoint 0:" + Num(random.Next(totalRows + 2)) + ":" + Num(random.Next(8)));
            Line("tcopy");
            if (random.Next(2) == 0)
            {
                Line("tdelete");
            }

            return output.ToString();
        }

        // ------------------------------------------------------------ tree construction

        // Builds a subtree and returns its register; bytes and rows are its total size.
        private int BuildSubtree(int depth, out int bytes, out int rows)
        {
            if (depth >= MaxDepth || nextRegister > 48 || (depth > 0 && random.Next(3) == 0))
            {
                return BuildLeaf(out bytes, out rows);
            }

            bool errorNode = random.Next(12) == 0;
            int production = 0;
            int structuralLimit = Math.Max(1, (int)language.Tables.MaxAliasSequenceLength);
            if (!errorNode && interestingProductions.Count > 0 && random.Next(4) != 0)
            {
                production = interestingProductions[random.Next(interestingProductions.Count)];
            }
            else if (!errorNode)
            {
                production = random.Next((int)language.Tables.ProductionIdCount);
            }

            // At most MaxAliasSequenceLength structural children: the alias sequence of a
            // production has that many entries. Extra children do not count.
            int childCount = random.Next(1, Math.Min(structuralLimit + 1, 4) + 1);
            List<int> children = new List<int>();
            int structural = 0;
            bytes = 0;
            rows = 0;
            for (int i = 0; i < childCount; i++)
            {
                int child = BuildSubtree(depth + 1, out int childBytes, out int childRows);
                bool extra = structural >= structuralLimit || random.Next(10) == 0;
                if (extra)
                {
                    Line("setextra " + Reg(child) + " 1");
                }
                else
                {
                    structural++;
                }

                children.Add(child);
                bytes += childBytes;
                rows += childRows;
            }

            int destination = NewRegister();
            StringBuilder text = new StringBuilder();
            if (errorNode)
            {
                text.Append("errnode ").Append(Reg(destination)).Append(' ').Append(Num(random.Next(5) == 0 ? 1 : 0));
            }
            else
            {
                text.Append("node ").Append(Reg(destination)).Append(' ').Append(Num(NonTerminal()))
                    .Append(' ').Append(Num(production));
            }

            foreach (int child in children)
            {
                text.Append(' ').Append(Reg(child));
            }

            Line(text.ToString());
            nodeCount++;
            if (depth == 0)
            {
                totalBytes = bytes;
                totalRows = rows;
            }

            return destination;
        }

        private int BuildLeaf(out int bytes, out int rows)
        {
            int destination = NewRegister();
            nodeCount++;
            int kind = random.Next(20);
            string padding = Padding(out int paddingBytes, out int paddingRows);
            string size = Size(out int sizeBytes, out int sizeRows);
            bytes = paddingBytes + sizeBytes;
            rows = paddingRows + sizeRows;
            if (kind == 0)
            {
                Line("missing " + Reg(destination) + " " + Num(Terminal()) + " " + Num(State()) + " " +
                    padding + " " + Num(random.Next(4)));
                bytes = paddingBytes;
                rows = paddingRows;
            }
            else if (kind == 1)
            {
                Line("error " + Reg(destination) + " " + Num(random.Next(32, 127)) + " " + padding + " " +
                    size + " " + Num(sizeBytes + random.Next(3)) + " " + Num(State()));
            }
            else
            {
                int symbol = random.Next(3) == 0 ? AnySymbol() : Terminal();
                int lookahead = random.Next(2) == 0 ? random.Next(1, 4) : 16 + random.Next(5);
                Line("leaf " + Reg(destination) + " " + Num(symbol) + " " + padding + " " + size + " " +
                    Num(lookahead) + " " + Num(State()) + " 0 " + Num(random.Next(8) == 0 ? 1 : 0) + " 0");
            }

            return destination;
        }

        private int NewRegister()
        {
            if (nextRegister >= 64)
            {
                throw new InvalidOperationException("The tree needs more registers.");
            }

            return nextRegister++;
        }

        // ------------------------------------------------------------ node commands

        private void NodeCommand(int register)
        {
            string reg = Reg(register);
            int pick = random.Next(100);
            if (pick < 8)
            {
                Line("root " + reg);
            }
            else if (pick < 22)
            {
                Line("child " + reg + " " + Num(random.Next(4)));
            }
            else if (pick < 30)
            {
                Line("named_child " + reg + " " + Num(random.Next(3)));
            }
            else if (pick < 45)
            {
                Line(MoveCommands[random.Next(MoveCommands.Length)] + " " + reg);
            }
            else if (pick < 50)
            {
                Line((random.Next(2) == 0 ? "first_child_for_byte " : "first_named_child_for_byte ") + reg +
                    " " + Num(random.Next(totalBytes + 2)));
            }
            else if (pick < 55)
            {
                int start = random.Next(totalBytes + 2);
                int end = random.Next(8) == 0 ? random.Next(start + 1) : start + random.Next(4);
                Line((random.Next(2) == 0 ? "descendant_for_byte_range " : "named_descendant_for_byte_range ") +
                    reg + " " + Num(start) + " " + Num(end));
            }
            else if (pick < 60)
            {
                Line((random.Next(2) == 0 ? "descendant_for_point_range " : "named_descendant_for_point_range ") +
                    reg + " " + PointArgument() + " " + PointArgument());
            }
            else if (pick < 66)
            {
                Line((random.Next(2) == 0 ? "field_name_for_child " : "field_name_for_named_child ") + reg +
                    " " + Num(random.Next(4)));
            }
            else if (pick < 72)
            {
                Line("child_by_field_id " + reg + " " + Num(random.Next((int)language.FieldCount + 2)));
            }
            else if (pick < 76)
            {
                string? name = language.FieldCount > 0
                    ? language.FieldNameForId((ushort)random.Next(1, (int)language.FieldCount + 1))
                    : null;
                Line("child_by_field_name " + reg + " " + (name ?? "no_such_field"));
            }
            else
            {
                Line(PropertyCommands[random.Next(PropertyCommands.Length)] + " " + reg);
            }
        }

        // ------------------------------------------------------------ random values

        private int AnySymbol() => random.Next(1, (int)language.SymbolCount);

        private int Terminal() => random.Next(1, (int)language.Tables.TokenCount);

        private int NonTerminal() =>
            random.Next((int)language.Tables.TokenCount, (int)language.SymbolCount);

        private int State() => random.Next((int)language.Tables.StateCount);

        // A padding: often empty, sometimes over a line break.
        private string Padding(out int bytes, out int rows)
        {
            int pick = random.Next(4);
            if (pick == 0)
            {
                bytes = 0;
                rows = 0;
                return "0:0:0";
            }

            if (pick == 1)
            {
                int column = random.Next(4);
                bytes = 1 + column;
                rows = 1;
                return Len(bytes, 1, column);
            }

            bytes = random.Next(1, 4);
            rows = 0;
            return Len(bytes, 0, bytes);
        }

        // A size: sometimes zero, sometimes over a line break.
        private string Size(out int bytes, out int rows)
        {
            int pick = random.Next(6);
            if (pick == 0)
            {
                bytes = 0;
                rows = 0;
                return "0:0:0";
            }

            if (pick == 1)
            {
                int column = random.Next(3);
                bytes = 2 + column;
                rows = 1;
                return Len(bytes, 1, column);
            }

            bytes = random.Next(1, 5);
            rows = 0;
            return Len(bytes, 0, bytes);
        }

        private string PointArgument() => "0:" + Num(random.Next(totalRows + 2)) + ":" + Num(random.Next(6));

        // ------------------------------------------------------------ text helpers

        private static string Reg(int index) => "r" + index.ToString(CultureInfo.InvariantCulture);

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Len(int bytes, int row, int column) =>
            Num(bytes) + ":" + Num(row) + ":" + Num(column);

        private void Line(string text) => output.Append(text).Append('\n');
    }
}
