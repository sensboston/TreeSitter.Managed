// The query cursor side of the predicate layer of the tree-sitter Rust binding v0.27.2
// (lib/binding_rust/lib.rs): QueryMatches and QueryCaptures check the text predicates of every
// match the cursor produces, and QueryMatch::satisfies_text_predicates is the check itself.
//
// Rules:
// - The C functions ts_query_cursor_next_match and ts_query_cursor_next_capture stay as they
//   are (they are the port of query.c); the predicate check is the binding's own work, so it
//   lives in these iterators.
// - QueryMatches::advance: a match that does not satisfy the predicates is skipped and the
//   loop asks the cursor for the next one. QueryCaptures::advance: the same, plus
//   result.remove() - ts_query_cursor_remove_match - for the rejected match, so that its
//   remaining captures are dropped too.
// - The text of a node comes from the QueryTextProvider, the shape of Rust's TextProvider
//   trait. The buffers Rust uses to join the chunks of a node are not needed: one call of the
//   provider returns the whole text.
// - MatchString compares the .NET regular expression against the UTF-8 decoded text of the
//   node, where regex::bytes matches the bytes themselves; for the UTF-8 text of a source both
//   find the same match. See the notes on the syntax of #match? in QueryPredicates.cs.

using System.Text;

namespace TreeSitter;

/// <summary>
/// A capture of a match: the match and the index of the capture in it, the element of Rust's
/// QueryCaptures iterator ((QueryMatch, usize)).
/// </summary>
/// <param name="Match">The match the capture belongs to.</param>
/// <param name="CaptureIndex">The index of the capture inside the match.</param>
public readonly record struct QueryCaptureMatch(QueryMatch Match, uint CaptureIndex)
{
    /// <summary>The capture itself (Rust: match.captures()[index]).</summary>
    public QueryCapture Capture => Match.Captures[(int)CaptureIndex];
}

/// <summary>
/// QueryMatches of the Rust binding: the matches of one execution, with the matches that do not
/// satisfy the text predicates of their pattern skipped.
/// </summary>
public sealed class QueryMatches : IEnumerable<QueryMatch>
{
    private readonly Query _query;
    private readonly QueryCursor _cursor;
    private readonly QueryTextProvider _textProvider;

    internal QueryMatches(QueryCursor cursor, Query query, QueryTextProvider textProvider)
    {
        _cursor = cursor;
        _query = query;
        _textProvider = textProvider;
    }

    /// <summary>
    /// QueryMatches::advance: the next match of the cursor that satisfies the predicates, or
    /// false at the end.
    /// </summary>
    /// <param name="match">The match, when there is one.</param>
    /// <returns>True when a match was produced.</returns>
    public bool TryGetNext(out QueryMatch match)
    {
        while (_cursor.NextMatch(out QueryMatch candidate))
        {
            if (candidate.SatisfiesTextPredicates(_query, _textProvider))
            {
                match = candidate;
                return true;
            }
        }

        match = default;
        return false;
    }

    /// <summary>
    /// Enumerates the matches. The cursor is the state of the enumeration: a second pass over
    /// the same object continues where the first one stopped and produces nothing more, as the
    /// StreamingIterator of Rust does.
    /// </summary>
    /// <returns>The enumerator of the matches.</returns>
    public IEnumerator<QueryMatch> GetEnumerator()
    {
        while (TryGetNext(out QueryMatch match))
        {
            yield return match;
        }
    }

    /// <summary>The non-generic form of <see cref="GetEnumerator"/>.</summary>
    /// <returns>The enumerator of the matches.</returns>
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// QueryCaptures of the Rust binding: the captures of one execution in order, with the captures
/// of a match that does not satisfy the text predicates dropped (and the match removed from the
/// cursor, as result.remove() does).
/// </summary>
public sealed class QueryCaptures : IEnumerable<QueryCaptureMatch>
{
    private readonly Query _query;
    private readonly QueryCursor _cursor;
    private readonly QueryTextProvider _textProvider;

    internal QueryCaptures(QueryCursor cursor, Query query, QueryTextProvider textProvider)
    {
        _cursor = cursor;
        _query = query;
        _textProvider = textProvider;
    }

