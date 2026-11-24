namespace TreeSitter.Generator.CSource;

/// <summary>
/// A single token produced by the C tokenizer.
/// </summary>
public sealed class CToken
{
    public CTokenKind Kind { get; }
    public string Text { get; }
    public int Line { get; }
    public int Column { get; }

    public SourcePos Pos => new(Line, Column);

    public CToken(CTokenKind kind, string text, int line, int column)
    {
        Kind = kind;
        Text = text;
        Line = line;
        Column = column;
    }
}

/// <summary>
/// Kinds of tokens produced by the C tokenizer.
/// </summary>
public enum CTokenKind
{
    Identifier,
    NumberLiteral,
    StringLiteral,
    CharLiteral,
    Punctuator,
    PreprocessorDirective,
    EOF,
}
