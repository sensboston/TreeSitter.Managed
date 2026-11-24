// Port of src/scanner.c of tree-sitter-c-sharp (commit e3971fcb53bf5573a7e0d4ff6db46d2a77a4bf71,
// grammars/CSharp/upstream/src/scanner.c), the external scanner of the C# grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - Interpolation is a class: the C pointers into interpolation_stack (current_interpolation)
//   are references to the same objects. Array(Interpolation) is List<Interpolation>.
// - uint8_t fields and locals are byte; the wrap at 256 of C is the wrap of unchecked byte.
// - The C local brace_advanced of scan() is braceAdvanced; the one of the CLOSE_BRACE block
//   (a second variable with the same name in C) is closeBraceAdvanced.
// - BAIL of scan_lambda_paren_open is the return it expands to.
// - iswspace/iswalpha/iswalnum of the C locale are the ASCII ones (as in Rust/Html).
// - The serialized state is the same bytes as the C scanner writes.

using System.Diagnostics;

namespace TreeSitter.Grammars.CSharp;

/// <summary>The external scanner of the C# grammar (interpolated and raw strings, lambdas, preprocessor).</summary>
internal sealed class CSharpScanner : IExternalScanner
{
    // enum TokenType
    private const int OptSemi = 0;
    private const int InterpolationRegularStart = 1;
    private const int InterpolationVerbatimStart = 2;
    private const int InterpolationRawStart = 3;
    private const int InterpolationStartQuote = 4;
    private const int InterpolationEndQuote = 5;
    private const int InterpolationOpenBrace = 6;
    private const int InterpolationCloseBrace = 7;
    private const int InterpolationStringContent = 8;
    private const int RawStringStart = 9;
    private const int RawStringEnd = 10;
    private const int RawStringContent = 11;
    private const int LambdaParenOpen = 12;
    private const int PreprocElseIfStart = 13;
    private const int ConditionalMemberQuestion = 14;

    // StringType
    private const byte Regular = 1 << 0;
    private const byte Verbatim = 1 << 1;
    private const byte Raw = 1 << 2;

    // TREE_SITTER_SERIALIZATION_BUFFER_SIZE
    private const int SerializationBufferSize = 1024;

    // "else" of scan_preproc_else_if_start
    private const string ElseKeyword = "else";

    private byte _quoteCount;
    private readonly List<Interpolation> _interpolationStack = new List<Interpolation>();

    // Outcome of `scan_lambda_paren_open` — distinguishes "didn't start"
    // (lexer untouched, wrapper can fall through to other token handlers)
    // from "started and failed" (lexer cursor advanced past characters
    // the wrapper must not let other handlers consume — they would emit
    // tokens with wrong spans, e.g. an `INTERPOLATION_REGULAR_START`
    // span covering `(false, $` in `return (false, $"...");`).
    private enum LambdaScanResult
    {
        NoParen,            // not a '(', no advance() done
        FailedAfterParen,   // '(' was consumed, dirty state
        Success,
    }

    // Interpolation: the state of one open interpolated string (see the C struct).
    private sealed class Interpolation
    {
        public byte DollarCount { get; set; }

        public byte OpenBraceCount { get; set; }

        public byte QuoteCount { get; set; }

        public byte StringType { get; set; }
    }

    private static bool IsRegular(Interpolation interpolation) => (interpolation.StringType & Regular) != 0;

    private static bool IsVerbatim(Interpolation interpolation) => (interpolation.StringType & Verbatim) != 0;

