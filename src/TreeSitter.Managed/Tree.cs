// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/tree.c, lib/src/tree.h (without ts_tree_edit,
// ts_tree_get_changed_ranges and ts_tree_print_dot_graph: M1d).
// Rules: docs/ARCHITECTURE.md D1, D2. Each method has the C name above it and the same logic.
// - Dispose is ts_tree_delete: it releases the root once. Copy retains the root (ts_tree_copy).
// - The root slot of a node (TSNode.id == &tree->root) is Node.Owner == this tree, Slot 0.

using System;
using TreeSitter.Abi;
using TreeSitter.Subtrees;

namespace TreeSitter;

/// <summary>A syntax tree (TSTree).</summary>
public sealed class Tree : IDisposable
{
    private Subtree _root;
    private readonly Language _language;
    private readonly Range[] _includedRanges;

    // ts_tree_edit changes the root and the included ranges in place.

    // ts_tree_new: takes the root reference; copies the included ranges.
    internal Tree(Subtree root, Language language, ReadOnlySpan<Range> includedRanges)
    {
        ArgumentNullException.ThrowIfNull(language);
        _root = root;
        _language = language;
        _includedRanges = includedRanges.ToArray();
    }

    /// <summary>The root subtree (TSTree.root).</summary>
    internal Subtree Root => _root;

    /// <summary>TSTree.included_ranges without a copy.</summary>
    internal ReadOnlySpan<Range> IncludedRangesSpan => _includedRanges;

    /// <summary>ts_tree_copy</summary>
    public Tree Copy()
    {
        SubtreeOps.Retain(_root);
        return new Tree(_root, _language, _includedRanges);
    }

    /// <summary>ts_tree_delete</summary>
    public void Dispose()
    {
        SubtreePool pool = new SubtreePool(0);
        SubtreeOps.Release(pool, _root);
        pool.Delete();
    }

    /// <summary>ts_tree_edit: edits the tree to keep it in sync with an edit of the source.</summary>
    public void Edit(InputEdit edit)
    {
        for (int i = 0; i < _includedRanges.Length; i++)
        {
            _includedRanges[i] = TreeSitter.Parsing.ChangedRanges.RangeEdit(_includedRanges[i], edit);
        }

        SubtreePool pool = new SubtreePool(0);
        _root = SubtreeOps.Edit(_root, edit, pool);
        pool.Delete();
    }

    /// <summary>
    /// ts_tree_get_changed_ranges: the ranges whose syntactic structure differs between an
    /// old tree (edited with Edit) and a new tree parsed from it.
    /// </summary>
    public static Range[] GetChangedRanges(Tree oldTree, Tree newTree)
    {
        ArgumentNullException.ThrowIfNull(oldTree);
        ArgumentNullException.ThrowIfNull(newTree);
        return TreeSitter.Parsing.ChangedRanges.Get(oldTree, newTree);
    }

    /// <summary>ts_tree_root_node</summary>
    public Node RootNode => new Node(this, this, 0, _root.Padding, 0);

    /// <summary>ts_tree_root_node_with_offset</summary>
    public Node RootNodeWithOffset(uint offsetBytes, Point offsetExtent)
    {
        Length offset = new Length(offsetBytes, offsetExtent);
        Length position = Length.Add(offset, _root.Padding);
        return new Node(this, this, 0, position, 0);
    }

    /// <summary>ts_tree_language</summary>
    public Language Language => _language;

    /// <summary>ts_tree_included_ranges (a new array on every call, like C returns a copy).</summary>
    public Range[] IncludedRanges()
    {
        Range[] copy = new Range[_includedRanges.Length];
        Array.Copy(_includedRanges, copy, _includedRanges.Length);
        return copy;
    }

    /// <summary>ts_tree_print_dot_graph: prints the dot graph of the tree.</summary>
    /// <param name="writer">The text writer to print the graph to. The nodes are numbered
    ///   in the order of their first appearance (the address of a node in C).</param>
    public void PrintDotGraph(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        SubtreeOps.PrintDotGraph(_root, _language, writer);
    }
}
