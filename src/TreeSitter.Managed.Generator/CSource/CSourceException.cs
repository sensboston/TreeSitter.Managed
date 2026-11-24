namespace TreeSitter.Generator.CSource;

/// <summary>
/// Error in a C source file. The message has the form "file:line:column: text", so tools
/// and editors can jump to the place.
/// </summary>
public sealed class CSourceException : Exception
{
    public CSourceException(string fileName, SourcePos pos, string text)
        : base($"{fileName}:{pos.Line}:{pos.Column}: {text}")
    {
        FileName = fileName;
        Pos = pos;
        Text = text;
    }

    public CSourceException()
    {
        FileName = string.Empty;
        Text = string.Empty;
    }

    public CSourceException(string message)
        : base(message)
    {
        FileName = string.Empty;
        Text = message;
    }

    public CSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
        FileName = string.Empty;
        Text = message;
    }

    public string FileName { get; }

    public SourcePos Pos { get; }

    /// <summary>The message without the "file:line:column: " prefix.</summary>
    public string Text { get; }
}