    private static bool IsRaw(Interpolation interpolation) => (interpolation.StringType & Raw) != 0;

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    // iswspace of the C locale.
    private static bool IsWSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // iswalpha of the C locale.
    private static bool IsWAlpha(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    // iswalnum of the C locale.
    private static bool IsWAlNum(int c) => IsWAlpha(c) || (c >= '0' && c <= '9');

    // ---- Helpers for LAMBDA_PAREN_OPEN scanning ---------------------------

    private static bool IsIdStart(int c) => c == '_' || IsWAlpha(c);

    private static bool IsIdContinue(int c) => c == '_' || IsWAlNum(c);

    // Skip whitespace, line comments (//...), and block comments (/* ... */).
    // Does NOT skip preprocessor directives — simple-lambda parameter lists
    // cannot legally contain them.
    private static void SkipWsAndComments(Lexer lexer)
    {
        for (;;)
        {
            int c = lexer.Lookahead;
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                Advance(lexer);
            }
            else if (c == '/')
            {
                Advance(lexer);
                if (lexer.Lookahead == '/')
                {
                    while (lexer.Lookahead != 0 && lexer.Lookahead != '\n')
                    {
                        Advance(lexer);
                    }
                }
                else if (lexer.Lookahead == '*')
                {
                    Advance(lexer);
                    int prev = 0;
                    while (lexer.Lookahead != 0 && !(prev == '*' && lexer.Lookahead == '/'))
                    {
                        prev = lexer.Lookahead;
                        Advance(lexer);
                    }

                    if (lexer.Lookahead == '/')
                    {
                        Advance(lexer);
                    }
                }
                else
                {
                    // a stray '/' isn't valid in a param list; bail out by
                    // making the caller see something unexpected
                    return;
                }
            }
            else
            {
                return;
            }
        }
    }

    // Consume an identifier into a fixed-size buffer. Returns the actual
    // length consumed (may exceed `max`, in which case `buf` is only filled
    // up to `max` bytes — useful for "too long to be a keyword" detection).
    private static int ConsumeIdentifierInto(Lexer lexer, Span<char> buf)
    {
        int n = 0;
        while (IsIdContinue(lexer.Lookahead))
        {
            if (n < buf.Length)
            {
                buf[n] = (char)lexer.Lookahead;
            }

            n++;
            Advance(lexer);
        }

        return n;
    }

