// The predicate layer of the tree-sitter Rust binding v0.27.2 (lib/binding_rust/lib.rs)
// on top of the port of query.c: Query::from_raw_parts builds for every pattern the text
// predicates, the property settings, the property predicates and the general predicates, and
// QueryMatch::satisfies_text_predicates checks the text predicates of a match (the checking
// part is in QueryCursorPredicates.cs).
//
// Rules:
// - The C runtime only parses the predicate steps (ts_query_predicates_for_pattern) and knows
//   none of these types; everything here is the binding's own work, so the text of the query
//   is kept for the row of an error exactly as from_raw_parts takes `source`.
// - The names are the Rust names in .NET casing: QueryPredicate, QueryPredicateArg,
//   QueryProperty, TextPredicateCapture, and the accessors general_predicates /
//   property_settings / property_predicates.
// - The error kind of a predicate error is QueryErrorKind::Predicate in Rust. TSQueryError
//   (api.h) has no such code and QueryError mirrors it one to one, so a predicate error is
//   reported as QueryError.Syntax carrying the message and the row of Rust's predicate_error
//   (its column and offset are 0, and so are ours).
// - The message strings are the Rust strings verbatim, including "#eq?" in the message of
//   #not-eq? and #any-not-eq?, and the "@" before a literal in the "Unexpected third
//   argument" message of parse_property.
// - TryCreate (the mirror of ts_query_new) keeps the compiled query when a predicate is
//   invalid, like Query::new_raw, which does no predicate analysis at all; the Query
//   constructors throw, like Query::new. A query from TryCreate reports the same error when
//   its predicates are read.

using System.Text;
using System.Text.RegularExpressions;

namespace TreeSitter;

/// <summary>
/// The kind of an argument of a general predicate (Rust: QueryPredicateArg). Rust's two variants
/// are the tuples Capture(u32) and String(&amp;str); the second one is called Literal here, because
/// an identifier String is the CA1720 rule (an identifier may not be a type name).
/// </summary>
public enum QueryPredicateArgKind
{
    /// <summary>QueryPredicateArg::Capture: the argument is a capture name.</summary>
    Capture,

    /// <summary>QueryPredicateArg::String: the argument is a literal string.</summary>
    Literal,
}

/// <summary>An argument of a general predicate (Rust: QueryPredicateArg).</summary>
/// <param name="Kind">Capture or Literal.</param>
/// <param name="CaptureId">The capture index, when <paramref name="Kind"/> is Capture.</param>
/// <param name="Value">The literal value, when <paramref name="Kind"/> is Literal.</param>
public readonly record struct QueryPredicateArg(QueryPredicateArgKind Kind, uint CaptureId, string? Value)
{
    /// <summary>QueryPredicateArg::Capture.</summary>
    /// <param name="captureId">The index of the capture in the query.</param>
    /// <returns>The argument.</returns>
    public static QueryPredicateArg FromCapture(uint captureId) => new(QueryPredicateArgKind.Capture, captureId, null);

    /// <summary>QueryPredicateArg::String, the literal form of the Rust variant.</summary>
    /// <param name="value">The literal value.</param>
    /// <returns>The argument.</returns>
    public static QueryPredicateArg FromString(string value) => new(QueryPredicateArgKind.Literal, 0, value);
}

