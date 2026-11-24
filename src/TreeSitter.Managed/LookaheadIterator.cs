// Port of tree-sitter v0.27.2 (3fb7be4): the TSLookaheadIterator part of
// lib/src/language.c (ts_lookahead_iterator_new to ts_lookahead_iterator_current_symbol_name)
// and of the TSLookaheadIterator section of api.h.
// Rules:
// - The LookaheadIterator struct of language.h is the LanguageLookaheadIterator of
//   Language.cs, which is created by ts_language_lookaheads (Language.Lookaheads) and
//   advanced by ts_lookahead_iterator__next (LanguageLookaheadIterator.Next).
// - ts_language_copy and ts_language_delete only refcount wasm languages; for a native
//   language both are no-ops (language.c). The port therefore keeps a plain reference to
//   the Language, so ts_lookahead_iterator_delete (the free of the malloc'ed struct) has
//   no counterpart: the garbage collector frees the iterator. "The iterator retains the
//   language" (api.h) holds the same way.
// - The state of a failed reset is not defined by the C code: a reset with a state that is
//   not below the state count leaves the iterator as it was, exactly like the C return
//   without an assignment.

namespace TreeSitter;

/// <summary>
/// A lookahead iterator (TSLookaheadIterator): the symbols that are valid in one parse
/// state. Repeatedly calling <see cref="Next"/> and reading <see cref="CurrentSymbol"/>
/// generates the valid symbols of the state, as api.h describes it. A newly created or
/// freshly reset iterator is not positioned on a symbol until <see cref="Next"/> is called.
/// </summary>
public sealed class LookaheadIterator
{
    // C: iterator->language, the language the iterator was created with or last reset to.
    private Language _language;

    // C: *iterator = ts_language_lookaheads(...): the LookaheadIterator of language.h.
    private LanguageLookaheadIterator _iterator;

    // ts_lookahead_iterator_new, without the state check: Language.CreateLookaheadIterator
    // and Reset keep the check, as the C functions do.
    internal LookaheadIterator(Language language, ushort state)
    {
        _language = language;
        _iterator = language.Lookaheads(state);
    }

    /// <summary>ts_lookahead_iterator_language: the current language of the iterator.</summary>
    public Language Language => _language;

    /// <summary>
    /// ts_lookahead_iterator_reset_state: resets the iterator to another state of its
    /// language. A reset iterator is not positioned on a symbol.
    /// </summary>
    /// <param name="state">The parse state to enumerate.</param>
    /// <returns>True when the state is below the state count of the language.</returns>
    public bool ResetState(ushort state)
    {
        if (state >= _language.StateCount)
        {
            return false;
        }

        _iterator = _language.Lookaheads(state);
        return true;
    }

    /// <summary>
    /// ts_lookahead_iterator_reset: resets the iterator to a state of another language.
    /// A reset iterator is not positioned on a symbol.
    /// </summary>
    /// <param name="language">The language to enumerate.</param>
    /// <param name="state">The parse state of that language.</param>
    /// <returns>True when the state is below the state count of the new language.</returns>
    public bool Reset(Language language, ushort state)
    {
        ArgumentNullException.ThrowIfNull(language);

        if (state >= language.StateCount)
        {
            return false;
        }

        _iterator = language.Lookaheads(state);
        _language = language;
        return true;
    }

    /// <summary>ts_lookahead_iterator_next: advances to the next valid symbol of the state.</summary>
    /// <returns>True when there is a new symbol, false at the end of the state.</returns>
    public bool Next() => _iterator.Next();

    /// <summary>
    /// ts_lookahead_iterator_current_symbol. It is only meaningful when the most recent
    /// call to <see cref="Next"/> returned true; before the first Next it is the
    /// UINT16_MAX the C iterator starts with.
    /// </summary>
    public ushort CurrentSymbol => _iterator.Symbol;

    /// <summary>
    /// ts_lookahead_iterator_current_symbol_name: null unless the most recent call to
    /// <see cref="Next"/> returned true.
    /// </summary>
    /// <returns>The name of the current symbol, or null when the iterator is not positioned.</returns>
    public string? CurrentSymbolName =>
        _iterator.IsPositioned ? _language.SymbolName(_iterator.Symbol) : null;
}