    // True iff the first `n` bytes of `buf` equal the C string `kw`.
    private static bool BufEquals(ReadOnlySpan<char> buf, int n, string kw)
    {
        if (n != kw.Length)
        {
            return false;
        }

        for (int i = 0; i < kw.Length; i++)
        {
            if (buf[i] != kw[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>tree_sitter_c_sharp_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        if (_interpolationStack.Count * 4 + 2 > SerializationBufferSize)
        {
            return 0;
        }

        int size = 0;

        buffer[size++] = _quoteCount;
        buffer[size++] = (byte)_interpolationStack.Count;

        for (int i = 0; i < _interpolationStack.Count; i++)
        {
            Interpolation interpolation = _interpolationStack[i];
            buffer[size++] = interpolation.DollarCount;
            buffer[size++] = interpolation.OpenBraceCount;
            buffer[size++] = interpolation.QuoteCount;
            buffer[size++] = interpolation.StringType;
        }

        return (uint)size;
    }

    /// <summary>tree_sitter_c_sharp_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        _quoteCount = 0;
        _interpolationStack.Clear();
        int size = 0;

        if (buffer.Length > 0)
        {
            _quoteCount = buffer[size++];
            int count = buffer[size++];

            for (int i = 0; i < count; i++)
            {
                Interpolation interpolation = new Interpolation();
                interpolation.DollarCount = buffer[size++];
                interpolation.OpenBraceCount = buffer[size++];
                interpolation.QuoteCount = buffer[size++];
                interpolation.StringType = buffer[size++];
                _interpolationStack.Add(interpolation);
            }
        }

        Debug.Assert(size == buffer.Length);
    }

    // Try to recognize a C# 14 simple-lambda parameter list at the current
    // position. On SUCCESS, `lexer->result_symbol` is LAMBDA_PAREN_OPEN
    // and `mark_end` is set on the opening '('. On NO_PAREN, the lexer is
    // untouched (only `skip(lexer)` over leading whitespace, which doesn't
    // participate in token boundaries). On FAILED_AFTER_PAREN, the lexer
    // has advanced past at least the opening '(' and the caller must
    // surface this as a false return from the external_scanner_scan
    // function so tree-sitter rewinds.
    //
    // Grammar of the pattern (after the opening '('):
    //
    //   element (',' element)* ')' '=>'
    //
    //   element  := modifier+ identifier
    //             | identifier
    //
    //   modifier ∈ { scoped, ref, out, in, readonly }
    //
    // At least one element must carry a modifier; otherwise we'd over-fire
    // on plain `(x, y) => ...` which is already handled by the existing
    // `_lambda_parameters` choice.
    //
    // Implementation note: identifier-tokens are consumed *whole* into a
    // small buffer before being classified as modifier-or-name. A
    // character-by-character probe against each candidate keyword would be
    // unworkable because `ref` is a prefix of `readonly` (they share `re`)
    // and TSLexer has no rewind primitive: any probe order leaves one of
    // the two keywords unreachable. Buffering the whole identifier
    // sidesteps the prefix conflict entirely.
    private static LambdaScanResult ScanLambdaParenOpen(Lexer lexer)
    {
        // External scanners run before whitespace `extras` are skipped, so
        // we need to skip leading whitespace/comments ourselves before
        // checking for the opening '('. `skip(lexer)` consumes the char as
        // an extra (not part of any token), so even on NO_PAREN return the
        // wrapper's fall-through into other handlers is safe — those
        // handlers do their own whitespace skipping.
        while (IsWSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        if (lexer.Lookahead != '(')
        {
            return LambdaScanResult.NoParen;
        }

        Advance(lexer);
        lexer.MarkEnd();

        // From this point on, the lexer cursor has moved past '('. Any
        // return must be FAILED_AFTER_PAREN unless we reach SUCCESS.

        // We require at least one "hard" modifier (ref/out/in/readonly) across
        // the whole list before committing. `scoped` alone is not a valid C#
        // parameter modifier — it must always combine with a hard modifier —
        // and `scoped` is also a legal type name, so accepting `(scoped x) =>`
        // as a modifier+name pair would collide with the existing
        // `parameter_list` path on input that semantically means
        // type+identifier.
        bool sawHardModifier = false;
        bool expectingElement = true;

        // The identifier buffer of the element ("readonly" is 8 chars).
        Span<char> buf = stackalloc char[9];

        for (;;)
        {
            SkipWsAndComments(lexer);
            int c = lexer.Lookahead;

            if (c == 0)
            {
                // EOF mid-list
                return LambdaScanResult.FailedAfterParen;
            }

            if (c == ')')
            {
                Advance(lexer);
                SkipWsAndComments(lexer);
                if (!sawHardModifier)
                {
                    return LambdaScanResult.FailedAfterParen;
                }

                if (lexer.Lookahead != '=')
                {
                    return LambdaScanResult.FailedAfterParen;
                }

                Advance(lexer);
                if (lexer.Lookahead != '>')
                {
                    return LambdaScanResult.FailedAfterParen;
                }

                lexer.ResultSymbol = LambdaParenOpen;
                return LambdaScanResult.Success;
            }

            if (!expectingElement)
            {
                if (c != ',')
                {
                    return LambdaScanResult.FailedAfterParen;
                }

                Advance(lexer);
                expectingElement = true;
                continue;
            }

            // Consume identifier-tokens in this element. Each one is either a
            // parameter modifier (`scoped`/`ref`/`out`/`in`/`readonly`) — in which
            // case we continue looking for more — or the parameter name, which
            // ends the element. Consuming the whole token before classifying
            // avoids any need to rewind the lexer on a prefix-conflict (e.g. ref
            // vs readonly).
            bool consumedName = false;
            while (!consumedName)
            {
                SkipWsAndComments(lexer);
                if (!IsIdStart(lexer.Lookahead))
                {
                    return LambdaScanResult.FailedAfterParen;
                }

                int n = ConsumeIdentifierInto(lexer, buf);

                bool isHardModifier = n <= 8 && (
                    BufEquals(buf, n, "ref") ||
                    BufEquals(buf, n, "out") ||
                    BufEquals(buf, n, "in") ||
                    BufEquals(buf, n, "readonly")
                );
                bool isSoftModifier = n == 6 && BufEquals(buf, n, "scoped");

                if (isHardModifier)
                {
                    sawHardModifier = true;
                }
                else if (!isSoftModifier)
                {
                    // `scoped` is a modifier only when followed by a hard
                    // modifier; keep scanning. If the element turns out to
                    // end with `scoped <identifier>` and no hard modifier
                    // appeared, the final `saw_hard_modifier` check fails
                    // and we fall back to the regular parameter_list path.
                    consumedName = true;
                }
            }

            expectingElement = false;
        }
    }

    // `#if <condition>` whose first content, after the condition line and any blank
    // lines or comments, is the keyword `else`. Only `#if` is consumed (mark_end);
    // the rest is read ahead and handed back so the normal tokens lex it. Lets an
    // `else` branch live inside a directive without a parser conflict on `#if`.
    private static bool ScanPreprocElseIfStart(Lexer lexer)
    {
        while (IsWSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        if (lexer.Lookahead != '#')
        {
            return false;
        }

        Advance(lexer);
        while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
        {
            Advance(lexer);
        }

        if (lexer.Lookahead != 'i')
        {
            return false;
        }

        Advance(lexer);
        if (lexer.Lookahead != 'f')
        {
            return false;
        }

        Advance(lexer);
        if (IsIdContinue(lexer.Lookahead))
        {
            return false;
        }

        lexer.MarkEnd();

        // rest of the condition line
        while (lexer.Lookahead != 0 && lexer.Lookahead != '\n')
        {
            Advance(lexer);
        }

        SkipWsAndComments(lexer);
        for (int i = 0; i < ElseKeyword.Length; i++)
        {
            if (lexer.Lookahead != ElseKeyword[i])
            {
                return false;
            }

            Advance(lexer);
        }

        if (IsIdContinue(lexer.Lookahead))
        {
            return false;
        }

        lexer.ResultSymbol = PreprocElseIfStart;
        return true;
    }

    // Match the grammar extras independently of the process locale.
    private static bool IsMemberAccessSpace(int c) =>
        (c >= '\t' && c <= '\r') || c == ' ' || c == 0xA0 || c == 0x3000 || c == 0xFEFF;

    private static bool SkipMemberAccessTrivia(Lexer lexer)
    {
        for (;;)
        {
            while (IsMemberAccessSpace(lexer.Lookahead))
            {
                Advance(lexer);
            }

            if (lexer.Lookahead != '/')
            {
                return true;
            }

            Advance(lexer);
            if (lexer.Lookahead == '/')
            {
                while (lexer.Lookahead != 0 && lexer.Lookahead != '\n' && lexer.Lookahead != '\r')
                {
                    Advance(lexer);
                }
            }
            else if (lexer.Lookahead == '*')
            {
                Advance(lexer);
                for (;;)
                {
                    if (lexer.Lookahead == 0)
                    {
                        return false;
                    }

                    int c = lexer.Lookahead;
                    Advance(lexer);
                    if (c == '*' && lexer.Lookahead == '/')
                    {
                        Advance(lexer);
                        break;
                    }
                }
            }
            else
            {
                return false;
            }
        }
    }

    /// <summary>tree_sitter_c_sharp_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        byte braceAdvanced = 0;
        byte quoteCount = 0;
        bool didAdvance = false;

        // Lambda-paren scanning advances forward speculatively past the
        // opening `(`. If it consumes input and then bails, the lexer
        // cursor is mispositioned and must not be reused by other token
        // handlers below — they would emit tokens with spans starting at
        // the original scan position but ending at the dirty cursor (e.g.
        // an INTERPOLATION_REGULAR_START swallowing `(false, $` in
        // `return (false, $"...");`). When the scanner consumed `(` but
        // didn't confirm a lambda, return false here so tree-sitter
        // rewinds and lets the built-in `(` tokenizer match.
        if (validSymbols[LambdaParenOpen])
        {
            switch (ScanLambdaParenOpen(lexer))
            {
                case LambdaScanResult.Success:
                    return true;
                case LambdaScanResult.FailedAfterParen:
                    return false;
                case LambdaScanResult.NoParen:
                    break;  // lexer untouched; fall through
            }
        }

        if (validSymbols[PreprocElseIfStart])
        {
            if (ScanPreprocElseIfStart(lexer))
            {
                return true;
            }

            return false;
        }

        // error recovery, gives better trees this way
        if (validSymbols[OptSemi] && validSymbols[InterpolationRegularStart])
        {
            return false;
        }

        if (validSymbols[ConditionalMemberQuestion])
        {
            while (IsMemberAccessSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '?')
            {
                Advance(lexer);
                lexer.MarkEnd();
                if (!SkipMemberAccessTrivia(lexer))
                {
                    return false;
                }

                if (lexer.Lookahead == '.')
                {
                    Advance(lexer);
                    if (lexer.Lookahead < '0' || lexer.Lookahead > '9')
                    {
                        lexer.ResultSymbol = ConditionalMemberQuestion;
                        return true;
                    }
                }

                return false;
            }
        }

        if (validSymbols[OptSemi])
        {
            lexer.ResultSymbol = OptSemi;
            if (lexer.Lookahead == ';')
            {
                Advance(lexer);
            }

            return true;
        }

        if (validSymbols[RawStringStart])
        {
            while (IsWSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '"')
            {
                while (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    quoteCount++;
                }

                if (quoteCount >= 3)
                {
                    lexer.ResultSymbol = RawStringStart;
                    _quoteCount = quoteCount;
                    return true;
                }
            }
        }

        if (validSymbols[RawStringEnd] && lexer.Lookahead == '"')
        {
            while (lexer.Lookahead == '"')
            {
                Advance(lexer);
                quoteCount++;
            }

            if (quoteCount == _quoteCount)
            {
                lexer.ResultSymbol = RawStringEnd;
                _quoteCount = 0;
                return true;
            }

            didAdvance = quoteCount > 0;
        }

        if (validSymbols[RawStringContent])
        {
            while (lexer.Lookahead != 0)
            {
                if (lexer.Lookahead == '"')
                {
                    lexer.MarkEnd();
                    quoteCount = 0;

                    while (lexer.Lookahead == '"')
                    {
                        Advance(lexer);
                        quoteCount++;
                    }

                    if (quoteCount == _quoteCount)
                    {
                        lexer.ResultSymbol = RawStringContent;
                        return true;
                    }
                }

                Advance(lexer);
                didAdvance = true;
            }

            lexer.MarkEnd();
            lexer.ResultSymbol = RawStringContent;
            return true;
        }

        if (validSymbols[InterpolationRegularStart] || validSymbols[InterpolationVerbatimStart] ||
            validSymbols[InterpolationRawStart])
        {
            while (IsWSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            byte dollarAdvanced = 0;

            bool isVerbatim = false;

            if (lexer.Lookahead == '@')
            {
                isVerbatim = true;
                Advance(lexer);
            }

            while (lexer.Lookahead == '$' && quoteCount == 0)
            {
                Advance(lexer);
                dollarAdvanced++;
            }

            if (dollarAdvanced > 0 && (lexer.Lookahead == '"' || lexer.Lookahead == '@'))
            {
                lexer.ResultSymbol = InterpolationRegularStart;
                Interpolation interpolation = new Interpolation
                {
                    DollarCount = dollarAdvanced,
                    OpenBraceCount = 0,
                    QuoteCount = 0,
                    StringType = 0,
                };

                if (isVerbatim || lexer.Lookahead == '@')
                {
                    if (lexer.Lookahead == '@')
                    {
                        Advance(lexer);
                        isVerbatim = true;
                    }

                    lexer.ResultSymbol = InterpolationVerbatimStart;
                    interpolation.StringType = Verbatim;
                }

                lexer.MarkEnd();
                Advance(lexer);

                if (lexer.Lookahead == '"' && !isVerbatim)
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '"')
                    {
                        lexer.ResultSymbol = InterpolationRawStart;
                        interpolation.StringType |= Raw;
                        _interpolationStack.Add(interpolation);
                    }

                    // If we find 1 or 3 quotes, we push an interpolation.
                    // If there's only two quotes, that's just an empty string
                }
                else
                {
                    interpolation.StringType |= Regular;
                    _interpolationStack.Add(interpolation);
                }

                return true;
            }
        }

        if (validSymbols[InterpolationStartQuote] && _interpolationStack.Count > 0)
        {
            Interpolation currentInterpolation = _interpolationStack[^1];

            if (IsVerbatim(currentInterpolation) || IsRegular(currentInterpolation))
            {
                if (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    currentInterpolation.QuoteCount++;
                }
            }
            else
            {
                while (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    currentInterpolation.QuoteCount++;
                }
            }

            lexer.ResultSymbol = InterpolationStartQuote;
            return currentInterpolation.QuoteCount > 0;
        }

        if (validSymbols[InterpolationEndQuote] && _interpolationStack.Count > 0)
        {
            Interpolation currentInterpolation = _interpolationStack[^1];

            if (IsRaw(currentInterpolation))
            {
                while (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    quoteCount++;
                }

                if (quoteCount == currentInterpolation.QuoteCount)
                {
                    lexer.ResultSymbol = InterpolationEndQuote;
                    _interpolationStack.RemoveAt(_interpolationStack.Count - 1);
                    return true;
                }

                didAdvance = quoteCount > 0;
            }
            else if (lexer.Lookahead == '"')
            {
                // A regular or verbatim string ends at ONE quote. Counting the whole run of quotes
                // (as a raw string must) misread a verbatim `""` escape that follows a hole or the
                // start quote: in `$@"a ""{t}"""` the `"""` after `{t}` counted three, matched
                // nothing, and the content then ran past the real end of the string.
                Advance(lexer);
                if (!IsVerbatim(currentInterpolation) || lexer.Lookahead != '"')
                {
                    lexer.ResultSymbol = InterpolationEndQuote;
                    _interpolationStack.RemoveAt(_interpolationStack.Count - 1);
                    return true;
                }

                // `""` in a verbatim string is an escaped quote: both quotes are string content.
                Advance(lexer);
                didAdvance = true;
            }
        }

        // A brace token never starts after quotes taken above: those quotes are string content, and a
        // brace token here would span them (`$@"{a}""{b}"` read `""{` as the hole's brace).
        if (validSymbols[InterpolationOpenBrace] && _interpolationStack.Count > 0 && !didAdvance)
        {
            Interpolation currentInterpolation = _interpolationStack[^1];

            while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.DollarCount)
            {
                Advance(lexer);
                braceAdvanced++;
            }

            if (braceAdvanced > 0 && braceAdvanced == currentInterpolation.DollarCount &&
                (braceAdvanced == 0 || lexer.Lookahead != '{'))
            {
                currentInterpolation.OpenBraceCount = braceAdvanced;
                lexer.ResultSymbol = InterpolationOpenBrace;
                return true;
            }
        }

        if (validSymbols[InterpolationCloseBrace] && _interpolationStack.Count > 0 && !didAdvance)
        {
            byte closeBraceAdvanced = 0;
            Interpolation currentInterpolation = _interpolationStack[^1];

            while (IsWSpace(lexer.Lookahead))
            {
                Advance(lexer);
            }

            while (lexer.Lookahead == '}')
            {
                Advance(lexer);
                closeBraceAdvanced++;

                if (closeBraceAdvanced == currentInterpolation.OpenBraceCount)
                {
                    currentInterpolation.OpenBraceCount = 0;
                    lexer.ResultSymbol = InterpolationCloseBrace;
                    return true;
                }
            }

            return false;
        }

        if (validSymbols[InterpolationStringContent] && _interpolationStack.Count > 0)
        {
            lexer.ResultSymbol = InterpolationStringContent;
            Interpolation currentInterpolation = _interpolationStack[^1];

            while (lexer.Lookahead != 0)
            {
                // top-down approach, first see if it's raw
                if (IsRaw(currentInterpolation))
                {
                    if (lexer.Lookahead == '"')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == '"')
                        {
                            Advance(lexer);
                            byte quoteAdvanced = 2;
                            while (lexer.Lookahead == '"')
                            {
                                quoteAdvanced++;
                                Advance(lexer);
                            }

                            if (quoteAdvanced == currentInterpolation.QuoteCount)
                            {
                                return didAdvance;
                            }
                        }
                    }

                    if (lexer.Lookahead == '{')
                    {
                        lexer.MarkEnd();

                        while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.OpenBraceCount)
                        {
                            Advance(lexer);
                            braceAdvanced++;
                        }

                        if (braceAdvanced == currentInterpolation.OpenBraceCount &&
                            (braceAdvanced == 0 || lexer.Lookahead != '{'))
                        {
                            return didAdvance;
                        }
                    }
                }

                // then verbatim, since it could be verbatim + raw, but run the raw branch first
                else if (IsVerbatim(currentInterpolation))
                {
                    if (lexer.Lookahead == '"')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == '"')
                        {
                            Advance(lexer);
                            continue;
                        }

                        return didAdvance;
                    }

