using TreeSitter.Abi;
using TreeSitter.Generator.CSource;
using TreeSitter.Generator.Lex;

namespace TreeSitter.Generator.Model;

/// <summary>Everything ts2cs needs from one parser.c.</summary>
public sealed class GrammarModel
{
    /// <summary>The tables, filled exactly from the TSLanguage initializer.</summary>
    public required LanguageTables Tables { get; init; }

    /// <summary>The function named by TSLanguage.lex_fn, or null.</summary>
    public required CFunction? LexFunction { get; init; }

    /// <summary>The function named by TSLanguage.keyword_lex_fn, or null.</summary>
    public required CFunction? KeywordLexFunction { get; init; }

    /// <summary>Parsed body of LexFunction, or null.</summary>
    public required LexProgram? Lex { get; init; }

    /// <summary>Parsed body of KeywordLexFunction, or null.</summary>
    public required LexProgram? KeywordLex { get; init; }

    /// <summary>Values of the ACCEPT_TOKEN symbols of Lex and KeywordLex by name, resolved like any C name (enum member, #define, parser.h macro).</summary>
    public required IReadOnlyDictionary<string, long> SymbolValues { get; init; }

    /// <summary>All TSCharacterRange arrays of the file by variable name (used by lexers, M1b).</summary>
    public required IReadOnlyDictionary<string, CharacterRange[]> CharacterSets { get; init; }

    /// <summary>External scanner function names from the initializer (create, destroy, scan, serialize, deserialize); null when NULL.</summary>
    public required IReadOnlyDictionary<string, string?> ExternalScannerFunctions { get; init; }
}
