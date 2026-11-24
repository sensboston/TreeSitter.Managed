using System.Globalization;
using System.Text;
using TreeSitter.Tests.Subtrees;

namespace TreeSitter.Tests.Queries;

/// <summary>
/// The managed side of oracle_query (tests/Oracle/FORMATS.md, section "oracle_query"):
/// must print exactly what the native function prints.
/// </summary>
internal static class QueryRunner
{
    internal static string Run(string grammar, byte[] source)
    {
        StringBuilder output = new StringBuilder();
        Language? language = ScriptLanguages.Get(grammar);
        if (language is null)
        {
            return "error: unknown language\n";
        }

        Query? query = Query.TryCreate(language, source, out uint errorOffset, out QueryError error);
        if (query is null)
        {
            output.Append(CultureInfo.InvariantCulture, $"error = {(int)error} offset = {errorOffset}\n");
        }
        else
        {
            Print(output, query, (uint)source.Length);
            output.Append("copy\n");
            Print(output, query.Copy(), (uint)source.Length);
        }

        return output.ToString();
    }

    // oracle_query_put_text
    private static void PutText(StringBuilder output, string text)
    {
        foreach (char c in text)
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
    }

    // oracle_query_print
    private static void Print(StringBuilder output, Query query, uint sourceLength)
    {
        uint patternCount = query.PatternCount;
        uint captureCount = query.CaptureCount;
        uint stringCount = query.StringCount;
        output.Append(CultureInfo.InvariantCulture, $"patterns = {patternCount} captures = {captureCount} strings = {stringCount}\n");

        for (uint i = 0; i < patternCount; i++)
        {
            output.Append(
                CultureInfo.InvariantCulture,
                $"pattern {i} start = {query.StartByteForPattern(i)} end = {query.EndByteForPattern(i)} " +
                $"rooted = {(query.IsPatternRooted(i) ? 1 : 0)} non_local = {(query.IsPatternNonLocal(i) ? 1 : 0)}\n");
            foreach (QueryPredicateStep step in query.PredicatesForPattern(i))
            {
                switch (step.Type)
                {
                    case QueryPredicateStepType.Done:
                        output.Append("  done\n");
                        break;
                    case QueryPredicateStepType.Capture:
                        output.Append(CultureInfo.InvariantCulture, $"  capture {step.ValueId} ");
                        PutText(output, query.CaptureNameForId(step.ValueId));
                        output.Append('\n');
                        break;
                    case QueryPredicateStepType.StringValue:
                        output.Append(CultureInfo.InvariantCulture, $"  string {step.ValueId} ");
                        PutText(output, query.StringValueForId(step.ValueId));
                        output.Append('\n');
                        break;
                }
            }
        }

        for (uint i = 0; i < captureCount; i++)
        {
            output.Append(CultureInfo.InvariantCulture, $"capture {i} ");
            PutText(output, query.CaptureNameForId(i));
            output.Append(" quantifiers =");
            for (uint j = 0; j < patternCount; j++)
            {
                output.Append(CultureInfo.InvariantCulture, $" {(int)query.CaptureQuantifierForId(j, i)}");
            }

            output.Append('\n');
        }

        for (uint i = 0; i < stringCount; i++)
        {
            output.Append(CultureInfo.InvariantCulture, $"string {i} ");
            PutText(output, query.StringValueForId(i));
            output.Append('\n');
        }

        output.Append("guaranteed =");
        for (uint offset = 0; offset <= sourceLength; offset++)
        {
            if (query.IsPatternGuaranteedAtStep(offset))
            {
                output.Append(CultureInfo.InvariantCulture, $" {offset}");
            }
        }

        output.Append('\n');
    }
}
