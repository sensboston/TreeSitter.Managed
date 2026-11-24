// Reads the body tokens of a lex function (CFunction.Body from CParser) into a LexProgram.
// The accepted form is described in LexSyntax.cs. Operator precedence as in C:
// ! > comparisons > && > ||, left associative. Anything else is a CSourceException with
// the position of the token.

using TreeSitter.Generator.CSource;

namespace TreeSitter.Generator.Lex;

public static class LexBodyParser
{
    /// <summary>Parses the body of one lex function (ts_lex, ts_lex_keywords).</summary>
    public static LexProgram Parse(CFunction function, string fileName)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(fileName);
        return new BodyParser(function, fileName).ParseProgram();
    }

    private sealed class BodyParser
    {
        private readonly CFunction _function;
        private readonly string _fileName;
        private readonly IReadOnlyList<CToken> _body;
        private int _index;

        public BodyParser(CFunction function, string fileName)
        {
            _function = function;
            _fileName = fileName;
            _body = function.Body;
        }

        public LexProgram ParseProgram()
        {
            ExpectIdentifier("START_LEXER");
            ExpectText("(");
            ExpectText(")");
            ExpectText(";");
            ExpectIdentifier("eof");
            ExpectText("=");
            ExpectIdentifier("lexer");
            ExpectText("->");
            ExpectIdentifier("eof");
            ExpectText("(");
            ExpectIdentifier("lexer");
            ExpectText(")");
            ExpectText(";");
            ExpectIdentifier("switch");
            ExpectText("(");
            ExpectIdentifier("state");
            ExpectText(")");
            ExpectText("{");

            var states = new List<LexState>();
            while (true)
            {
                if (IsIdentifier("case"))
                {
                    states.Add(ParseState(states.Count));
                    continue;
                }

                if (IsIdentifier("default"))
                {
                    Consume();
                    ExpectText(":");
                    ExpectIdentifier("return");
                    ExpectIdentifier("false");
                    ExpectText(";");
                    ExpectText("}");
                    if (_index < _body.Count)
                    {
                        throw Error(Current, "Unexpected token after the switch.");
                    }

                    break;
                }

                throw Error(Current, "Expected 'case' or 'default' in the switch.");
            }

            return new LexProgram(_function.Name, states);
        }

        private LexState ParseState(int expectedNumber)
        {
            CToken caseToken = Consume();
            CToken numberToken = Current;
            int number = ReadNumber();
            if (number != expectedNumber)
            {
                throw Error(numberToken, $"Expected case {expectedNumber}, found case {number}.");
            }

            ExpectText(":");
            var statements = new List<LexStatement>();
            while (!IsIdentifier("END_STATE"))
            {
                statements.Add(ParseStatement());
            }

            ExpectIdentifier("END_STATE");
            ExpectText("(");
            ExpectText(")");
            ExpectText(";");
            return new LexState(caseToken.Pos, number, statements);
        }

        private LexStatement ParseStatement()
        {
            CToken token = Current;
            if (IsIdentifier("if"))
            {
                Consume();
                ExpectText("(");
                LexCondition condition = ParseOr();
                ExpectText(")");
                bool skip;
                if (IsIdentifier("ADVANCE"))
                {
                    skip = false;
                }
                else if (IsIdentifier("SKIP"))
                {
                    skip = true;
                }
                else
                {
                    throw Error(Current, "Expected 'ADVANCE' or 'SKIP' after the if condition.");
                }

                Consume();
                ExpectText("(");
                int target = ReadNumber();
                ExpectText(")");
                ExpectText(";");
                return new LexIf(token.Pos, condition, target, skip);
            }

            if (IsIdentifier("ADVANCE_MAP"))
            {
                Consume();
                ExpectText("(");
                var keys = new List<int>();
                var targets = new List<int>();
                while (!IsText(")"))
                {
                    keys.Add(ReadMapKey());
                    ExpectText(",");
                    targets.Add(ReadNumber());
                    if (IsText(","))
                    {
                        Consume();
                    }
                }

                ExpectText(")");
                ExpectText(";");
                return new LexAdvanceMap(token.Pos, keys, targets);
            }

            if (IsIdentifier("ACCEPT_TOKEN"))
            {
                Consume();
                ExpectText("(");
                CToken symbol = Current;
                if (symbol.Kind != CTokenKind.Identifier)
                {
                    throw Error(symbol, "Expected a symbol name identifier.");
                }

                Consume();
                ExpectText(")");
                ExpectText(";");
                return new LexAccept(token.Pos, symbol.Text);
            }

            throw Error(token, $"Unsupported lex statement starting with '{token.Text}'.");
        }

        private LexCondition ParseOr()
        {
            LexCondition left = ParseAnd();
            while (IsText("||"))
            {
                CToken op = Consume();
                LexCondition right = ParseAnd();
                left = new LexLogical(op.Pos, false, left, right);
            }

            return left;
        }

        private LexCondition ParseAnd()
        {
            LexCondition left = ParseCompare();
            while (IsText("&&"))
            {
                CToken op = Consume();
                LexCondition right = ParseCompare();
                left = new LexLogical(op.Pos, true, left, right);
            }

            return left;
        }

        private LexCondition ParseCompare()
        {
            LexCondition left = ParseUnary();
            if (IsCompareOperator(Current))
            {
                CToken op = Consume();
                LexCondition right = ParseUnary();
                return new LexCompare(op.Pos, op.Text, left, right);
            }

            return left;
        }

        private static bool IsCompareOperator(CToken token) =>
            token.Kind == CTokenKind.Punctuator &&
            (string.Equals(token.Text, "==", StringComparison.Ordinal) ||
             string.Equals(token.Text, "!=", StringComparison.Ordinal) ||
             string.Equals(token.Text, "<", StringComparison.Ordinal) ||
             string.Equals(token.Text, "<=", StringComparison.Ordinal) ||
             string.Equals(token.Text, ">", StringComparison.Ordinal) ||
             string.Equals(token.Text, ">=", StringComparison.Ordinal));

        private LexCondition ParseUnary()
        {
            if (IsText("!"))
            {
                CToken op = Consume();
                return new LexNot(op.Pos, ParseUnary());
            }

            return ParsePrimary();
        }

        private LexCondition ParsePrimary()
        {
            CToken token = Current;
            if (IsText("("))
            {
                Consume();
                LexCondition inner = ParseOr();
                ExpectText(")");
                return inner;
            }

            if (token.Kind == CTokenKind.Identifier)
            {
                if (string.Equals(token.Text, "lookahead", StringComparison.Ordinal))
                {
                    Consume();
                    return new LexLookahead(token.Pos);
                }

                if (string.Equals(token.Text, "eof", StringComparison.Ordinal))
                {
                    Consume();
                    return new LexEof(token.Pos);
                }

                if (string.Equals(token.Text, "set_contains", StringComparison.Ordinal))
                {
                    return ParseSetContains(token);
                }
            }

            if (token.Kind == CTokenKind.CharLiteral)
            {
                Consume();
                return new LexConstant(token.Pos, CTokenizer.ParseCharLiteral(token.Text));
            }

            if (token.Kind == CTokenKind.NumberLiteral)
            {
                Consume();
                return new LexConstant(token.Pos, ToInt(token));
            }

            throw Error(token, "Expected a condition.");
        }

        private LexSetContains ParseSetContains(CToken token)
        {
            Consume();
            ExpectText("(");
            CToken setName = Current;
            if (setName.Kind != CTokenKind.Identifier)
            {
                throw Error(setName, "Expected a set name identifier.");
            }

            Consume();
            ExpectText(",");
            int length = ReadNumber();
            ExpectText(",");
            if (!IsIdentifier("lookahead"))
            {
                throw Error(Current, "Expected 'lookahead'.");
            }

            Consume();
            ExpectText(")");
            return new LexSetContains(token.Pos, setName.Text, length);
        }

        private int ReadNumber()
        {
            CToken token = Current;
            if (token.Kind != CTokenKind.NumberLiteral)
            {
                throw Error(token, "Expected a number.");
            }

            Consume();
            return ToInt(token);
        }

        private int ReadMapKey()
        {
            CToken token = Current;
            if (token.Kind == CTokenKind.CharLiteral)
            {
                Consume();
                return CTokenizer.ParseCharLiteral(token.Text);
            }

            if (token.Kind == CTokenKind.NumberLiteral)
            {
                return ReadNumber();
            }

            throw Error(token, "Expected a character or number key.");
        }

        private int ToInt(CToken token)
        {
            long value;
            try
            {
                value = CTokenizer.ParseInteger(token.Text);
            }
            catch (FormatException)
            {
                throw Error(token, $"Invalid integer literal '{token.Text}'.");
            }
            catch (OverflowException)
            {
                throw Error(token, $"Invalid integer literal '{token.Text}'.");
            }

            if (value is < int.MinValue or > int.MaxValue)
            {
                throw Error(token, $"Integer literal '{token.Text}' is out of range.");
            }

            return (int)value;
        }

        private bool IsIdentifier(string text) =>
            Current.Kind == CTokenKind.Identifier &&
            string.Equals(Current.Text, text, StringComparison.Ordinal);

        private bool IsText(string text) => string.Equals(Current.Text, text, StringComparison.Ordinal);

        private void ExpectIdentifier(string text)
        {
            if (!IsIdentifier(text))
            {
                throw Error(Current, $"Expected identifier '{text}'.");
            }

            Consume();
        }

        private void ExpectText(string text)
        {
            if (!IsText(text))
            {
                throw Error(Current, $"Expected '{text}'.");
            }

            Consume();
        }

        private CToken Consume()
        {
            CToken token = Current;
            if (token.Kind != CTokenKind.EOF)
            {
                _index++;
            }

            return token;
        }

        private CToken Current => _body.Count > 0
            ? _body[Math.Min(_index, _body.Count - 1)]
            : FallbackToken;

        private CToken FallbackToken =>
            new(CTokenKind.EOF, string.Empty, _function.Pos.Line, _function.Pos.Column);

        private CSourceException Error(CToken token, string message) => new(_fileName, token.Pos, message);
    }
}