    /// <summary>
    /// QueryCaptures::advance: the next capture whose match satisfies the predicates, or false
    /// at the end.
    /// </summary>
    /// <param name="capture">The capture and its match, when there is one.</param>
    /// <returns>True when a capture was produced.</returns>
    public bool TryGetNext(out QueryCaptureMatch capture)
    {
        while (_cursor.NextCapture(out QueryMatch candidate, out uint captureIndex))
        {
            if (candidate.SatisfiesTextPredicates(_query, _textProvider))
            {
                capture = new QueryCaptureMatch(candidate, captureIndex);
                return true;
            }

            // Rust: result.remove(), so the other captures of the rejected match go too.
            _cursor.RemoveMatch(candidate.Id);
        }

        capture = default;
        return false;
    }

    /// <summary>
    /// Enumerates the captures. As in <see cref="QueryMatches"/>, the cursor holds the state, so
    /// a second pass continues instead of restarting.
    /// </summary>
    /// <returns>The enumerator of the captures.</returns>
    public IEnumerator<QueryCaptureMatch> GetEnumerator()
    {
        while (TryGetNext(out QueryCaptureMatch capture))
        {
            yield return capture;
        }
    }

    /// <summary>The non-generic form of <see cref="GetEnumerator"/>.</summary>
    /// <returns>The enumerator of the captures.</returns>
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The predicate part of <see cref="QueryCursor"/>: the two iterator entries of the Rust binding,
/// which execute the query on the node and check the text predicates while they enumerate.
/// </summary>
public sealed partial class QueryCursor
{
    /// <summary>
    /// QueryCursor::matches with the source as the text provider: ts_query_cursor_exec on the
    /// node, then the matches whose text predicates hold.
    /// </summary>
    /// <param name="query">The query to run.</param>
    /// <param name="node">The node to run it on.</param>
    /// <param name="source">The source text the node was parsed from.</param>
    /// <returns>The matches of the execution.</returns>
    public QueryMatches Matches(Query query, Node node, ReadOnlyMemory<byte> source) =>
        Matches(query, node, SourceTextProvider(source));

    /// <summary>
    /// QueryCursor::matches with a provider of the text of a node.
    /// </summary>
    /// <param name="query">The query to run.</param>
    /// <param name="node">The node to run it on.</param>
    /// <param name="textProvider">The text of one node, as Rust's TextProvider.</param>
    /// <returns>The matches of the execution.</returns>
    public QueryMatches Matches(Query query, Node node, QueryTextProvider textProvider)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(textProvider);

        query.ThrowForInvalidPredicate();
        Exec(query, node);
        return new QueryMatches(this, query, textProvider);
    }

    /// <summary>
    /// QueryCursor::captures with the source as the text provider: ts_query_cursor_exec on the
    /// node, then the captures in order.
    /// </summary>
    /// <param name="query">The query to run.</param>
    /// <param name="node">The node to run it on.</param>
    /// <param name="source">The source text the node was parsed from.</param>
    /// <returns>The captures of the execution.</returns>
    public QueryCaptures Captures(Query query, Node node, ReadOnlyMemory<byte> source) =>
        Captures(query, node, SourceTextProvider(source));

    /// <summary>
    /// QueryCursor::captures with a provider of the text of a node.
    /// </summary>
    /// <param name="query">The query to run.</param>
    /// <param name="node">The node to run it on.</param>
    /// <param name="textProvider">The text of one node, as Rust's TextProvider.</param>
    /// <returns>The captures of the execution.</returns>
    public QueryCaptures Captures(Query query, Node node, QueryTextProvider textProvider)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(textProvider);

        query.ThrowForInvalidPredicate();
        Exec(query, node);
        return new QueryCaptures(this, query, textProvider);
    }

    /// <summary>
    /// The &amp;[u8] text provider of Rust (source.as_bytes()): the text of a node is its slice of
    /// the source.
    /// </summary>
    internal static QueryTextProvider SourceTextProvider(ReadOnlyMemory<byte> source) =>
        node => source.Slice((int)node.StartByte, (int)(node.EndByte - node.StartByte));
}