                    if (lexer.Lookahead == '{')
                    {
                        lexer.MarkEnd();

                        while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.OpenBraceCount)
                        {
                            Advance(lexer);
                            braceAdvanced++;
                        }

                        if (braceAdvanced == currentInterpolation.OpenBraceCount &&
                            (braceAdvanced == 0 || lexer.Lookahead != '{'))
                        {
                            return didAdvance;
                        }

                        // `{{` is an escaped brace, and both braces are content. Take the second one
                        // here and start counting again, so a `{` straight after the escape can still
                        // open a hole: in `$"{{{x}}}"` the third `{` starts `{x}`. Without the reset
                        // `brace_advanced` stays at the limit, the third `{` is read as content too,
                        // and the hole's expression becomes string text.
                        if (lexer.Lookahead == '{')
                        {
                            Advance(lexer);
                            braceAdvanced = 0;
                            didAdvance = true;
                            continue;
                        }
                    }
                }

                // finally regular
                else if (IsRegular(currentInterpolation))
                {
                    if (lexer.Lookahead == '\\' || lexer.Lookahead == '\n' || lexer.Lookahead == '"')
                    {
                        lexer.MarkEnd();
                        return didAdvance;
                    }

                    if (lexer.Lookahead == '{')
                    {
                        lexer.MarkEnd();

                        while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.OpenBraceCount)
                        {
                            Advance(lexer);
                            braceAdvanced++;
                        }

                        // if we're in a brace we're not allowed to
                        // collect more than the open_brace_count
                        if (braceAdvanced == currentInterpolation.OpenBraceCount &&
                            (braceAdvanced == 0 || lexer.Lookahead != '{'))
                        {
                            return didAdvance;
                        }

                        // An escaped `{{`: see the verbatim branch above.
                        if (lexer.Lookahead == '{')
                        {
                            Advance(lexer);
                            braceAdvanced = 0;
                            didAdvance = true;
                            continue;
                        }
                    }
                }

                if (lexer.Lookahead != '{')
                {
                    braceAdvanced = 0;
                }

                Advance(lexer);
                didAdvance = true;
            }

            lexer.MarkEnd();
            return didAdvance;
        }

        return false;
    }
}