/// <summary>A predicate the runtime does not know (Rust: QueryPredicate).</summary>
/// <param name="Operator">The name of the predicate function, without the leading # or .</param>
/// <param name="Args">Its arguments: capture names and literals, in source order.</param>
public sealed record QueryPredicate(string Operator, IReadOnlyList<QueryPredicateArg> Args)
{
    /// <summary>Two predicates are the same when their operator and their arguments are.</summary>
    /// <param name="other">The predicate to compare with.</param>
    /// <returns>True when both are the same.</returns>
    public bool Equals(QueryPredicate? other)
    {
        if (other is null ||
            !string.Equals(Operator, other.Operator, StringComparison.Ordinal) ||
            Args.Count != other.Args.Count)
        {
            return false;
        }

        for (int i = 0; i < Args.Count; i++)
        {
            if (Args[i] != other.Args[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The hash code of the operator and the arguments.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        HashCode code = new HashCode();
        code.Add(Operator, StringComparer.Ordinal);
        for (int i = 0; i < Args.Count; i++)
        {
            code.Add(Args[i]);
        }

        return code.ToHashCode();
    }
}

/// <summary>A key-value pair of a pattern (#set!, #is?, #is-not?; Rust: QueryProperty).</summary>
/// <param name="Key">The property name.</param>
/// <param name="Value">The property value, or null when the predicate gives none.</param>
/// <param name="CaptureId">The capture the property applies to, or null for the whole pattern.</param>
public sealed record QueryProperty(string Key, string? Value, uint? CaptureId);

/// <summary>
/// One entry of Query::property_predicates: a property and whether the pattern asks for it to
/// be set (true is #is?, false is #is-not?; Rust: the (QueryProperty, bool) pair).
/// </summary>
/// <param name="Property">The property the predicate names.</param>
/// <param name="IsPositive">True for #is?, false for #is-not?.</param>
public readonly record struct QueryPropertyPredicate(QueryProperty Property, bool IsPositive);

/// <summary>
/// The text of one node, as the TextProvider trait of the Rust binding provides it. The simple
/// case is a slice of the source, which is what the ReadOnlyMemory&lt;byte&gt; overloads build.
/// A Rust provider returns an iterator of chunks, so Rust joins the chunks of a node into a
/// buffer; a .NET source is one buffer, so a provider returns the whole text of the node and
/// the two join buffers of Rust (buffer1 and buffer2) have nothing to do.
/// </summary>
/// <param name="node">The node whose text is asked for.</param>
/// <returns>The bytes of the node in the source.</returns>
public delegate ReadOnlyMemory<byte> QueryTextProvider(Node node);

/// <summary>The four shapes of TextPredicateCapture of the Rust binding.</summary>
internal enum TextPredicateKind
{
    /// <summary>TextPredicateCapture::EqString(capture, string, is_positive, match_all).</summary>
    EqString,

    /// <summary>TextPredicateCapture::EqCapture(capture, capture, is_positive, match_all).</summary>
    EqCapture,

    /// <summary>TextPredicateCapture::MatchString(capture, regex, is_positive, match_all).</summary>
    MatchString,

    /// <summary>TextPredicateCapture::AnyString(capture, strings, is_positive).</summary>
    AnyString,
}

/// <summary>
/// TextPredicateCapture of the Rust binding: a predicate on the text of the captured nodes.
/// The first bool is whether the capture is positive, the last whether all of the captured
/// nodes must satisfy the predicate (true) or only one of them (false).
/// </summary>
internal sealed class TextPredicateCapture
{
    internal TextPredicateKind Kind;

    /// <summary>The index of the capture the predicate is about.</summary>
    internal uint CaptureId;

    /// <summary>EqCapture: the index of the other capture.</summary>
    internal uint OtherCaptureId;

    /// <summary>EqString: the literal to compare with.</summary>
    internal string? Text;

    /// <summary>EqString: the UTF-8 bytes of Text; Rust compares the bytes of the str.</summary>
    internal byte[]? TextBytes;

    /// <summary>MatchString: the compiled pattern.</summary>
    internal Regex? Expression;

    /// <summary>AnyString: the literals to compare with, as bytes.</summary>
    internal byte[][]? Texts;

    internal bool IsPositive;

    internal bool MatchAllNodes;
}

/// <summary>
/// The predicate part of <see cref="Query"/>: what Query::from_raw_parts of the Rust binding
/// builds from the predicate steps that the C runtime parsed.
/// </summary>
public sealed partial class Query
{
    /// <summary>
    /// #match? and its siblings use <see cref="Regex"/> where Rust uses regex::bytes::Regex:
    /// RegexOptions.CultureInvariant, without Compiled. Both search unanchored (is_match asks
    /// whether the pattern occurs somewhere in the text), both let . stand for one character
    /// that is not a line feed, both make \d \w \s \p{..} Unicode classes, and both ignore case
    /// only by flag. The syntax differs where .NET is the richer engine:
    /// - look-around ((?= (?! (?&lt;= (?&lt;! ) and backreferences (\1) exist only in .NET; the
    ///   Rust engine rejects them, so a query that uses them works here and nowhere else.
    /// - a named group is (?&lt;name&gt;) in .NET; Rust also accepts the Python form
    ///   (?P&lt;name&gt;), which .NET reads as a group named "P".
    /// - $ in .NET also matches before a trailing line feed, which it does in Rust only under
    ///   (?m): a node whose text ends with a line feed can match the pattern "x$" here and
    ///   not in Rust.
    /// - .NET has no \Q..\E quoting, Rust has no \u{...} escape, Rust rejects a pattern that is
    ///   not valid UTF-8, and .NET rejects a pattern that is invalid for it (an unmatched ')'
    ///   or a dangling quantifier) - both become the "Invalid regex" predicate error below.
    /// - (?x), (?s), (?i), (?m) mean the same in both; (?n), (?#), conditionals and balancing
    ///   groups are .NET only.
    /// The query files of the shipped grammars use only the common subset.
    /// </summary>
    private const RegexOptions MatchOptions = RegexOptions.CultureInvariant;

    private TextPredicateCapture[][] _textPredicates = Array.Empty<TextPredicateCapture[]>();

    private QueryProperty[][] _propertySettings = Array.Empty<QueryProperty[]>();

    private QueryPropertyPredicate[][] _propertyPredicates = Array.Empty<QueryPropertyPredicate[]>();

    private QueryPredicate[][] _generalPredicates = Array.Empty<QueryPredicate[]>();

    /// <summary>
    /// The source of the query, kept for the row of a predicate error (Rust hands the source
    /// text to from_raw_parts). It is not part of TSQuery.
    /// </summary>
    private byte[]? _predicateSource;

    /// <summary>The message of the predicate error of the query, when one of its patterns has one.</summary>
    private string? _predicateError;

    /// <summary>The row of the pattern whose predicate is invalid, as Rust's predicate_error sets it.</summary>
    private uint _predicateErrorRow;

    /// <summary>
    /// Query::general_predicates: the predicates of a pattern that neither the C runtime nor the
    /// binding knows, with their operator and arguments.
    /// </summary>
    /// <param name="patternIndex">The index of the pattern.</param>
    /// <returns>The general predicates of the pattern.</returns>
    public ReadOnlySpan<QueryPredicate> GeneralPredicates(uint patternIndex)
    {
        ThrowForInvalidPredicate();
        return _generalPredicates[(int)patternIndex];
    }

    /// <summary>
    /// Query::property_settings: the #set! properties of a pattern, in source order.
    /// </summary>
    /// <param name="patternIndex">The index of the pattern.</param>
    /// <returns>The property settings of the pattern.</returns>
    public ReadOnlySpan<QueryProperty> PropertySettings(uint patternIndex)
    {
        ThrowForInvalidPredicate();
        return _propertySettings[(int)patternIndex];
    }

    /// <summary>
    /// Query::property_predicates: the #is? and #is-not? properties of a pattern, with the flag
    /// that tells them apart.
    /// </summary>
    /// <param name="patternIndex">The index of the pattern.</param>
    /// <returns>The property predicates of the pattern.</returns>
    public ReadOnlySpan<QueryPropertyPredicate> PropertyPredicates(uint patternIndex)
    {
        ThrowForInvalidPredicate();
        return _propertyPredicates[(int)patternIndex];
    }

    /// <summary>
    /// Query::text_predicates, private in Rust: the predicates that need the text of the
    /// captured nodes, in source order.
    /// </summary>
    internal ReadOnlySpan<TextPredicateCapture> TextPredicates(uint patternIndex) =>
        _textPredicates[(int)patternIndex];

    /// <summary>
    /// The Query::new half of the compile: a query whose predicates do not build throws, the
    /// Query::new_raw half (TryCreate) does not.
    /// </summary>
    internal void ThrowForInvalidPredicate()
    {
        if (_predicateError is not null)
        {
            throw new QueryException(QueryError.Syntax, 0, _predicateErrorRow, _predicateError);
        }
    }

    // Query::from_raw_parts, the predicate part: for every pattern the steps of
    // ts_query_predicates_for_pattern are split at the Done steps and dispatched to the four
    // lists of the binding.
    private void BuildPredicates()
    {
        uint patternCount = _patterns.Size;
        _textPredicates = new TextPredicateCapture[patternCount][];
        _propertySettings = new QueryProperty[patternCount][];
        _propertyPredicates = new QueryPropertyPredicate[patternCount][];
        _generalPredicates = new QueryPredicate[patternCount][];
        byte[] source = _predicateSource ?? Array.Empty<byte>();

        for (uint i = 0; i < patternCount && _predicateError is null; i++)
        {
            // Rust: the row of the pattern is the number of line feeds before its start byte.
            uint byteOffset = StartByteForPattern(i);
            uint row = 0;
            for (uint k = 0; k < byteOffset && k < (uint)source.Length; k++)
            {
                if (source[k] == (byte)'\n')
                {
                    row++;
                }
            }

            List<TextPredicateCapture> textPredicates = new List<TextPredicateCapture>();
            List<QueryProperty> propertySettings = new List<QueryProperty>();
            List<QueryPropertyPredicate> propertyPredicates = new List<QueryPropertyPredicate>();
            List<QueryPredicate> generalPredicates = new List<QueryPredicate>();

            ReadOnlySpan<QueryPredicateStep> steps = PredicatesForPattern(i);
            int position = 0;
            for (; ; )
            {
                int end = position;
                while (end < steps.Length && steps[end].Type != QueryPredicateStepType.Done)
                {
                    end++;
                }

                ReadOnlySpan<QueryPredicateStep> predicate = steps.Slice(position, end - position);
                bool isLast = end == steps.Length;
                position = end + 1;

                if (predicate.Length > 0)
                {
                    AddPredicate(predicate, row, textPredicates, propertySettings, propertyPredicates, generalPredicates);
                }

                if (isLast || _predicateError is not null)
                {
                    break;
                }
            }

            _textPredicates[i] = textPredicates.ToArray();
            _propertySettings[i] = propertySettings.ToArray();
            _propertyPredicates[i] = propertyPredicates.ToArray();
            _generalPredicates[i] = generalPredicates.ToArray();
        }
    }

    // One predicate of a pattern: the match on the operator name in from_raw_parts.
    private void AddPredicate(
        ReadOnlySpan<QueryPredicateStep> predicate,
        uint row,
        List<TextPredicateCapture> textPredicates,
        List<QueryProperty> propertySettings,
        List<QueryPropertyPredicate> propertyPredicates,
        List<QueryPredicate> generalPredicates)
    {
        // The check of from_raw_parts that the group starts with its name. ts_query_new of
        // query.c reads a predicate as '#' or '.' plus a name, so a group whose first step is a
        // capture never reaches a compiled query (the C parser rejects "(@x \"a\"\)" with
        // TSQueryErrorSyntax, as the first case of InvalidPredicateErrors shows): the branch is
        // the port of the Rust check and is unreachable through a query the C runtime accepts.
        if (predicate[0].Type != QueryPredicateStepType.StringValue)
        {
            SetPredicateError(row, $"Expected predicate to start with a function name. Got @{CaptureNameForId(predicate[0].ValueId)}.");
            return;
        }

        string operatorName = StringValueForId(predicate[0].ValueId);
        switch (operatorName)
        {
            case "eq?":
            case "not-eq?":
            case "any-eq?":
            case "any-not-eq?":
            {
                if (predicate.Length != 3)
                {
                    SetPredicateError(row, $"Wrong number of arguments to #eq? predicate. Expected 2, got {predicate.Length - 1}.");
                    return;
                }

                if (predicate[1].Type != QueryPredicateStepType.Capture)
                {
                    SetPredicateError(row, $"First argument to #eq? predicate must be a capture name. Got literal \"{StringValueForId(predicate[1].ValueId)}\".");
                    return;
                }

                bool isPositive = operatorName == "eq?" || operatorName == "any-eq?";
                bool matchAllNodes = operatorName == "eq?" || operatorName == "not-eq?";
                if (predicate[2].Type == QueryPredicateStepType.Capture)
                {
                    textPredicates.Add(new TextPredicateCapture
                    {
                        Kind = TextPredicateKind.EqCapture,
                        CaptureId = predicate[1].ValueId,
                        OtherCaptureId = predicate[2].ValueId,
                        IsPositive = isPositive,
                        MatchAllNodes = matchAllNodes,
                    });
                }
                else
                {
                    string value = StringValueForId(predicate[2].ValueId);
                    textPredicates.Add(new TextPredicateCapture
                    {
                        Kind = TextPredicateKind.EqString,
                        CaptureId = predicate[1].ValueId,
                        Text = value,
                        TextBytes = Encoding.UTF8.GetBytes(value),
                        IsPositive = isPositive,
                        MatchAllNodes = matchAllNodes,
                    });
                }

                return;
            }

            case "match?":
            case "not-match?":
            case "any-match?":
            case "any-not-match?":
            {
                if (predicate.Length != 3)
                {
                    SetPredicateError(row, $"Wrong number of arguments to #match? predicate. Expected 2, got {predicate.Length - 1}.");
                    return;
                }

                if (predicate[1].Type != QueryPredicateStepType.Capture)
                {
                    SetPredicateError(row, $"First argument to #match? predicate must be a capture name. Got literal \"{StringValueForId(predicate[1].ValueId)}\".");
                    return;
                }

                if (predicate[2].Type == QueryPredicateStepType.Capture)
                {
                    SetPredicateError(row, $"Second argument to #match? predicate must be a literal. Got capture @{CaptureNameForId(predicate[2].ValueId)}.");
                    return;
                }

                string pattern = StringValueForId(predicate[2].ValueId);
                Regex expression;
                try
                {
                    expression = new Regex(pattern, MatchOptions);
                }
                catch (ArgumentException)
                {
                    SetPredicateError(row, $"Invalid regex '{pattern}'");
                    return;
                }

                textPredicates.Add(new TextPredicateCapture
                {
                    Kind = TextPredicateKind.MatchString,
                    CaptureId = predicate[1].ValueId,
                    Expression = expression,
                    IsPositive = operatorName == "match?" || operatorName == "any-match?",
                    MatchAllNodes = operatorName == "match?" || operatorName == "not-match?",
                });
                return;
            }

            case "set!":
            {
                QueryProperty? property = ParseProperty(row, operatorName, predicate.Slice(1));
                if (property is not null)
                {
                    propertySettings.Add(property);
                }

                return;
            }

            case "is?":
            case "is-not?":
            {
                QueryProperty? property = ParseProperty(row, operatorName, predicate.Slice(1));
                if (property is not null)
                {
                    propertyPredicates.Add(new QueryPropertyPredicate(property, operatorName == "is?"));
                }

                return;
            }

            case "any-of?":
            case "not-any-of?":
            {
                if (predicate.Length < 2)
                {
                    SetPredicateError(row, $"Wrong number of arguments to #any-of? predicate. Expected at least 1, got {predicate.Length - 1}.");
                    return;
                }

                if (predicate[1].Type != QueryPredicateStepType.Capture)
                {
                    SetPredicateError(row, $"First argument to #any-of? predicate must be a capture name. Got literal \"{StringValueForId(predicate[1].ValueId)}\".");
                    return;
                }

                List<byte[]> values = new List<byte[]>();
                for (int a = 2; a < predicate.Length; a++)
                {
                    if (predicate[a].Type == QueryPredicateStepType.Capture)
                    {
                        SetPredicateError(row, $"Arguments to #any-of? predicate must be literals. Got capture @{CaptureNameForId(predicate[a].ValueId)}.");
                        return;
                    }

                    values.Add(Encoding.UTF8.GetBytes(StringValueForId(predicate[a].ValueId)));
                }

                textPredicates.Add(new TextPredicateCapture
                {
                    Kind = TextPredicateKind.AnyString,
                    CaptureId = predicate[1].ValueId,
                    Texts = values.ToArray(),
                    IsPositive = operatorName == "any-of?",
                });
                return;
            }

            default:
            {
                QueryPredicateArg[] args = new QueryPredicateArg[predicate.Length - 1];
                for (int a = 1; a < predicate.Length; a++)
                {
                    args[a - 1] = predicate[a].Type == QueryPredicateStepType.Capture
                        ? QueryPredicateArg.FromCapture(predicate[a].ValueId)
                        : QueryPredicateArg.FromString(StringValueForId(predicate[a].ValueId));
                }

                generalPredicates.Add(new QueryPredicate(operatorName, args));
                return;
            }
        }
    }

    // Query::parse_property of the Rust binding: the key, the value and the optional capture
    // of a #set!, #is? or #is-not? predicate.
    private QueryProperty? ParseProperty(uint row, string functionName, ReadOnlySpan<QueryPredicateStep> args)
    {
        if (args.Length == 0 || args.Length > 3)
        {
            SetPredicateError(row, $"Wrong number of arguments to {functionName} predicate. Expected 1 to 3, got {args.Length}.");
            return null;
        }

        uint? captureId = null;
        string? key = null;
        string? value = null;

        for (int a = 0; a < args.Length; a++)
        {
            QueryPredicateStep arg = args[a];
            if (arg.Type == QueryPredicateStepType.Capture)
            {
                if (captureId is not null)
                {
                    SetPredicateError(row, $"Invalid arguments to {functionName} predicate. Unexpected second capture name @{CaptureNameForId(arg.ValueId)}");
                    return null;
                }

                captureId = arg.ValueId;
            }
            else if (key is null)
            {
                key = StringValueForId(arg.ValueId);
            }
            else if (value is null)
            {
                value = StringValueForId(arg.ValueId);
            }
            else
            {
                SetPredicateError(row, $"Invalid arguments to {functionName} predicate. Unexpected third argument @{StringValueForId(arg.ValueId)}");
                return null;
            }
        }

        if (key is null)
        {
            SetPredicateError(row, $"Invalid arguments to {functionName} predicate. Missing key argument");
            return null;
        }

        return new QueryProperty(key, value, captureId);
    }

    // predicate_error of the Rust binding: kind Predicate (QueryError.Syntax here, see the top
    // of the file), the row of the pattern, column 0, offset 0 and the message.
    private void SetPredicateError(uint row, string message)
    {
        _predicateError = message;
        _predicateErrorRow = row;
    }
}
