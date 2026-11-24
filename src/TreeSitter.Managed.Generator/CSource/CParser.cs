// Reads the token list of a generated parser.c into a CTranslationUnit.
//
// Supported, and nothing else (anything else is a CSourceException with position):
// - preprocessor: #include, #pragma (ignored); #define NAME [expr] (recorded);
//   #if / #ifdef / #elif / #else / #endif evaluated with NO predefined macros, conditions
//   made of defined(NAME), NAME (for #ifdef) and ||. Lines in false branches are dropped.
// - enum NAME { MEMBER [= expr], ... };
// - static const TYPE NAME[...]... = INIT;
// - static bool NAME(PARAMS) { BODY }  (kept as tokens)
// - prototypes: TYPE [*]NAME(PARAMS);
// - extern "C" { ... } (only reachable when __cplusplus were defined, so normally dropped)
// - TS_PUBLIC const TSLanguage *tree_sitter_NAME(void) { static const TSLanguage language = INIT; return &language; }
//
// The file must be read as Latin-1 (CSourceFile.Read) so that every byte is one char.

namespace TreeSitter.Generator.CSource;

using System.Text;

public sealed class CParser
{
    private readonly List<CToken> _sourceTokens;
    private List<CToken> _tokens;
    private int _index;

    public CParser(IReadOnlyList<CToken> tokens, string fileName)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(fileName);
        Tokens = tokens;
        FileName = fileName;
        _sourceTokens = EnsureSingleEof(tokens);
        _tokens = _sourceTokens;
    }

    public IReadOnlyList<CToken> Tokens { get; }

    public string FileName { get; }

    /// <summary>Reads the whole file.</summary>
    public CTranslationUnit ParseTranslationUnit()
    {
        var defines = new List<CDefine>();
        _tokens = Preprocess(defines);
        _index = 0;

        var enums = new List<CEnumDeclaration>();
        var variables = new List<CVariable>();
        var functions = new List<CFunction>();
        var prototypes = new List<CPrototype>();
        var externBlocks = new List<CToken>();
        CLanguageFunction? language = null;

        while (Current.Kind != CTokenKind.EOF)
        {
            if (IsText("}"))
            {
                if (externBlocks.Count == 0)
                {
                    throw Error(Current, "Unexpected closing brace at file scope.");
                }

                Consume();
                externBlocks.RemoveAt(externBlocks.Count - 1);
                continue;
            }

            if (IsExternCBlock())
            {
                externBlocks.Add(Current);
                Consume();
                Consume();
                Consume();
                continue;
            }

            if (IsIdentifier("enum"))
            {
                enums.Add(ParseEnumDeclaration());
                continue;
            }

            if (IsIdentifier("TS_PUBLIC"))
            {
                if (language is not null)
                {
                    throw Error(Current, "More than one TS_PUBLIC language function was found.");
                }

                language = ParseLanguageFunction();
                continue;
            }

            if (IsIdentifier("static") && Peek(1).Kind == CTokenKind.Identifier &&
                string.Equals(Peek(1).Text, "bool", StringComparison.Ordinal))
            {
                functions.Add(ParseFunctionDefinition());
                continue;
            }

            if (IsIdentifier("static") && Peek(1).Kind == CTokenKind.Identifier &&
                string.Equals(Peek(1).Text, "const", StringComparison.Ordinal))
            {
                variables.Add(ParseVariableDeclaration());
                continue;
            }

            if (LooksLikePrototype())
            {
                prototypes.Add(ParsePrototype());
                continue;
            }

            throw Error(Current, $"Unsupported file-scope construct beginning with '{Current.Text}'.");
        }

        if (externBlocks.Count > 0)
        {
            throw Error(externBlocks[^1], "Unterminated extern \"C\" block.");
        }

        if (language is null)
        {
            throw Error(Current, "Missing TS_PUBLIC language function.");
        }

        return new CTranslationUnit(FileName, defines, enums, variables, functions, prototypes, language);
    }

    /// <summary>Reads one expression starting at the current token (used by tests).</summary>
    public CExpr ParseExpression()
    {
        // additive-expression: unary (('+' | '-') unary)*, left associative.
        CExpr left = ParseUnaryExpression();
        while (IsText("+") || IsText("-"))
        {
            CToken op = Consume();
            CExpr right = ParseUnaryExpression();
            left = new CBinary(op.Pos, op.Text[0], left, right);
        }

        return left;
    }

    // Words that can make up a type name inside a cast. A parenthesized name that is not made
    // of these words is an expression, so "(id) - X" is a subtraction, not a cast of -X.
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "const", "void", "char", "signed", "unsigned", "short", "int", "long", "bool",
        "int8_t", "int16_t", "int32_t", "uint8_t", "uint16_t", "uint32_t",
        "TSSymbol", "TSStateId", "TSFieldId", "TSLexer", "TSLanguage", "TSLexMode", "TSLexerMode",
        "TSParseActionEntry", "TSSymbolMetadata", "TSMapSlice", "TSFieldMapEntry", "TSCharacterRange",
    };

    /// <summary>Reads one initializer starting at the current token (used by tests).</summary>
    public CInitializer ParseInitializer() => ParseInitializerCore();

    private static List<CToken> EnsureSingleEof(IReadOnlyList<CToken> tokens)
    {
        var result = new List<CToken>(tokens.Count + 1);
        CToken? eof = null;
        foreach (CToken token in tokens)
        {
            if (token.Kind == CTokenKind.EOF)
            {
                eof = token;
            }
            else
            {
                result.Add(token);
            }
        }

        if (eof is null)
        {
            if (result.Count == 0)
            {
                eof = new CToken(CTokenKind.EOF, string.Empty, 1, 1);
            }
            else
            {
                CToken last = result[^1];
                eof = new CToken(CTokenKind.EOF, string.Empty, last.Line, last.Column + last.Text.Length);
            }
        }

        result.Add(eof);
        return result;
    }

    private List<CToken> Preprocess(List<CDefine> defines)
    {
        var result = new List<CToken>(_sourceTokens.Count);
        var definedMacros = new HashSet<string>(StringComparer.Ordinal);
        var conditionals = new List<ConditionalFrame>();
        bool active = true;

        foreach (CToken token in _sourceTokens)
        {
            if (token.Kind == CTokenKind.EOF)
            {
                if (conditionals.Count > 0)
                {
                    throw Error(conditionals[^1].Directive, "Missing #endif for conditional directive.");
                }

                result.Add(token);
                break;
            }

            if (token.Kind != CTokenKind.PreprocessorDirective)
            {
                if (active)
                {
                    result.Add(token);
                }

                continue;
            }

            DirectiveParts directive = ReadDirective(token);
            switch (directive.Name)
            {
                case "if":
                {
                    bool condition = EvaluateIfCondition(directive, token, definedMacros);
                    var frame = new ConditionalFrame(active, condition, token);
                    conditionals.Add(frame);
                    active = frame.CurrentActive;
                    break;
                }
                case "ifdef":
                {
                    bool condition = EvaluateIfdefCondition(directive, token, definedMacros);
                    var frame = new ConditionalFrame(active, condition, token);
                    conditionals.Add(frame);
                    active = frame.CurrentActive;
                    break;
                }
                case "elif":
                {
                    if (conditionals.Count == 0)
                    {
                        throw Error(token, "#elif without a matching #if.");
                    }

                    ConditionalFrame frame = conditionals[^1];
                    if (frame.HasElse)
                    {
                        throw Error(token, "#elif after #else.");
                    }

                    bool condition = EvaluateIfCondition(directive, token, definedMacros);
                    frame.CurrentActive = frame.ParentActive && !frame.AnyBranchTaken && condition;
                    frame.AnyBranchTaken |= condition;
                    conditionals[^1] = frame;
                    active = frame.CurrentActive;
                    break;
                }
                case "else":
                {
                    EnsureNoDirectiveArguments(directive, token);
                    if (conditionals.Count == 0)
                    {
                        throw Error(token, "#else without a matching #if.");
                    }

                    ConditionalFrame frame = conditionals[^1];
                    if (frame.HasElse)
                    {
                        throw Error(token, "Duplicate #else directive.");
                    }

                    frame.HasElse = true;
                    frame.CurrentActive = frame.ParentActive && !frame.AnyBranchTaken;
                    frame.AnyBranchTaken = true;
                    conditionals[^1] = frame;
                    active = frame.CurrentActive;
                    break;
                }
                case "endif":
                {
                    EnsureNoDirectiveArguments(directive, token);
                    if (conditionals.Count == 0)
                    {
                        throw Error(token, "#endif without a matching #if.");
                    }

                    conditionals.RemoveAt(conditionals.Count - 1);
                    active = conditionals.Count == 0 || conditionals[^1].CurrentActive;
                    break;
                }
                case "define":
                    if (active)
                    {
                        CDefine define = ParseDefine(directive, token);
                        defines.Add(define);
                        definedMacros.Add(define.Name);
                    }

                    break;
                case "include":
                case "pragma":
                    break;
                default:
                    if (active)
                    {
                        throw Error(token, $"Unsupported preprocessor directive '#{directive.Name}'.");
                    }

                    break;
            }
        }

        return result;
    }

    private DirectiveParts ReadDirective(CToken token)
    {
        string normalized = RemoveLineSplices(token.Text);
        if (normalized.Length == 0 || normalized[0] != '#')
        {
            throw Error(token, "Malformed preprocessor directive.");
        }

        int index = 1;
        while (index < normalized.Length && IsDirectiveWhitespace(normalized[index]))
        {
            index++;
        }

        int nameStart = index;
        if (index >= normalized.Length || !IsIdentifierStart(normalized[index]))
        {
            throw Error(token, "Expected a preprocessor directive name.");
        }

        index++;
        while (index < normalized.Length && IsIdentifierPart(normalized[index]))
        {
            index++;
        }

        return new DirectiveParts(normalized[nameStart..index], normalized, index);
    }

    private static string RemoveLineSplices(string text)
    {
        var sb = new StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length && IsLineBreak(text[index + 1]))
            {
                char firstLineBreak = text[index + 1];
                index += 2;
                if (firstLineBreak == '\r' && index < text.Length && text[index] == '\n')
                {
                    index++;
                }

                continue;
            }

            sb.Append(text[index]);
            index++;
        }

        return sb.ToString();
    }

    private CDefine ParseDefine(DirectiveParts directive, CToken directiveToken)
    {
        List<CToken> tokens = TokenizeDirectiveArguments(directive, directiveToken);
        if (tokens[0].Kind != CTokenKind.Identifier)
        {
            throw Error(directiveToken, "Expected a macro name after #define.");
        }

        CToken name = tokens[0];
        if (tokens.Count > 1 && string.Equals(tokens[1].Text, "(", StringComparison.Ordinal) &&
            tokens[1].Line == name.Line && tokens[1].Column == name.Column + name.Text.Length)
        {
            throw Error(directiveToken, "Function-like #define macros are not supported.");
        }

        var body = new List<CToken>();
        for (int i = 1; i < tokens.Count && tokens[i].Kind != CTokenKind.EOF; i++)
        {
            body.Add(tokens[i]);
        }

        return new CDefine(directiveToken.Pos, name.Text, body);
    }

    private bool EvaluateIfCondition(DirectiveParts directive, CToken directiveToken, HashSet<string> definedMacros)
    {
        List<CToken> tokens = TokenizeDirectiveArguments(directive, directiveToken);
        int index = 0;
        bool result = ReadDefinedTerm(tokens, ref index, directiveToken, definedMacros);

        while (tokens[index].Kind != CTokenKind.EOF)
        {
            if (!string.Equals(tokens[index].Text, "||", StringComparison.Ordinal))
            {
                throw Error(directiveToken, "Only defined(NAME) terms joined by || are supported in #if conditions.");
            }

            index++;
            bool next = ReadDefinedTerm(tokens, ref index, directiveToken, definedMacros);
            result = result || next;
        }

        return result;
    }

    private bool ReadDefinedTerm(List<CToken> tokens, ref int index, CToken directiveToken, HashSet<string> definedMacros)
    {
        if (tokens[index].Kind != CTokenKind.Identifier ||
            !string.Equals(tokens[index].Text, "defined", StringComparison.Ordinal))
        {
            throw Error(directiveToken, "Only defined(NAME) terms joined by || are supported in #if conditions.");
        }

        index++;
        if (!string.Equals(tokens[index].Text, "(", StringComparison.Ordinal))
        {
            throw Error(directiveToken, "Expected '(' after defined in #if condition.");
        }

        index++;
        if (tokens[index].Kind != CTokenKind.Identifier)
        {
            throw Error(directiveToken, "Expected a macro name in defined(NAME) condition.");
        }

        string name = tokens[index].Text;
        index++;
        if (!string.Equals(tokens[index].Text, ")", StringComparison.Ordinal))
        {
            throw Error(directiveToken, "Expected ')' after macro name in defined(NAME) condition.");
        }

        index++;
        return definedMacros.Contains(name);
    }

    private bool EvaluateIfdefCondition(DirectiveParts directive, CToken directiveToken, HashSet<string> definedMacros)
    {
        List<CToken> tokens = TokenizeDirectiveArguments(directive, directiveToken);
        if (tokens[0].Kind != CTokenKind.Identifier || tokens[1].Kind != CTokenKind.EOF)
        {
            throw Error(directiveToken, "#ifdef requires exactly one macro name.");
        }

        return definedMacros.Contains(tokens[0].Text);
    }

    private void EnsureNoDirectiveArguments(DirectiveParts directive, CToken directiveToken)
    {
        List<CToken> tokens = TokenizeDirectiveArguments(directive, directiveToken);
        if (tokens[0].Kind != CTokenKind.EOF)
        {
            throw Error(directiveToken, $"Unexpected text after #{directive.Name}.");
        }
    }

    private List<CToken> TokenizeDirectiveArguments(DirectiveParts directive, CToken directiveToken)
    {
        string fragment = directive.Text[directive.ArgumentsOffset..];
        List<CToken> localTokens;
        try
        {
            localTokens = new CTokenizer(fragment, FileName).Tokenize();
        }
        catch (CSourceException exception)
        {
            var pos = new SourcePos(
                directiveToken.Line + exception.Pos.Line - 1,
                directiveToken.Column + directive.ArgumentsOffset + exception.Pos.Column - 1);
            throw new CSourceException(FileName, pos, exception.Text);
        }

        var tokens = new List<CToken>(localTokens.Count);
        foreach (CToken token in localTokens)
        {
            int line = directiveToken.Line + token.Line - 1;
            int column = directiveToken.Column + directive.ArgumentsOffset + token.Column - 1;
            tokens.Add(new CToken(token.Kind, token.Text, line, column));
        }

        return tokens;
    }

    private static bool IsDirectiveWhitespace(char c) => c is ' ' or '\t' or '\v' or '\f' or '\r' or '\n';

    private static bool IsLineBreak(char c) => c is '\r' or '\n';

    private static bool IsIdentifierStart(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || c is >= '0' and <= '9';

    private CEnumDeclaration ParseEnumDeclaration()
    {
        CToken start = ExpectIdentifier("enum");
        CToken name = ExpectIdentifier();
        ExpectText("{");
        var members = new List<CEnumMember>();

        while (!IsText("}"))
        {
            if (Current.Kind == CTokenKind.EOF)
            {
                throw Error(Current, "Unterminated enum declaration.");
            }

            CToken memberName = ExpectIdentifier();
            CExpr? value = null;
            if (IsText("="))
            {
                Consume();
                value = ParseExpression();
            }

            members.Add(new CEnumMember(memberName.Pos, memberName.Text, value));
            if (IsText(","))
            {
                Consume();
                continue;
            }

            if (!IsText("}"))
            {
                throw Error(Current, "Expected ',' or '}' after enum member.");
            }
        }

        ExpectText("}");
        ExpectText(";");
        return new CEnumDeclaration(start.Pos, name.Text, members);
    }

    private CVariable ParseVariableDeclaration()
    {
        CToken start = ExpectIdentifier("static");
        ExpectIdentifier("const");
        int typeStart = _index;
        int assignmentIndex = FindVariableAssignment();

        int firstDimension = -1;
        for (int i = typeStart; i < assignmentIndex; i++)
        {
            if (string.Equals(_tokens[i].Text, "[", StringComparison.Ordinal))
            {
                firstDimension = i;
                break;
            }
        }

        int nameIndex = firstDimension >= 0 ? firstDimension - 1 : assignmentIndex - 1;
        if (nameIndex <= typeStart || _tokens[nameIndex].Kind != CTokenKind.Identifier)
        {
            CToken errorToken = nameIndex >= typeStart && nameIndex < assignmentIndex ? _tokens[nameIndex] : Current;
            throw Error(errorToken, "Expected a variable name after its type.");
        }

        string elementType = JoinTokens(_tokens.GetRange(typeStart, nameIndex - typeStart));
        if (elementType.Length == 0)
        {
            throw Error(_tokens[nameIndex], "Expected a variable type.");
        }

        CToken name = _tokens[nameIndex];
        _index = nameIndex + 1;
        var dimensions = new List<CExpr?>();
        while (IsText("["))
        {
            Consume();
            if (IsText("]"))
            {
                dimensions.Add(null);
            }
            else
            {
                dimensions.Add(ParseExpression());
            }

            ExpectText("]");
        }

        if (_index != assignmentIndex)
        {
            throw Error(Current, "Unsupported tokens in variable declarator.");
        }

        ExpectText("=");
        CInitializer initializer = ParseInitializer();
        ExpectText(";");
        return new CVariable(start.Pos, elementType, name.Text, dimensions, initializer);
    }

    private int FindVariableAssignment()
    {
        int bracketDepth = 0;
        int parenthesisDepth = 0;
        for (int i = _index; i < _tokens.Count; i++)
        {
            CToken token = _tokens[i];
            if (token.Kind == CTokenKind.EOF)
            {
                break;
            }

            if (string.Equals(token.Text, "[", StringComparison.Ordinal))
            {
                bracketDepth++;
            }
            else if (string.Equals(token.Text, "]", StringComparison.Ordinal))
            {
                bracketDepth--;
            }
            else if (string.Equals(token.Text, "(", StringComparison.Ordinal))
            {
                parenthesisDepth++;
            }
            else if (string.Equals(token.Text, ")", StringComparison.Ordinal))
            {
                parenthesisDepth--;
            }
            else if (string.Equals(token.Text, "=", StringComparison.Ordinal) && bracketDepth == 0 && parenthesisDepth == 0)
            {
                return i;
            }
            else if (string.Equals(token.Text, ";", StringComparison.Ordinal) && bracketDepth == 0 && parenthesisDepth == 0)
            {
                throw Error(token, "Expected '=' and an initializer in static const declaration.");
            }
        }

        throw Error(Current, "Expected '=' and an initializer in static const declaration.");
    }

    private CFunction ParseFunctionDefinition()
    {
        CToken start = ExpectIdentifier("static");
        FunctionHeader header = ParseFunctionHeader();
        if (!string.Equals(header.ReturnType, "bool", StringComparison.Ordinal))
        {
            throw Error(start, "Only static bool function definitions are supported.");
        }

        List<CToken> body = ParseFunctionBody();
        return new CFunction(start.Pos, header.ReturnType, header.Name, header.Parameters, body);
    }

    private CPrototype ParsePrototype()
    {
        CToken start = Current;
        FunctionHeader header = ParseFunctionHeader();
        if (!IsText(";"))
        {
            throw Error(Current, "Expected ';' after function prototype.");
        }

        Consume();
        return new CPrototype(start.Pos, header.ReturnType, header.Name, header.Parameters);
    }

    private CLanguageFunction ParseLanguageFunction()
    {
        CToken start = ExpectIdentifier("TS_PUBLIC");
        FunctionHeader header = ParseFunctionHeader();
        if (!string.Equals(header.ReturnType, "const TSLanguage *", StringComparison.Ordinal) ||
            !string.Equals(header.Parameters, "void", StringComparison.Ordinal))
        {
            throw Error(start, "Unsupported TS_PUBLIC language function signature.");
        }

        ExpectText("{");
        CVariable language = ParseVariableDeclaration();
        if (!string.Equals(language.ElementType, "TSLanguage", StringComparison.Ordinal) ||
            !string.Equals(language.Name, "language", StringComparison.Ordinal) || language.Dimensions.Count != 0)
        {
            throw Error(start, "Expected a static const TSLanguage language object.");
        }

        ExpectIdentifier("return");
        ExpectText("&");
        CToken returnedName = ExpectIdentifier();
        if (!string.Equals(returnedName.Text, language.Name, StringComparison.Ordinal))
        {
            throw Error(returnedName, "Language function must return &language.");
        }

        ExpectText(";");
        ExpectText("}");
        return new CLanguageFunction(start.Pos, header.Name, language);
    }

    private FunctionHeader ParseFunctionHeader()
    {
        int returnTypeStart = _index;
        int openParenthesis = -1;
        for (int i = _index; i < _tokens.Count; i++)
        {
            CToken token = _tokens[i];
            if (token.Kind == CTokenKind.EOF || string.Equals(token.Text, ";", StringComparison.Ordinal) ||
                string.Equals(token.Text, "{", StringComparison.Ordinal) || string.Equals(token.Text, "=", StringComparison.Ordinal))
            {
                break;
            }

            if (string.Equals(token.Text, "(", StringComparison.Ordinal))
            {
                openParenthesis = i;
                break;
            }
        }

        if (openParenthesis <= returnTypeStart)
        {
            throw Error(Current, "Expected a function name and parameter list.");
        }

        int nameIndex = openParenthesis - 1;
        if (_tokens[nameIndex].Kind != CTokenKind.Identifier)
        {
            throw Error(_tokens[openParenthesis], "Expected a function name before parameter list.");
        }

        string returnType = JoinTokens(_tokens.GetRange(returnTypeStart, nameIndex - returnTypeStart));
        if (returnType.Length == 0)
        {
            throw Error(_tokens[nameIndex], "Expected a function return type.");
        }

        string name = _tokens[nameIndex].Text;
        _index = openParenthesis;
        ExpectText("(");
        var parameters = new List<CToken>();
        int depth = 1;
        while (depth > 0)
        {
            if (Current.Kind == CTokenKind.EOF)
            {
                throw Error(Current, "Unterminated function parameter list.");
            }

            if (IsText("("))
            {
                depth++;
                parameters.Add(Consume());
                continue;
            }

            if (IsText(")"))
            {
                depth--;
                if (depth == 0)
                {
                    Consume();
                    break;
                }

                parameters.Add(Consume());
                continue;
            }

            parameters.Add(Consume());
        }

        return new FunctionHeader(name, returnType, JoinTokens(parameters));
    }

    private List<CToken> ParseFunctionBody()
    {
        CToken openBrace = ExpectText("{");
        int bodyStart = _index;
        int depth = 1;

        while (depth > 0)
        {
            if (Current.Kind == CTokenKind.EOF)
            {
                throw Error(openBrace, "Unterminated function body.");
            }

            if (IsText("{"))
            {
                depth++;
                Consume();
                continue;
            }

            if (IsText("}"))
            {
                depth--;
                if (depth == 0)
                {
                    var body = _tokens.GetRange(bodyStart, _index - bodyStart);
                    Consume();
                    return body;
                }

                Consume();
                continue;
            }

            Consume();
        }

        throw Error(openBrace, "Unterminated function body.");
    }

    private bool LooksLikePrototype()
    {
        for (int i = _index; i < _tokens.Count; i++)
        {
            CToken token = _tokens[i];
            if (token.Kind == CTokenKind.EOF || string.Equals(token.Text, ";", StringComparison.Ordinal) ||
                string.Equals(token.Text, "{", StringComparison.Ordinal) || string.Equals(token.Text, "=", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(token.Text, "(", StringComparison.Ordinal))
            {
                return i > _index && _tokens[i - 1].Kind == CTokenKind.Identifier;
            }
        }

        return false;
    }

    private bool IsExternCBlock() =>
        IsIdentifier("extern") && Peek(1).Kind == CTokenKind.StringLiteral &&
        string.Equals(Peek(1).Text, "\"C\"", StringComparison.Ordinal) && IsText(Peek(2), "{");

    private CExpr ParseUnaryExpression()
    {
        if (IsText("-") || IsText("&"))
        {
            CToken op = Consume();
            CExpr operand = ParseUnaryExpression();
            return new CUnary(op.Pos, op.Text[0], operand);
        }

        if (TryGetCastType(out int closeParenthesis))
        {
            CToken open = Consume();
            var typeTokens = new List<CToken>();
            while (!IsText(")"))
            {
                typeTokens.Add(Consume());
            }

            ExpectText(")");
            if (_index != closeParenthesis + 1)
            {
                throw Error(open, "Malformed cast type.");
            }

            CExpr operand = ParseUnaryExpression();
            return new CCast(open.Pos, JoinTokens(typeTokens), operand);
        }

        return ParsePostfixExpression();
    }

    private bool TryGetCastType(out int closeParenthesis)
    {
        closeParenthesis = -1;
        if (!IsText("("))
        {
            return false;
        }

        int cursor = _index + 1;
        int identifierCount = 0;
        while (cursor < _tokens.Count)
        {
            CToken token = _tokens[cursor];
            if (token.Kind == CTokenKind.Identifier)
            {
                if (!TypeWords.Contains(token.Text))
                {
                    return false;
                }

                identifierCount++;
                cursor++;
                continue;
            }

            if (string.Equals(token.Text, "*", StringComparison.Ordinal))
            {
                cursor++;
                continue;
            }

            if (string.Equals(token.Text, ")", StringComparison.Ordinal))
            {
                break;
            }

            return false;
        }

        if (identifierCount == 0 || cursor >= _tokens.Count || cursor + 1 >= _tokens.Count)
        {
            return false;
        }

        if (!CanStartUnaryExpression(_tokens[cursor + 1]))
        {
            return false;
        }

        closeParenthesis = cursor;
        return true;
    }

    private static bool CanStartUnaryExpression(CToken token) =>
        token.Kind is CTokenKind.Identifier or CTokenKind.NumberLiteral or CTokenKind.StringLiteral or CTokenKind.CharLiteral ||
        string.Equals(token.Text, "(", StringComparison.Ordinal) ||
        string.Equals(token.Text, "-", StringComparison.Ordinal) ||
        string.Equals(token.Text, "&", StringComparison.Ordinal);

    private CExpr ParsePostfixExpression()
    {
        CExpr expression = ParsePrimaryExpression();
        while (IsText("["))
        {
            Consume();
            CExpr index = ParseExpression();
            ExpectText("]");
            expression = new CIndex(expression.Pos, expression, index);
        }

        return expression;
    }

    private CExpr ParsePrimaryExpression()
    {
        CToken token = Current;
        switch (token.Kind)
        {
            case CTokenKind.NumberLiteral:
                Consume();
                try
                {
                    return new CIntLiteral(token.Pos, CTokenizer.ParseInteger(token.Text), token.Text);
                }
                catch (FormatException)
                {
                    throw Error(token, $"Invalid integer literal '{token.Text}'.");
                }
                catch (OverflowException)
                {
                    throw Error(token, $"Invalid integer literal '{token.Text}'.");
                }
            case CTokenKind.CharLiteral:
                Consume();
                try
                {
                    return new CCharLiteral(token.Pos, CTokenizer.ParseCharLiteral(token.Text));
                }
                catch (FormatException)
                {
                    throw Error(token, $"Invalid character literal '{token.Text}'.");
                }
                catch (OverflowException)
                {
                    throw Error(token, $"Invalid character literal '{token.Text}'.");
                }
            case CTokenKind.StringLiteral:
                Consume();
                string value = CTokenizer.UnescapeString(token.Text);
                var bytes = new byte[value.Length];
                for (int i = 0; i < value.Length; i++)
                {
                    bytes[i] = unchecked((byte)value[i]);
                }

                return new CStringLiteral(token.Pos, bytes);
            case CTokenKind.Identifier:
                Consume();
                if (!IsText("("))
                {
                    return new CIdentifier(token.Pos, token.Text);
                }

                Consume();
                var arguments = new List<CExpr>();
                if (!IsText(")"))
                {
                    while (true)
                    {
                        arguments.Add(ParseExpression());
                        if (!IsText(","))
                        {
                            break;
                        }

                        Consume();
                        if (IsText(")"))
                        {
                            throw Error(Current, "Expected a macro argument after ','.");
                        }
                    }
                }

                ExpectText(")");
                return new CCall(token.Pos, token.Text, arguments);
            case CTokenKind.Punctuator when string.Equals(token.Text, "(", StringComparison.Ordinal):
                Consume();
                CExpr parenthesized = ParseExpression();
                ExpectText(")");
                return parenthesized;
            default:
                throw Error(token, $"Expected an expression, found '{token.Text}'.");
        }
    }

    private CInitializer ParseInitializerCore()
    {
        if (!IsText("{"))
        {
            CExpr expression = ParseExpression();
            return new CInitExpr(expression.Pos, expression);
        }

        CToken openBrace = Consume();
        var items = new List<CInitItem>();
        while (!IsText("}"))
        {
            if (Current.Kind == CTokenKind.EOF)
            {
                throw Error(Current, "Unterminated initializer list.");
            }

            CToken itemStart = Current;
            var designators = new List<CDesignator>();
            while (IsText("[") || IsText("."))
            {
                if (IsText("["))
                {
                    CToken openBracket = Consume();
                    CExpr index = ParseExpression();
                    ExpectText("]");
                    designators.Add(new CIndexDesignator(openBracket.Pos, index));
                    continue;
                }

                CToken dot = Consume();
                CToken field = ExpectIdentifier();
                designators.Add(new CFieldDesignator(dot.Pos, field.Text));
            }

            if (designators.Count > 0)
            {
                ExpectText("=");
            }

            CInitializer value = ParseInitializerCore();
            items.Add(new CInitItem(itemStart.Pos, designators, value));

            if (IsText(","))
            {
                Consume();
                continue;
            }

            if (!IsText("}"))
            {
                throw Error(Current, "Expected ',' or '}' after initializer item.");
            }
        }

        ExpectText("}");
        return new CInitList(openBrace.Pos, items);
    }

    private static string JoinTokens(IReadOnlyList<CToken> tokens) =>
        string.Join(" ", tokens.Select(static token => token.Text));

    private bool IsIdentifier(string text) =>
        Current.Kind == CTokenKind.Identifier && string.Equals(Current.Text, text, StringComparison.Ordinal);

    private bool IsText(string text) => IsText(Current, text);

    private static bool IsText(CToken token, string text) => string.Equals(token.Text, text, StringComparison.Ordinal);

    private CToken ExpectIdentifier() =>
        Current.Kind == CTokenKind.Identifier ? Consume() : throw Error(Current, "Expected an identifier.");

    private CToken ExpectIdentifier(string text) =>
        IsIdentifier(text) ? Consume() : throw Error(Current, $"Expected identifier '{text}'.");

    private CToken ExpectText(string text) =>
        IsText(text) ? Consume() : throw Error(Current, $"Expected '{text}'.");

    private CToken Consume()
    {
        CToken token = Current;
        if (token.Kind != CTokenKind.EOF)
        {
            _index++;
        }

        return token;
    }

    private CToken Current => _tokens[Math.Min(_index, _tokens.Count - 1)];

    private CToken Peek(int offset)
    {
        int index = _index + offset;
        return _tokens[Math.Min(index, _tokens.Count - 1)];
    }

    private CSourceException Error(CToken token, string message) => new(FileName, token.Pos, message);

    private readonly record struct DirectiveParts(string Name, string Text, int ArgumentsOffset);

    private sealed class ConditionalFrame
    {
        public ConditionalFrame(bool parentActive, bool condition, CToken directive)
        {
            ParentActive = parentActive;
            AnyBranchTaken = condition;
            CurrentActive = parentActive && condition;
            Directive = directive;
        }

        public bool ParentActive { get; }

        public bool AnyBranchTaken { get; set; }

        public bool CurrentActive { get; set; }

        public bool HasElse { get; set; }

        public CToken Directive { get; }
    }

    private readonly record struct FunctionHeader(string Name, string ReturnType, string Parameters);
}

/// <summary>Reads a parser.c file from disk with one char per byte.</summary>
public static class CSourceFile
{
    /// <summary>Reads the file as Latin-1 text.</summary>
    public static string Read(string path) => File.ReadAllText(path, System.Text.Encoding.Latin1);

    /// <summary>Tokenizes and parses a file.</summary>
    public static CTranslationUnit Parse(string path)
    {
        string text = Read(path);
        var tokens = new CTokenizer(text, Path.GetFileName(path)).Tokenize();
        return new CParser(tokens, Path.GetFileName(path)).ParseTranslationUnit();
    }
}
