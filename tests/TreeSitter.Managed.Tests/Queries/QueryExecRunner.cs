using System.Globalization;
using System.Text;
using TreeSitter.Tests.Subtrees;

namespace TreeSitter.Tests.Queries;

/// <summary>
/// The managed side of oracle_query_exec (tests/Oracle/FORMATS.md, section
/// "oracle_query_exec"): must print exactly what the native function prints.
/// </summary>
internal static class QueryExecRunner
{
    internal static string Run(string grammar, byte[] source, byte[] querySource, string commands)
    {
        StringBuilder output = new StringBuilder();
        Language? language = ScriptLanguages.Get(grammar);
        if (language is null)
        {
            return "error: unknown language\n";
        }

        Query? query = Query.TryCreate(language, querySource, out uint errorOffset, out QueryError error);
        if (query is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"error = {(int)error} offset = {errorOffset}\n");
        }

        Parser parser = new Parser();
        parser.SetLanguage(language);
        Tree tree = parser.ParseString(source)!;
        Node root = tree.RootNode;
        QueryCursor cursor = new QueryCursor();

        foreach (string line in commands.Split('\n'))
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            string command = words[0];
            string arg1 = words.Length > 1 ? words[1] : string.Empty;
            string arg2 = words.Length > 2 ? words[2] : string.Empty;
            switch (command)
            {
                case "match_limit":
                    cursor.MatchLimit = Number(arg1);
                    output.Append(CultureInfo.InvariantCulture, $"match_limit = {cursor.MatchLimit}\n");
                    break;
                case "max_start_depth":
                    cursor.SetMaxStartDepth(Number(arg1));
                    break;
                case "byte_range":
                    output.Append(CultureInfo.InvariantCulture, $"byte_range = {(cursor.SetByteRange(Number(arg1), Number(arg2)) ? 1 : 0)}\n");
                    break;
                case "containing_byte_range":
                    output.Append(CultureInfo.InvariantCulture, $"containing_byte_range = {(cursor.SetContainingByteRange(Number(arg1), Number(arg2)) ? 1 : 0)}\n");
                    break;
                case "point_range":
                    output.Append(CultureInfo.InvariantCulture, $"point_range = {(cursor.SetPointRange(ParsePoint(arg1), ParsePoint(arg2)) ? 1 : 0)}\n");
                    break;
                case "containing_point_range":
                    output.Append(CultureInfo.InvariantCulture, $"containing_point_range = {(cursor.SetContainingPointRange(ParsePoint(arg1), ParsePoint(arg2)) ? 1 : 0)}\n");
                    break;
                case "disable_capture":
                    query.DisableCapture(arg1);
                    break;
                case "disable_pattern":
                    // C does not check the index (array_get asserts); the script skips a bad one.
                    if (Number(arg1) < query.PatternCount)
                    {
                        query.DisablePattern(Number(arg1));
                    }

                    break;
                case "matches":
                case "cancel":
                    RunMatches(output, query, cursor, root, command, arg1, arg2);
                    break;
                case "captures":
                    RunCaptures(output, query, cursor, root, arg1, arg2);
                    break;
                default:
                    output.Append(CultureInfo.InvariantCulture, $"error: unknown command '{command}'\n");
                    return output.ToString();
            }
        }

        return output.ToString();
    }

    private static void RunMatches(StringBuilder output, Query query, QueryCursor cursor, Node root, string command, string arg1, string arg2)
    {
        uint removeEvery = 0;
        if (command == "cancel")
        {
            uint calls = Number(arg1);
            QueryCursorOptions options = new QueryCursorOptions
            {
                ProgressCallback = _ =>
                {
                    calls--;
                    return calls == 0;
                },
            };
            cursor.Exec(query, root, options);
        }
        else
        {
            if (arg1 == "remove_every")
            {
                removeEvery = Number(arg2);
            }

            cursor.Exec(query, root);
        }

        uint index = 0;
        while (cursor.NextMatch(out QueryMatch match))
        {
            output.Append(CultureInfo.InvariantCulture, $"match {match.Id} pattern = {match.PatternIndex} captures = {match.Captures.Length}\n");
            foreach (QueryCapture capture in match.Captures)
            {
                output.Append("  ");
                PutCapture(output, query, capture);
            }

            index++;
            if (removeEvery > 0 && index % removeEvery == 0)
            {
                cursor.RemoveMatch(match.Id);
            }
        }

        output.Append(CultureInfo.InvariantCulture, $"exceeded = {(cursor.DidExceedMatchLimit ? 1 : 0)}\n");
    }

    private static void RunCaptures(StringBuilder output, Query query, QueryCursor cursor, Node root, string arg1, string arg2)
    {
        uint removeEvery = arg1 == "remove_every" ? Number(arg2) : 0;
        cursor.Exec(query, root);
        uint index = 0;
        while (cursor.NextCapture(out QueryMatch match, out uint captureIndex))
        {
            output.Append(CultureInfo.InvariantCulture, $"capture {match.Id} pattern = {match.PatternIndex} index = {captureIndex} ");
            PutCapture(output, query, match.Captures[(int)captureIndex]);
            index++;
            if (removeEvery > 0 && index % removeEvery == 0)
            {
                cursor.RemoveMatch(match.Id);
            }
        }

        output.Append(CultureInfo.InvariantCulture, $"exceeded = {(cursor.DidExceedMatchLimit ? 1 : 0)}\n");
    }

    // oracle_exec_put_capture
    private static void PutCapture(StringBuilder output, Query query, QueryCapture capture)
    {
        output.Append('@');
        foreach (char c in query.CaptureNameForId(capture.Index))
        {
            switch (c)
            {
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default: output.Append(c); break;
            }
        }

        Node node = capture.Node;
        output.Append(
            CultureInfo.InvariantCulture,
            $" {node.Type} {node.StartByte}-{node.EndByte} {node.StartPoint.Row}:{node.StartPoint.Column}\n");
    }

    private static uint Number(string text) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ? value : 0;

    // oracle_exec_parse_point; a point that does not parse stays 0:0, as in the shim.
    private static Point ParsePoint(string text)
    {
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 ||
            !uint.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out uint row) ||
            !uint.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint column))
        {
            return new Point(0, 0);
        }

        return new Point(row, column);
    }
}
