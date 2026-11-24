using System.Globalization;
using System.Text;
using TreeSitter.Tests.Subtrees;

namespace TreeSitter.Tests.Parsing;

/// <summary>
/// The managed side of oracle_edit (tests/Oracle/FORMATS.md, section "oracle_edit"):
/// must print exactly what the native function prints.
/// </summary>
internal static class EditRunner
{
    internal static string Run(string grammar, byte[] input, string edits, uint flags)
    {
        StringBuilder output = new StringBuilder();
        Language? language = ScriptLanguages.Get(grammar);
        if (language is null)
        {
            return "error: unknown language\n";
        }

        Parser parser = new Parser();
        parser.SetLanguage(language);

        byte[] text = (byte[])input.Clone();
        Tree? tree = parser.ParseString(text);
        ParseRunner.PrintTree(output, tree);
        if ((flags & ParseRunner.LogFlag) != 0)
        {
            parser.Logger = (type, message) =>
                output.Append(type == LogType.Parse ? "parse " : "lex ").Append(message).Append('\n');
        }

        foreach (string line in edits.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tree is null)
            {
                break;
            }

            string[] parts = line.Split(' ');
            uint start = uint.Parse(parts[0], CultureInfo.InvariantCulture);
            uint deleted = uint.Parse(parts[1], CultureInfo.InvariantCulture);
            byte[] inserted = parts.Length > 2 ? Convert.FromHexString(parts[2]) : [];

            uint textLength = (uint)text.Length;
            if (start > textLength)
            {
                start = textLength;
            }

            if (deleted > textLength - start)
            {
                deleted = textLength - start;
            }

            Point startPoint = PointAt(text, start);
            Point oldEndPoint = PointAt(text, start + deleted);
            byte[] newText = new byte[textLength - deleted + inserted.Length];
            Array.Copy(text, 0, newText, 0, start);
            Array.Copy(inserted, 0, newText, start, inserted.Length);
            Array.Copy(text, start + deleted, newText, start + inserted.Length, textLength - start - deleted);
            text = newText;
            InputEdit edit = new InputEdit(
                start,
                start + deleted,
                start + (uint)inserted.Length,
                startPoint,
                oldEndPoint,
                PointAt(text, start + (uint)inserted.Length));

            output.Append(
                CultureInfo.InvariantCulture,
                $"edit {edit.StartByte} {edit.OldEndByte} {edit.NewEndByte} {edit.StartPoint.Row}:{edit.StartPoint.Column} " +
                $"{edit.OldEndPoint.Row}:{edit.OldEndPoint.Column} {edit.NewEndPoint.Row}:{edit.NewEndPoint.Column}\n");

            // ts_node_edit of the children of the root, before the tree is edited.
            Node root = tree.RootNode;
            for (uint i = 0, n = root.ChildCount; i < n; i++)
            {
                Node child = root.Child(i).Edit(edit);
                output.Append(CultureInfo.InvariantCulture, $"node_edit {child.StartByte} {child.StartPoint.Row}:{child.StartPoint.Column}\n");
            }

            tree.Edit(edit);
            Tree? newTree = parser.ParseString(text, tree);
            if (newTree is null)
            {
                output.Append("tree = null\n");
                break;
            }

            Range[] ranges = Tree.GetChangedRanges(tree, newTree);
            output.Append(CultureInfo.InvariantCulture, $"changed_ranges = {ranges.Length}\n");
            foreach (Range range in ranges)
            {
                output.Append(
                    CultureInfo.InvariantCulture,
                    $"{range.StartByte}:{range.StartPoint.Row}:{range.StartPoint.Column}-{range.EndByte}:{range.EndPoint.Row}:{range.EndPoint.Column}\n");
            }

            ParseRunner.PrintTree(output, newTree);

            Parser freshParser = new Parser();
            freshParser.SetLanguage(language);
            Tree fresh = freshParser.ParseString(text)!;
            bool same = string.Equals(newTree.RootNode.ToString(), fresh.RootNode.ToString(), StringComparison.Ordinal);
            output.Append(CultureInfo.InvariantCulture, $"same_as_fresh = {(same ? 1 : 0)}\n");
            fresh.Dispose();

            tree.Dispose();
            tree = newTree;
        }

        tree?.Dispose();
        return output.ToString();
    }

    // oracle_edit_point_at
    private static Point PointAt(byte[] text, uint offset)
    {
        uint row = 0;
        uint column = 0;
        for (uint i = 0; i < offset; i++)
        {
            if (text[i] == (byte)'\n')
            {
                row++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return new Point(row, column);
    }
}