/// <summary>
/// The predicate part of <see cref="QueryMatch"/>: QueryMatch::satisfies_text_predicates of the
/// Rust binding.
/// </summary>
public readonly partial struct QueryMatch
{
    /// <summary>
    /// QueryMatch::satisfies_text_predicates: whether every text predicate of the matched
    /// pattern holds for the captures of this match.
    /// </summary>
    /// <param name="query">The query the match came from.</param>
    /// <param name="textProvider">The text of one node.</param>
    /// <returns>True when the match satisfies every text predicate of its pattern.</returns>
    public bool SatisfiesTextPredicates(Query query, QueryTextProvider textProvider)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(textProvider);

        foreach (TextPredicateCapture predicate in query.TextPredicates(PatternIndex))
        {
            if (!Satisfies(predicate, textProvider))
            {
                return false;
            }
        }

        return true;
    }

    // One arm of the match of satisfies_text_predicates: the same four shapes, the same two
    // flags and the same early exits as in Rust.
    private bool Satisfies(TextPredicateCapture predicate, QueryTextProvider textProvider)
    {
        switch (predicate.Kind)
        {
            case TextPredicateKind.EqCapture:
            {
                CaptureNodes nodes1 = new(Captures, predicate.CaptureId);
                CaptureNodes nodes2 = new(Captures, predicate.OtherCaptureId);
                while (nodes1.TryPeek(out Node node1) && nodes2.TryPeek(out Node node2))
                {
                    nodes1.TryNext(out node1);
                    nodes2.TryNext(out node2);
                    bool isPositiveMatch = Text(textProvider, node1).Span.SequenceEqual(Text(textProvider, node2).Span);
                    if (isPositiveMatch != predicate.IsPositive && predicate.MatchAllNodes)
                    {
                        return false;
                    }

                    if (isPositiveMatch == predicate.IsPositive && !predicate.MatchAllNodes)
                    {
                        return true;
                    }
                }

                // Rust: nodes_1.next().is_none() && nodes_2.next().is_none() - the two lists of
                // nodes have to be equally long.
                return !nodes1.TryNext(out _) && !nodes2.TryNext(out _);
            }

            case TextPredicateKind.EqString:
            {
                CaptureNodes nodes = new(Captures, predicate.CaptureId);
                while (nodes.TryNext(out Node node))
                {
                    bool isPositiveMatch = Text(textProvider, node).Span.SequenceEqual(predicate.TextBytes!.AsSpan());
                    if (isPositiveMatch != predicate.IsPositive && predicate.MatchAllNodes)
                    {
                        return false;
                    }

                    if (isPositiveMatch == predicate.IsPositive && !predicate.MatchAllNodes)
                    {
                        return true;
                    }
                }

                return true;
            }

            case TextPredicateKind.MatchString:
            {
                CaptureNodes nodes = new(Captures, predicate.CaptureId);
                while (nodes.TryNext(out Node node))
                {
                    bool isPositiveMatch = predicate.Expression!.IsMatch(
                        Encoding.UTF8.GetString(Text(textProvider, node).Span));
                    if (isPositiveMatch != predicate.IsPositive && predicate.MatchAllNodes)
                    {
                        return false;
                    }

                    if (isPositiveMatch == predicate.IsPositive && !predicate.MatchAllNodes)
                    {
                        return true;
                    }
                }

                return true;
            }

            default:
            {
                // TextPredicateCapture::AnyString.
                CaptureNodes nodes = new(Captures, predicate.CaptureId);
                while (nodes.TryNext(out Node node))
                {
                    ReadOnlySpan<byte> text = Text(textProvider, node).Span;
                    bool found = false;
                    foreach (byte[] value in predicate.Texts!)
                    {
                        if (text.SequenceEqual(value.AsSpan()))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (found != predicate.IsPositive)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    private static ReadOnlyMemory<byte> Text(QueryTextProvider textProvider, Node node) => textProvider(node);

    /// <summary>
    /// nodes_for_capture_index of the Rust binding: the nodes captured with one capture index,
    /// in the order of the captures of the match. Peek is the peekable() of Rust.
    /// </summary>
    private struct CaptureNodes
    {
        private readonly QueryCapture[] _captures;
        private readonly uint _index;
        private int _position;

        internal CaptureNodes(QueryCapture[] captures, uint index)
        {
            _captures = captures;
            _index = index;
            _position = 0;
        }

        internal bool TryNext(out Node node)
        {
            while (_position < _captures.Length)
            {
                QueryCapture capture = _captures[_position++];
                if (capture.Index == _index)
                {
                    node = capture.Node;
                    return true;
                }
            }

            node = default;
            return false;
        }

        internal bool TryPeek(out Node node)
        {
            int position = _position;
            bool found = TryNext(out node);
            _position = position;
            return found;
        }
    }
}
