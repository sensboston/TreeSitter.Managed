using System.Globalization;
using System.Text;
using TreeSitter.Tests.Subtrees;

namespace TreeSitter.Tests.Runtime;

/// <summary>
/// The managed side of oracle_lookahead (tests/Oracle/FORMATS.md, section
/// "oracle_lookahead"): must print exactly what the native shim prints for the same
/// commands. The commands work on one <see cref="LookaheadIterator"/>, as in the shim;
/// the line splitting, the argument parsing and the printed text mirror
/// <c>oracle_lookahead</c> of tests/Oracle/shim/oracle.c.
/// </summary>
internal static class LookaheadRunner
{
    internal static string Run(string grammar, string commands)
    {
        Language? language = ScriptLanguages.Get(grammar);
        if (language is null)
        {
            return "error: unknown language\n";
        }

        StringBuilder output = new StringBuilder();
        LookaheadIterator? iterator = null;

        string[] lines = commands.Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];

            // An empty line has no words, and a line without words only moves to the next
            // one (the "if (*command == '\0') { line = next; continue; }" of the shim): the
            // script goes on. The loop of the shim stops where the commands text ends, which
            // is the empty last element that Split('\n') produces for a trailing line feed.
            if (line.Length == 0)
            {
                continue;
            }

            // The line is "<command> [<arg1> [<arg2>]]", separated by spaces; the split of
            // the shim, which ignores a line without words and drops the words after arg2.
            string command = string.Empty;
            string arg1 = string.Empty;
            string arg2 = string.Empty;
            int position = 0;
            for (int word = 0; word < 3; word++)
            {
                while (position < line.Length && line[position] == ' ')
                {
                    position++;
                }

                if (position == line.Length)
                {
                    break;
                }

                int end = position;
                while (end < line.Length && line[end] != ' ')
                {
                    end++;
                }

                bool isLast = end == line.Length;
                string text = line.Substring(position, end - position);
                if (word == 0)
                {
                    command = text;
                }
                else if (word == 1)
                {
                    arg1 = text;
                }
                else
                {
                    arg2 = text;
                }

                if (isLast)
                {
                    break;
                }

                position = end + 1;
            }

            if (command.Length == 0)
            {
                continue;
            }

            if (command == "new")
            {
                iterator = language.CreateLookaheadIterator((ushort)Number(arg1));
                output.Append(CultureInfo.InvariantCulture, $"new = {(iterator is not null ? 1 : 0)}\n");
            }
            else if (command == "reset_state")
            {
                if (iterator is null)
                {
                    return NoIterator(output);
                }

                output.Append(CultureInfo.InvariantCulture, $"reset_state = {(iterator.ResetState((ushort)Number(arg1)) ? 1 : 0)}\n");
            }
            else if (command == "reset")
            {
                // reset <grammar> <state>: the state belongs to the given grammar.
                Language? other = ScriptLanguages.Get(arg1);
                if (other is null)
                {
                    output.Append("error: unknown language\n");
                    return output.ToString();
                }

                if (iterator is null)
                {
                    return NoIterator(output);
                }

                output.Append(CultureInfo.InvariantCulture, $"reset = {(iterator.Reset(other, (ushort)Number(arg2)) ? 1 : 0)}\n");
            }
            else if (command == "next")
            {
                if (iterator is null)
                {
                    return NoIterator(output);
                }

                output.Append(CultureInfo.InvariantCulture, $"next = {(iterator.Next() ? 1 : 0)}\n");
            }
            else if (command == "peek")
            {
                if (iterator is null)
                {
                    return NoIterator(output);
                }

                output.Append(
                    CultureInfo.InvariantCulture,
                    $"symbol = {iterator.CurrentSymbol} {iterator.CurrentSymbolName ?? "null"}\n");
            }
            else if (command == "all" || command == "count")
            {
                if (iterator is null)
                {
                    return NoIterator(output);
                }

                uint count = 0;
                while (iterator.Next())
                {
                    if (command == "all")
                    {
                        ushort symbol = iterator.CurrentSymbol;
                        output.Append(
                            CultureInfo.InvariantCulture,
                            $"symbol {symbol} {iterator.Language.SymbolName(symbol) ?? "null"}\n");
                    }

                    count++;
                }

                output.Append(CultureInfo.InvariantCulture, $"count = {count}\n");
            }
            else if (command == "language")
            {
                if (iterator is null)
                {
                    return NoIterator(output);
                }

                output.Append(CultureInfo.InvariantCulture, $"language = {iterator.Language.Name ?? "null"}\n");
            }
            else if (command == "delete")
            {
                iterator = null;
                output.Append("delete = 1\n");
            }
            else
            {
                output.Append(CultureInfo.InvariantCulture, $"error: unknown command '{command}'\n");
                return output.ToString();
            }
        }

        return output.ToString();
    }

    /// <summary>The line of a command that needs an iterator when there is none; it ends the script.</summary>
    private static string NoIterator(StringBuilder output)
    {
        output.Append("error = no iterator\n");
        return output.ToString();
    }

    /// <summary>
    /// strtoul with base 10 on the argument: the leading digits, 0 when there are none, and
    /// a negative value wraps like the C conversion to an unsigned long. The tests never use
    /// a state above uint.MaxValue, which C would saturate.
    /// </summary>
    private static uint Number(string text)
    {
        int index = 0;
        while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
        {
            index++;
        }

        bool negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            index++;
        }

        if (index == text.Length || text[index] < '0' || text[index] > '9')
        {
            return 0;
        }

        unchecked
        {
            uint value = 0;
            while (index < text.Length && text[index] >= '0' && text[index] <= '9')
            {
                value = (value * 10) + (uint)(text[index] - '0');
                index++;
            }

            return negative ? (uint.MaxValue - value) + 1u : value;
        }
    }
}
