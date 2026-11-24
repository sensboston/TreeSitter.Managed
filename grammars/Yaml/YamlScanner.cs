// Port of src/scanner.c of tree-sitter-yaml (commit a1c4812a73ec5e089de8e441fdea3a921e8d5079,
// grammars/Yaml/upstream/src/scanner.c), the external scanner of the YAML grammar. The C file includes
// schema.core.c (YAML_SCHEMA core, the default): it is ported in SchemaCore.cs.
// - Scanner is this class: its fields are the C struct Scanner, and the functions taking
//   Scanner *scanner are instance methods. create is the constructor, destroy is not needed.
// - The macros are methods or inlined code: RET_SYM(X) is `return RetSym(lexer, X)` (RetSym flushes
//   the position and sets the result symbol); POP_IND, PUSH_BGN_IND and MAY_PUSH_IMP_IND return false
//   from the caller in C and are TryPopInd / PushBgnInd / MayPushImpInd here.
// - int16_t fields are short; arithmetic on them is cast back to short as in C.
// - The serialized state is the same bytes as the C scanner writes (little-endian int16 values).
// - SGL_PLN_SYM(POS, CTX) is SglPlnSym: the schema of the scalar selects the symbol. HAS_TIMESTAMP is 0,
//   so the timestamp symbols are not used.
// - The C function pointer is_plain_safe is the bool inFlow: false is is_plain_safe_in_block,
//   true is is_plain_safe_in_flow.
// - ASCII character classes as in the C source (the scanner uses no wctype functions).

namespace TreeSitter.Grammars.Yaml;

/// <summary>The external scanner of the YAML grammar (indentation, scalars, tags and anchors).</summary>
internal sealed class YamlScanner : IExternalScanner
{
    // enum TokenType
    private const int EndOfFile = 0;
    private const int SDirYmlBgn = 1;
    private const int RDirYmlVer = 2;
    private const int SDirTagBgn = 3;
    private const int RDirTagHdl = 4;
    private const int RDirTagPfx = 5;
    private const int SDirRsvBgn = 6;
    private const int RDirRsvPrm = 7;
    private const int SDrsEnd = 8;
    private const int SDocEnd = 9;
    private const int RBlkSeqBgn = 10;
    private const int BrBlkSeqBgn = 11;
    private const int BBlkSeqBgn = 12;
    private const int RBlkKeyBgn = 13;
    private const int BrBlkKeyBgn = 14;
    private const int BBlkKeyBgn = 15;
    private const int RBlkValBgn = 16;
    private const int BrBlkValBgn = 17;
    private const int BBlkValBgn = 18;
    private const int RBlkImpBgn = 19;
    private const int RBlkLitBgn = 20;
    private const int BrBlkLitBgn = 21;
    private const int RBlkFldBgn = 22;
    private const int BrBlkFldBgn = 23;
    private const int BrBlkStrCtn = 24;
    private const int RFlwSeqBgn = 25;
    private const int BrFlwSeqBgn = 26;
    private const int BFlwSeqBgn = 27;
    private const int RFlwSeqEnd = 28;
    private const int BrFlwSeqEnd = 29;
    private const int BFlwSeqEnd = 30;
    private const int RFlwMapBgn = 31;
    private const int BrFlwMapBgn = 32;
    private const int BFlwMapBgn = 33;
    private const int RFlwMapEnd = 34;
    private const int BrFlwMapEnd = 35;
    private const int BFlwMapEnd = 36;
    private const int RFlwSepBgn = 37;
    private const int BrFlwSepBgn = 38;
    private const int RFlwKeyBgn = 39;
    private const int BrFlwKeyBgn = 40;
    private const int RFlwJsvBgn = 41;
    private const int BrFlwJsvBgn = 42;
    private const int RFlwNjvBgn = 43;
    private const int BrFlwNjvBgn = 44;
    private const int RDqtStrBgn = 45;
    private const int BrDqtStrBgn = 46;
    private const int BDqtStrBgn = 47;
    private const int RDqtStrCtn = 48;
    private const int BrDqtStrCtn = 49;
    private const int RDqtEscNwl = 50;
    private const int BrDqtEscNwl = 51;
    private const int RDqtEscSeq = 52;
    private const int BrDqtEscSeq = 53;
    private const int RDqtStrEnd = 54;
    private const int BrDqtStrEnd = 55;
    private const int RSqtStrBgn = 56;
    private const int BrSqtStrBgn = 57;
    private const int BSqtStrBgn = 58;
    private const int RSqtStrCtn = 59;
    private const int BrSqtStrCtn = 60;
    private const int RSqtEscSqt = 61;
    private const int BrSqtEscSqt = 62;
    private const int RSqtStrEnd = 63;
    private const int BrSqtStrEnd = 64;
    private const int RSglPlnNulBlk = 65;
    private const int BrSglPlnNulBlk = 66;
    private const int BSglPlnNulBlk = 67;
    private const int RSglPlnNulFlw = 68;
    private const int BrSglPlnNulFlw = 69;
    private const int RSglPlnBolBlk = 70;
    private const int BrSglPlnBolBlk = 71;
    private const int BSglPlnBolBlk = 72;
    private const int RSglPlnBolFlw = 73;
    private const int BrSglPlnBolFlw = 74;
    private const int RSglPlnIntBlk = 75;
    private const int BrSglPlnIntBlk = 76;
    private const int BSglPlnIntBlk = 77;
    private const int RSglPlnIntFlw = 78;
    private const int BrSglPlnIntFlw = 79;
    private const int RSglPlnFltBlk = 80;
    private const int BrSglPlnFltBlk = 81;
    private const int BSglPlnFltBlk = 82;
    private const int RSglPlnFltFlw = 83;
    private const int BrSglPlnFltFlw = 84;
    private const int RSglPlnTmsBlk = 85;
    private const int BrSglPlnTmsBlk = 86;
    private const int BSglPlnTmsBlk = 87;
    private const int RSglPlnTmsFlw = 88;
    private const int BrSglPlnTmsFlw = 89;
    private const int RSglPlnStrBlk = 90;
    private const int BrSglPlnStrBlk = 91;
    private const int BSglPlnStrBlk = 92;
    private const int RSglPlnStrFlw = 93;
    private const int BrSglPlnStrFlw = 94;
    private const int RMtlPlnStrBlk = 95;
    private const int BrMtlPlnStrBlk = 96;
    private const int RMtlPlnStrFlw = 97;
    private const int BrMtlPlnStrFlw = 98;
    private const int RTag = 99;
    private const int BrTag = 100;
    private const int BTag = 101;
    private const int RAcrBgn = 102;
    private const int BrAcrBgn = 103;
    private const int BAcrBgn = 104;
    private const int RAcrCtn = 105;
    private const int RAlsBgn = 106;
    private const int BrAlsBgn = 107;
    private const int BAlsBgn = 108;
    private const int RAlsCtn = 109;
    private const int Bl = 110;
    private const int Comment = 111;
    private const int ErrRec = 112;

    // enum ScanStatus
    private enum ScanStatus
    {
        Fail = -1,
        Stop = 0,
        Succ = 1,
    }

    // #define IND_ROT 'r' ... (the indentation types, stored as int16_t)
    private const short IndRot = (short)'r';
    private const short IndMap = (short)'m';
    private const short IndSeq = (short)'q';
    private const short IndStr = (short)'s';

    // TREE_SITTER_SERIALIZATION_BUFFER_SIZE
    private const int SerializationBufferSize = 1024;

    // Scanner (struct): the state kept between scans.
    private short _row;
    private short _col;
    private short _blkImpRow;
    private short _blkImpCol;
    private short _blkImpTab;
    private readonly List<short> _indTypStk = new List<short>();
    private readonly List<short> _indLenStk = new List<short>();

    // temp
    private short _endRow;
    private short _endCol;
    private short _curRow;
    private short _curCol;
    private int _curChr;
    private sbyte _schStt;
    private ResultSchema _rltSch;

    /// <summary>tree_sitter_yaml_external_scanner_create</summary>
    public YamlScanner()
    {
        Deserialize(ReadOnlySpan<byte>.Empty);
    }

    // ------------------------------------------------------------ helpers (scanner.c)

    /// <summary>SGL_PLN_SYM: the symbol of a single-line plain scalar of the scanned schema type.</summary>
    private int SglPlnSym(int nul, int bol, int intSym, int flt, int str) => _rltSch switch
    {
        ResultSchema.RsNull => nul,
        ResultSchema.RsBool => bol,
        ResultSchema.RsInt => intSym,
        ResultSchema.RsFloat => flt,
        _ => str,
    };

    /// <summary>RET_SYM: flush the position, set the result symbol and return true.</summary>
    private bool RetSym(Lexer lexer, int resultSymbol)
    {
        Flush();
        lexer.ResultSymbol = (ushort)resultSymbol;
        return true;
    }

    /// <summary>adv: advance one character (not skipped), counting the column.</summary>
    private void Adv(Lexer lexer)
    {
        _curCol++;
        _curChr = lexer.Lookahead;
        lexer.Advance(false);
    }

    /// <summary>adv_nwl: advance over a newline.</summary>
    private void AdvNwl(Lexer lexer)
    {
        _curRow++;
        _curCol = 0;
        _curChr = lexer.Lookahead;
        lexer.Advance(false);
    }

    /// <summary>skp: skip one character.</summary>
    private void Skp(Lexer lexer)
    {
        _curCol++;
        _curChr = lexer.Lookahead;
        lexer.Advance(true);
    }

    /// <summary>skp_nwl: skip over a newline.</summary>
    private void SkpNwl(Lexer lexer)
    {
        _curRow++;
        _curCol = 0;
        _curChr = lexer.Lookahead;
        lexer.Advance(true);
    }

    /// <summary>mrk_end: mark the end of the token at the current position.</summary>
    private void MrkEnd(Lexer lexer)
    {
        _endRow = _curRow;
        _endCol = _curCol;
        lexer.MarkEnd();
    }

    /// <summary>init: the temp state of one scan.</summary>
    private void Init()
    {
        _curRow = _row;
        _curCol = _col;
        _curChr = 0;
        _schStt = 0;
        _rltSch = ResultSchema.RsStr;
    }

    /// <summary>flush: the position of the token becomes the position of the scanner.</summary>
    private void Flush()
    {
        _row = _endRow;
        _col = _endCol;
    }

    /// <summary>POP_IND: false when the stack holds only the root (an incorrect state after error recovery).</summary>
    private bool TryPopInd()
    {
        if (_indTypStk.Count == 1)
        {
            return false;
        }

        PopInd();
        return true;
    }

    /// <summary>pop_ind</summary>
    private void PopInd()
    {
        _indLenStk.RemoveAt(_indLenStk.Count - 1);
        _indTypStk.RemoveAt(_indTypStk.Count - 1);
    }

    /// <summary>push_ind</summary>
    private void PushInd(short typ, short len)
    {
        _indLenStk.Add(len);
        _indTypStk.Add(typ);
    }

    /// <summary>PUSH_BGN_IND: false when a tab was used for the indentation.</summary>
    private bool PushBgnInd(bool hasTabInd, short typ, short bgnCol)
    {
        if (hasTabInd)
        {
            return false;
        }

        PushInd(typ, bgnCol);
        return true;
    }

    /// <summary>MAY_PUSH_IMP_IND: false when a tab was used for the implicit indentation.</summary>
    private bool MayPushImpInd(short curInd)
    {
        if (curInd != _blkImpCol)
        {
            if (_blkImpTab != 0)
            {
                return false;
            }

            PushInd(IndMap, _blkImpCol);
        }

        return true;
    }

    /// <summary>MAY_PUSH_SPC_SEQ_IND</summary>
    private void MayPushSpcSeqInd(short curIndTyp, short bgnCol)
    {
        if (curIndTyp == IndMap)
        {
            PushInd(IndSeq, bgnCol);
        }
    }

    /// <summary>MAY_UPD_IMP_COL</summary>
    private void MayUpdImpCol(short bgnRow, short bgnCol, bool hasTabInd)
    {
        if (_blkImpRow != bgnRow)
        {
            _blkImpRow = bgnRow;
            _blkImpCol = bgnCol;
            _blkImpTab = (short)(hasTabInd ? 1 : 0);
        }
    }

    private static bool IsWsp(int c) => c == ' ' || c == '\t';

    private static bool IsNwl(int c) => c == '\r' || c == '\n';

    private static bool IsWht(int c) => IsWsp(c) || IsNwl(c) || c == 0;

    private static bool IsNsDecDigit(int c) => c >= '0' && c <= '9';

    private static bool IsNsHexDigit(int c) =>
        IsNsDecDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsNsWordChar(int c) =>
        c == '-' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsNbJson(int c) => c == 0x09 || (c >= 0x20 && c <= 0x10ffff);

    private static bool IsNbDoubleChar(int c) => IsNbJson(c) && c != '\\' && c != '"';

    private static bool IsNbSingleChar(int c) => IsNbJson(c) && c != '\'';

    private static bool IsNsChar(int c) =>
        (c >= 0x21 && c <= 0x7e) || c == 0x85 || (c >= 0xa0 && c <= 0xd7ff) || (c >= 0xe000 && c <= 0xfefe) ||
        (c >= 0xff00 && c <= 0xfffd) || (c >= 0x10000 && c <= 0x10ffff);

    private static bool IsCIndicator(int c) =>
        c == '-' || c == '?' || c == ':' || c == ',' || c == '[' || c == ']' || c == '{' || c == '}' || c == '#' ||
        c == '&' || c == '*' || c == '!' || c == '|' || c == '>' || c == '\'' || c == '"' || c == '%' || c == '@' ||
        c == '`';

    private static bool IsCFlowIndicator(int c) => c == ',' || c == '[' || c == ']' || c == '{' || c == '}';

    private static bool IsPlainSafeInBlock(int c) => IsNsChar(c);

    private static bool IsPlainSafeInFlow(int c) => IsNsChar(c) && !IsCFlowIndicator(c);

    /// <summary>is_plain_safe: the block function when <paramref name="inFlow"/> is false, the flow one otherwise.</summary>
    private static bool IsPlainSafe(int c, bool inFlow) => inFlow ? IsPlainSafeInFlow(c) : IsPlainSafeInBlock(c);

    private static bool IsNsUriChar(int c) =>
        IsNsWordChar(c) || c == '#' || c == ';' || c == '/' || c == '?' || c == ':' || c == '@' || c == '&' ||
        c == '=' || c == '+' || c == '$' || c == ',' || c == '_' || c == '.' || c == '!' || c == '~' || c == '*' ||
        c == '\'' || c == '(' || c == ')' || c == '[' || c == ']';

    private static bool IsNsTagChar(int c) =>
        IsNsWordChar(c) || c == '#' || c == ';' || c == '/' || c == '?' || c == ':' || c == '@' || c == '&' ||
        c == '=' || c == '+' || c == '$' || c == '_' || c == '.' || c == '~' || c == '*' || c == '\'' || c == '(' ||
        c == ')';

    private static bool IsNsAnchorChar(int c) => IsNsChar(c) && !IsCFlowIndicator(c);

    // ------------------------------------------------------------ scanners (scanner.c)

    private ScanStatus ScnUriEsc(Lexer lexer)
    {
        if (lexer.Lookahead != '%')
        {
            return ScanStatus.Stop;
        }

        MrkEnd(lexer);
        Adv(lexer);
        if (!IsNsHexDigit(lexer.Lookahead))
        {
            return ScanStatus.Fail;
        }

        Adv(lexer);
        if (!IsNsHexDigit(lexer.Lookahead))
        {
            return ScanStatus.Fail;
        }

        Adv(lexer);
        return ScanStatus.Succ;
    }

    private ScanStatus ScnNsUriChar(Lexer lexer)
    {
        if (IsNsUriChar(lexer.Lookahead))
        {
            Adv(lexer);
            return ScanStatus.Succ;
        }

        return ScnUriEsc(lexer);
    }

    private ScanStatus ScnNsTagChar(Lexer lexer)
    {
        if (IsNsTagChar(lexer.Lookahead))
        {
            Adv(lexer);
            return ScanStatus.Succ;
        }

        return ScnUriEsc(lexer);
    }

    private bool ScnDirBgn(Lexer lexer)
    {
        Adv(lexer);
        if (lexer.Lookahead == 'Y')
        {
            Adv(lexer);
            if (lexer.Lookahead == 'A')
            {
                Adv(lexer);
                if (lexer.Lookahead == 'M')
                {
                    Adv(lexer);
                    if (lexer.Lookahead == 'L')
                    {
                        Adv(lexer);
                        if (IsWht(lexer.Lookahead))
                        {
                            MrkEnd(lexer);
                            return RetSym(lexer, SDirYmlBgn);
                        }
                    }
                }
            }
        }
        else if (lexer.Lookahead == 'T')
        {
            Adv(lexer);
            if (lexer.Lookahead == 'A')
            {
                Adv(lexer);
                if (lexer.Lookahead == 'G')
                {
                    Adv(lexer);
                    if (IsWht(lexer.Lookahead))
                    {
                        MrkEnd(lexer);
                        return RetSym(lexer, SDirTagBgn);
                    }
                }
            }
        }

        for (;;)
        {
            if (!IsNsChar(lexer.Lookahead))
            {
                break;
            }

            Adv(lexer);
        }

        if (_curCol > 1 && IsWht(lexer.Lookahead))
        {
            MrkEnd(lexer);
            return RetSym(lexer, SDirRsvBgn);
        }

        return false;
    }

    private bool ScnDirYmlVer(Lexer lexer, int resultSymbol)
    {
        int n1 = 0;
        int n2 = 0;
        while (IsNsDecDigit(lexer.Lookahead))
        {
            Adv(lexer);
            n1++;
        }

        if (lexer.Lookahead != '.')
        {
            return false;
        }

        Adv(lexer);
        while (IsNsDecDigit(lexer.Lookahead))
        {
            Adv(lexer);
            n2++;
        }

        if (n1 == 0 || n2 == 0)
        {
            return false;
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnTagHdlTal(Lexer lexer)
    {
        if (lexer.Lookahead == '!')
        {
            Adv(lexer);
            return true;
        }

        int n = 0;
        while (IsNsWordChar(lexer.Lookahead))
        {
            Adv(lexer);
            n++;
        }

        if (n == 0)
        {
            return true;
        }

        if (lexer.Lookahead == '!')
        {
            Adv(lexer);
            return true;
        }

        return false;
    }

    private bool ScnDirTagHdl(Lexer lexer, int resultSymbol)
    {
        if (lexer.Lookahead == '!')
        {
            Adv(lexer);
            if (ScnTagHdlTal(lexer))
            {
                MrkEnd(lexer);
                return RetSym(lexer, resultSymbol);
            }
        }

        return false;
    }

    private bool ScnDirTagPfx(Lexer lexer, int resultSymbol)
    {
        if (lexer.Lookahead == '!')
        {
            Adv(lexer);
        }
        else if (ScnNsTagChar(lexer) != ScanStatus.Succ)
        {
            return false;
        }

        for (;;)
        {
            switch (ScnNsUriChar(lexer))
            {
                case ScanStatus.Stop:
                    MrkEnd(lexer);
                    return RetSym(lexer, resultSymbol);
                case ScanStatus.Fail:
                    return RetSym(lexer, resultSymbol);
                default:
                    break;
            }
        }
    }

    private bool ScnDirRsvPrm(Lexer lexer, int resultSymbol)
    {
        if (!IsNsChar(lexer.Lookahead))
        {
            return false;
        }

        Adv(lexer);
        while (IsNsChar(lexer.Lookahead))
        {
            Adv(lexer);
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnTag(Lexer lexer, int resultSymbol)
    {
        if (lexer.Lookahead != '!')
        {
            return false;
        }

        Adv(lexer);
        if (IsWht(lexer.Lookahead))
        {
            MrkEnd(lexer);
            return RetSym(lexer, resultSymbol);
        }

        if (lexer.Lookahead == '<')
        {
            Adv(lexer);
            if (ScnNsUriChar(lexer) != ScanStatus.Succ)
            {
                return false;
            }

            for (;;)
            {
                switch (ScnNsUriChar(lexer))
                {
                    case ScanStatus.Stop:
                        if (lexer.Lookahead == '>')
                        {
                            Adv(lexer);
                            MrkEnd(lexer);
                            return RetSym(lexer, resultSymbol);
                        }

                        return false;
                    case ScanStatus.Fail:
                        return false;
                    default:
                        break;
                }
            }
        }
        else
        {
            if (ScnTagHdlTal(lexer) && ScnNsTagChar(lexer) != ScanStatus.Succ)
            {
                return false;
            }

            for (;;)
            {
                switch (ScnNsTagChar(lexer))
                {
                    case ScanStatus.Stop:
                        MrkEnd(lexer);
                        return RetSym(lexer, resultSymbol);
                    case ScanStatus.Fail:
                        return RetSym(lexer, resultSymbol);
                    default:
                        break;
                }
            }
        }
    }

    private bool ScnAcrBgn(Lexer lexer, int resultSymbol)
    {
        if (lexer.Lookahead != '&')
        {
            return false;
        }

        Adv(lexer);
        if (!IsNsAnchorChar(lexer.Lookahead))
        {
            return false;
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnAcrCtn(Lexer lexer, int resultSymbol)
    {
        while (IsNsAnchorChar(lexer.Lookahead))
        {
            Adv(lexer);
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnAlsBgn(Lexer lexer, int resultSymbol)
    {
        if (lexer.Lookahead != '*')
        {
            return false;
        }

        Adv(lexer);
        if (!IsNsAnchorChar(lexer.Lookahead))
        {
            return false;
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnAlsCtn(Lexer lexer, int resultSymbol)
    {
        while (IsNsAnchorChar(lexer.Lookahead))
        {
            Adv(lexer);
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnDqtEscSeq(Lexer lexer, int resultSymbol)
    {
        int i;
        switch (lexer.Lookahead)
        {
            case '0':
            case 'a':
            case 'b':
            case 't':
            case '\t':
            case 'n':
            case 'v':
            case 'r':
            case 'e':
            case 'f':
            case ' ':
            case '"':
            case '/':
            case '\\':
            case 'N':
            case '_':
            case 'L':
            case 'P':
                Adv(lexer);
                break;
            case 'U':
                Adv(lexer);
                for (i = 0; i < 8; i++)
                {
                    if (IsNsHexDigit(lexer.Lookahead))
                    {
                        Adv(lexer);
                    }
                    else
                    {
                        return false;
                    }
                }

                break;
            case 'u':
                Adv(lexer);
                for (i = 0; i < 4; i++)
                {
                    if (IsNsHexDigit(lexer.Lookahead))
                    {
                        Adv(lexer);
                    }
                    else
                    {
                        return false;
                    }
                }

                break;
            case 'x':
                Adv(lexer);
                for (i = 0; i < 2; i++)
                {
                    if (IsNsHexDigit(lexer.Lookahead))
                    {
                        Adv(lexer);
                    }
                    else
                    {
                        return false;
                    }
                }

                break;
            default:
                return false;
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnDrsDocEnd(Lexer lexer)
    {
        if (lexer.Lookahead != '-' && lexer.Lookahead != '.')
        {
            return false;
        }

        int delimeter = lexer.Lookahead;
        Adv(lexer);
        if (lexer.Lookahead == delimeter)
        {
            Adv(lexer);
            if (lexer.Lookahead == delimeter)
            {
                Adv(lexer);
                if (IsWht(lexer.Lookahead))
                {
                    return true;
                }
            }
        }

        MrkEnd(lexer);
        return false;
    }

    private bool ScnDqtStrCnt(Lexer lexer, int resultSymbol)
    {
        if (!IsNbDoubleChar(lexer.Lookahead))
        {
            return false;
        }

        if (_curCol == 0 && ScnDrsDocEnd(lexer))
        {
            MrkEnd(lexer);
            return RetSym(lexer, _curChr == '-' ? SDrsEnd : SDocEnd);
        }
        else
        {
            Adv(lexer);
        }

        while (IsNbDoubleChar(lexer.Lookahead))
        {
            Adv(lexer);
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnSqtStrCnt(Lexer lexer, int resultSymbol)
    {
        if (!IsNbSingleChar(lexer.Lookahead))
        {
            return false;
        }

        if (_curCol == 0 && ScnDrsDocEnd(lexer))
        {
            MrkEnd(lexer);
            return RetSym(lexer, _curChr == '-' ? SDrsEnd : SDocEnd);
        }
        else
        {
            Adv(lexer);
        }

        while (IsNbSingleChar(lexer.Lookahead))
        {
            Adv(lexer);
        }

        MrkEnd(lexer);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnBlkStrBgn(Lexer lexer, int resultSymbol)
    {
        if (lexer.Lookahead != '|' && lexer.Lookahead != '>')
        {
            return false;
        }

        Adv(lexer);
        short curInd = _indLenStk[_indLenStk.Count - 1];
        short ind = -1;
        if (lexer.Lookahead >= '1' && lexer.Lookahead <= '9')
        {
            ind = (short)(lexer.Lookahead - '1');
            Adv(lexer);
            if (lexer.Lookahead == '+' || lexer.Lookahead == '-')
            {
                Adv(lexer);
            }
        }
        else if (lexer.Lookahead == '+' || lexer.Lookahead == '-')
        {
            Adv(lexer);
            if (lexer.Lookahead >= '1' && lexer.Lookahead <= '9')
            {
                ind = (short)(lexer.Lookahead - '1');
                Adv(lexer);
            }
        }

        if (!IsWht(lexer.Lookahead))
        {
            return false;
        }

        MrkEnd(lexer);
        if (ind != -1)
        {
            ind = (short)(ind + curInd);
        }
        else
        {
            ind = curInd;
            while (IsWsp(lexer.Lookahead))
            {
                Adv(lexer);
            }

            if (lexer.Lookahead == '#')
            {
                Adv(lexer);
                while (!IsNwl(lexer.Lookahead) && lexer.Lookahead != 0)
                {
                    Adv(lexer);
                }
            }

            if (IsNwl(lexer.Lookahead))
            {
                AdvNwl(lexer);
            }

            while (lexer.Lookahead != 0)
            {
                if (lexer.Lookahead == ' ')
                {
                    Adv(lexer);
                }
                else if (IsNwl(lexer.Lookahead))
                {
                    if (_curCol - 1 < ind)
                    {
                        break;
                    }

                    ind = (short)(_curCol - 1);
                    AdvNwl(lexer);
                }
                else
                {
                    if (_curCol - 1 > ind)
                    {
                        ind = (short)(_curCol - 1);
                    }

                    break;
                }
            }
        }

        PushInd(IndStr, ind);
        return RetSym(lexer, resultSymbol);
    }

    private bool ScnBlkStrCnt(Lexer lexer, int resultSymbol)
    {
        if (!IsNsChar(lexer.Lookahead))
        {
            return false;
        }

        if (_curCol == 0 && ScnDrsDocEnd(lexer))
        {
            if (!TryPopInd())
            {
                return false;
            }

            return RetSym(lexer, Bl);
        }
        else
        {
            Adv(lexer);
        }

        MrkEnd(lexer);
        for (;;)
        {
            if (IsNsChar(lexer.Lookahead))
            {
                Adv(lexer);
                while (IsNsChar(lexer.Lookahead))
                {
                    Adv(lexer);
                }

                MrkEnd(lexer);
            }

            if (IsWsp(lexer.Lookahead))
            {
                Adv(lexer);
                while (IsWsp(lexer.Lookahead))
                {
                    Adv(lexer);
                }
            }
            else
            {
                break;
            }
        }

        return RetSym(lexer, resultSymbol);
    }

    private ScanStatus ScnPlnCnt(Lexer lexer, bool inFlow)
    {
        // The C variable is_cur_wsp is assigned but never read, so it is not kept.
        bool isCurSaf = IsPlainSafe(_curChr, inFlow);
        bool isLkaWsp = IsWsp(lexer.Lookahead);
        bool isLkaSaf = IsPlainSafe(lexer.Lookahead, inFlow);
        if (isLkaSaf || isLkaWsp)
        {
            for (;;)
            {
                if (isLkaSaf && lexer.Lookahead != '#' && lexer.Lookahead != ':')
                {
                    Adv(lexer);
                    MrkEnd(lexer);
                    _schStt = SchemaCore.AdvSchStt(_schStt, _curChr, ref _rltSch);
                }
                else if (isCurSaf && lexer.Lookahead == '#')
                {
                    Adv(lexer);
                    MrkEnd(lexer);
                    _schStt = SchemaCore.AdvSchStt(_schStt, _curChr, ref _rltSch);
                }
                else if (isLkaWsp)
                {
                    Adv(lexer);
                    _schStt = SchemaCore.AdvSchStt(_schStt, _curChr, ref _rltSch);
                }
                else if (lexer.Lookahead == ':')
                {
                    Adv(lexer); // check later
                }
                else
                {
                    break;
                }

                isCurSaf = isLkaSaf;
                isLkaWsp = IsWsp(lexer.Lookahead);
                isLkaSaf = IsPlainSafe(lexer.Lookahead, inFlow);

                if (_curChr == ':')
                {
                    if (isLkaSaf)
                    {
                        MrkEnd(lexer);
                        _schStt = SchemaCore.AdvSchStt(_schStt, _curChr, ref _rltSch);
                    }
                    else
                    {
                        return ScanStatus.Fail;
                    }
                }
            }
        }
        else
        {
            return ScanStatus.Stop;
        }

        return ScanStatus.Succ;
    }

    /// <summary>scan (scanner.c): the scan of one token; <paramref name="validSymbols"/> is valid_symbols.</summary>
    private bool ScanToken(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        Init();
        MrkEnd(lexer);

        bool allowComment = !(validSymbols[RDqtStrCtn] || validSymbols[BrDqtStrCtn] ||
                              validSymbols[RSqtStrCtn] || validSymbols[BrSqtStrCtn]);
        int indPtr = _indLenStk.Count - 1;
        short curInd = _indLenStk[indPtr];
        indPtr--;
        short prtInd = indPtr == -1 ? (short)-1 : _indLenStk[indPtr];
        short curIndTyp = _indTypStk[_indTypStk.Count - 1];

        bool hasTabInd = false;
        short leadingSpaces = 0;

        for (;;)
        {
            if (lexer.Lookahead == ' ')
            {
                if (!hasTabInd)
                {
                    leadingSpaces++;
                }

                Skp(lexer);
            }
            else if (lexer.Lookahead == '\t')
            {
                hasTabInd = true;
                Skp(lexer);
            }
            else if (IsNwl(lexer.Lookahead))
            {
                hasTabInd = false;
                leadingSpaces = 0;
                SkpNwl(lexer);
            }
            else if (allowComment && lexer.Lookahead == '#')
            {
                if (validSymbols[BrBlkStrCtn] && validSymbols[Bl] && _curCol <= curInd)
                {
                    if (!TryPopInd())
                    {
                        return false;
                    }

                    return RetSym(lexer, Bl);
                }

                if (validSymbols[BrBlkStrCtn]
                        ? _curRow == _row
                        : _curCol == 0 || _curRow != _row || _curCol > _col)
                {
                    Adv(lexer);
                    while (!IsNwl(lexer.Lookahead) && lexer.Lookahead != 0)
                    {
                        Adv(lexer);
                    }

                    MrkEnd(lexer);
                    return RetSym(lexer, Comment);
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        if (lexer.Lookahead == 0)
        {
            if (validSymbols[Bl])
            {
                MrkEnd(lexer);
                if (!TryPopInd())
                {
                    return false;
                }

                return RetSym(lexer, Bl);
            }

            if (validSymbols[EndOfFile])
            {
                MrkEnd(lexer);
                return RetSym(lexer, EndOfFile);
            }

            return false;
        }

        short bgnRow = _curRow;
        short bgnCol = _curCol;
        int bgnChr = lexer.Lookahead;

        if (validSymbols[Bl] && bgnCol <= curInd && !hasTabInd)
        {
            if (curInd == prtInd && curIndTyp == IndSeq
                    ? bgnCol < curInd || lexer.Lookahead != '-'
                    : bgnCol <= prtInd || curIndTyp == IndStr)
            {
                if (!TryPopInd())
                {
                    return false;
                }

                return RetSym(lexer, Bl);
            }
        }

        bool hasNwl = _curRow > _row;
        bool isR = !hasNwl;
        bool isBr = hasNwl && leadingSpaces > curInd;
        bool isB = hasNwl && leadingSpaces == curInd && !hasTabInd;
        bool isS = bgnCol == 0;

        if (validSymbols[RDirYmlVer] && isR)
        {
            return ScnDirYmlVer(lexer, RDirYmlVer);
        }

        if (validSymbols[RDirTagHdl] && isR)
        {
            return ScnDirTagHdl(lexer, RDirTagHdl);
        }

        if (validSymbols[RDirTagPfx] && isR)
        {
            return ScnDirTagPfx(lexer, RDirTagPfx);
        }

        if (validSymbols[RDirRsvPrm] && isR)
        {
            return ScnDirRsvPrm(lexer, RDirRsvPrm);
        }

        if (validSymbols[BrBlkStrCtn] && isBr && ScnBlkStrCnt(lexer, BrBlkStrCtn))
        {
            return true;
        }

        if ((validSymbols[RDqtStrCtn] && isR && ScnDqtStrCnt(lexer, RDqtStrCtn)) ||
            (validSymbols[BrDqtStrCtn] && (isBr || hasNwl) && ScnDqtStrCnt(lexer, BrDqtStrCtn)))
        {
            return true;
        }

        if ((validSymbols[RSqtStrCtn] && isR && ScnSqtStrCnt(lexer, RSqtStrCtn)) ||
            (validSymbols[BrSqtStrCtn] && isBr && ScnSqtStrCnt(lexer, BrSqtStrCtn)))
        {
            return true;
        }

        if (validSymbols[RAcrCtn] && isR)
        {
            return ScnAcrCtn(lexer, RAcrCtn);
        }

        if (validSymbols[RAlsCtn] && isR)
        {
            return ScnAlsCtn(lexer, RAlsCtn);
        }

        if (lexer.Lookahead == '%')
        {
            if (validSymbols[SDirYmlBgn] && isS)
            {
                return ScnDirBgn(lexer);
            }
        }
        else if (lexer.Lookahead == '*')
        {
            if (validSymbols[RAlsBgn] && isR)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnAlsBgn(lexer, RAlsBgn);
            }

            if (validSymbols[BrAlsBgn] && isBr)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnAlsBgn(lexer, BrAlsBgn);
            }

            if (validSymbols[BAlsBgn] && isB)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnAlsBgn(lexer, BAlsBgn);
            }
        }
        else if (lexer.Lookahead == '&')
        {
            if (validSymbols[RAcrBgn] && isR)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnAcrBgn(lexer, RAcrBgn);
            }

            if (validSymbols[BrAcrBgn] && isBr)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnAcrBgn(lexer, BrAcrBgn);
            }

            if (validSymbols[BAcrBgn] && isB)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnAcrBgn(lexer, BAcrBgn);
            }
        }
        else if (lexer.Lookahead == '!')
        {
            if (validSymbols[RTag] && isR)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnTag(lexer, RTag);
            }

            if (validSymbols[BrTag] && isBr)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnTag(lexer, BrTag);
            }

            if (validSymbols[BTag] && isB)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                return ScnTag(lexer, BTag);
            }
        }
        else if (lexer.Lookahead == '[')
        {
            if (validSymbols[RFlwSeqBgn] && isR)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RFlwSeqBgn);
            }

            if (validSymbols[BrFlwSeqBgn] && isBr)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwSeqBgn);
            }

            if (validSymbols[BFlwSeqBgn] && isB)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BFlwSeqBgn);
            }
        }
        else if (lexer.Lookahead == ']')
        {
            if (validSymbols[RFlwSeqEnd] && isR)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RFlwSeqEnd);
            }

            if (validSymbols[BrFlwSeqEnd] && isBr)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwSeqEnd);
            }

            if (validSymbols[BFlwSeqEnd] && isB)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwSeqEnd); // as in C: the B_FLW_SEQ_END case returns BR_FLW_SEQ_END
            }
        }
        else if (lexer.Lookahead == '{')
        {
            if (validSymbols[RFlwMapBgn] && isR)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RFlwMapBgn);
            }

            if (validSymbols[BrFlwMapBgn] && isBr)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwMapBgn);
            }

            if (validSymbols[BFlwMapBgn] && isB)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BFlwMapBgn);
            }
        }
        else if (lexer.Lookahead == '}')
        {
            if (validSymbols[RFlwMapEnd] && isR)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RFlwMapEnd);
            }

            if (validSymbols[BrFlwMapEnd] && isBr)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwMapEnd);
            }

            if (validSymbols[BFlwMapEnd] && isB)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwMapEnd); // as in C: the B_FLW_MAP_END case returns BR_FLW_MAP_END
            }
        }
        else if (lexer.Lookahead == ',')
        {
            if (validSymbols[RFlwSepBgn] && isR)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RFlwSepBgn);
            }

            if (validSymbols[BrFlwSepBgn] && isBr)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwSepBgn);
            }
        }
        else if (lexer.Lookahead == '"')
        {
            if (validSymbols[RDqtStrBgn] && isR)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RDqtStrBgn);
            }

            if (validSymbols[BrDqtStrBgn] && isBr)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrDqtStrBgn);
            }

            if (validSymbols[BDqtStrBgn] && isB)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BDqtStrBgn);
            }

            if (validSymbols[RDqtStrEnd] && isR)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RDqtStrEnd);
            }

            if (validSymbols[BrDqtStrEnd] && isBr)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrDqtStrEnd);
            }
        }
        else if (lexer.Lookahead == '\'')
        {
            if (validSymbols[RSqtStrBgn] && isR)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RSqtStrBgn);
            }

            if (validSymbols[BrSqtStrBgn] && isBr)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrSqtStrBgn);
            }

            if (validSymbols[BSqtStrBgn] && isB)
            {
                MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BSqtStrBgn);
            }

            if (validSymbols[RSqtStrEnd] && isR)
            {
                Adv(lexer);
                if (lexer.Lookahead == '\'')
                {
                    Adv(lexer);
                    MrkEnd(lexer);
                    return RetSym(lexer, RSqtEscSqt);
                }
                else
                {
                    MrkEnd(lexer);
                    return RetSym(lexer, RSqtStrEnd);
                }
            }

            if (validSymbols[BrSqtStrEnd] && isBr)
            {
                Adv(lexer);
                if (lexer.Lookahead == '\'')
                {
                    Adv(lexer);
                    MrkEnd(lexer);
                    return RetSym(lexer, BrSqtEscSqt);
                }
                else
                {
                    MrkEnd(lexer);
                    return RetSym(lexer, BrSqtStrEnd);
                }
            }
        }
        else if (lexer.Lookahead == '?')
        {
            bool isRBlkKeyBgn = validSymbols[RBlkKeyBgn] && isR;
            bool isBrBlkKeyBgn = validSymbols[BrBlkKeyBgn] && isBr;
            bool isBBlkKeyBgn = validSymbols[BBlkKeyBgn] && isB;
            bool isRFlwKeyBgn = validSymbols[RFlwKeyBgn] && isR;
            bool isBrFlwKeyBgn = validSymbols[BrFlwKeyBgn] && isBr;
            if (isRBlkKeyBgn || isBrBlkKeyBgn || isBBlkKeyBgn || isRFlwKeyBgn || isBrFlwKeyBgn)
            {
                Adv(lexer);
                if (IsWht(lexer.Lookahead))
                {
                    MrkEnd(lexer);
                    if (isRBlkKeyBgn)
                    {
                        if (!PushBgnInd(hasTabInd, IndMap, bgnCol))
                        {
                            return false;
                        }

                        return RetSym(lexer, RBlkKeyBgn);
                    }

                    if (isBrBlkKeyBgn)
                    {
                        if (!PushBgnInd(hasTabInd, IndMap, bgnCol))
                        {
                            return false;
                        }

                        return RetSym(lexer, BrBlkKeyBgn);
                    }

                    if (isBBlkKeyBgn)
                    {
                        return RetSym(lexer, BBlkKeyBgn);
                    }

                    if (isRFlwKeyBgn)
                    {
                        return RetSym(lexer, RFlwKeyBgn);
                    }

                    if (isBrFlwKeyBgn)
                    {
                        return RetSym(lexer, BrFlwKeyBgn);
                    }
                }
            }
        }
        else if (lexer.Lookahead == ':')
        {
            if (validSymbols[RFlwJsvBgn] && isR)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, RFlwJsvBgn);
            }

            if (validSymbols[BrFlwJsvBgn] && isBr)
            {
                Adv(lexer);
                MrkEnd(lexer);
                return RetSym(lexer, BrFlwJsvBgn);
            }

            bool isRBlkValBgn = validSymbols[RBlkValBgn] && isR;
            bool isBrBlkValBgn = validSymbols[BrBlkValBgn] && isBr;
            bool isBBlkValBgn = validSymbols[BBlkValBgn] && isB;
            bool isRBlkImpBgn = validSymbols[RBlkImpBgn] && isR;
            bool isRFlwNjvBgn = validSymbols[RFlwNjvBgn] && isR;
            bool isBrFlwNjvBgn = validSymbols[BrFlwNjvBgn] && isBr;
            if (isRBlkValBgn || isBrBlkValBgn || isBBlkValBgn || isRBlkImpBgn || isRFlwNjvBgn ||
                isBrFlwNjvBgn)
            {
                Adv(lexer);
                bool isLkaWht = IsWht(lexer.Lookahead);
                if (isLkaWht)
                {
                    if (isRBlkValBgn)
                    {
                        if (!PushBgnInd(hasTabInd, IndMap, bgnCol))
                        {
                            return false;
                        }

                        MrkEnd(lexer);
                        return RetSym(lexer, RBlkValBgn);
                    }

                    if (isBrBlkValBgn)
                    {
                        if (!PushBgnInd(hasTabInd, IndMap, bgnCol))
                        {
                            return false;
                        }

                        MrkEnd(lexer);
                        return RetSym(lexer, BrBlkValBgn);
                    }

                    if (isBBlkValBgn)
                    {
                        MrkEnd(lexer);
                        return RetSym(lexer, BBlkValBgn);
                    }

                    if (isRBlkImpBgn)
                    {
                        if (!MayPushImpInd(curInd))
                        {
                            return false;
                        }

                        MrkEnd(lexer);
                        return RetSym(lexer, RBlkImpBgn);
                    }
                }

                if (isLkaWht || lexer.Lookahead == ',' || lexer.Lookahead == ']' || lexer.Lookahead == '}')
                {
                    if (isRFlwNjvBgn)
                    {
                        MrkEnd(lexer);
                        return RetSym(lexer, RFlwNjvBgn);
                    }

                    if (isBrFlwNjvBgn)
                    {
                        MrkEnd(lexer);
                        return RetSym(lexer, BrFlwNjvBgn);
                    }
                }
            }
        }
        else if (lexer.Lookahead == '-')
        {
            bool isRBlkSeqBgn = validSymbols[RBlkSeqBgn] && isR;
            bool isBrBlkSeqBgn = validSymbols[BrBlkSeqBgn] && isBr;
            bool isBBlkSeqBgn = validSymbols[BBlkSeqBgn] && isB;
            bool isSDrsEnd = isS;
            if (isRBlkSeqBgn || isBrBlkSeqBgn || isBBlkSeqBgn || isSDrsEnd)
            {
                Adv(lexer);
                if (IsWht(lexer.Lookahead))
                {
                    if (isRBlkSeqBgn)
                    {
                        if (!PushBgnInd(hasTabInd, IndSeq, bgnCol))
                        {
                            return false;
                        }

                        MrkEnd(lexer);
                        return RetSym(lexer, RBlkSeqBgn);
                    }

                    if (isBrBlkSeqBgn)
                    {
                        if (!PushBgnInd(hasTabInd, IndSeq, bgnCol))
                        {
                            return false;
                        }

                        MrkEnd(lexer);
                        return RetSym(lexer, BrBlkSeqBgn);
                    }

                    if (isBBlkSeqBgn)
                    {
                        MayPushSpcSeqInd(curIndTyp, bgnCol);
                        MrkEnd(lexer);
                        return RetSym(lexer, BBlkSeqBgn);
                    }
                }
                else if (lexer.Lookahead == '-' && isSDrsEnd)
                {
                    Adv(lexer);
                    if (lexer.Lookahead == '-')
                    {
                        Adv(lexer);
                        if (IsWht(lexer.Lookahead))
                        {
                            if (validSymbols[Bl])
                            {
                                if (!TryPopInd())
                                {
                                    return false;
                                }

                                return RetSym(lexer, Bl);
                            }

                            MrkEnd(lexer);
                            return RetSym(lexer, SDrsEnd);
                        }
                    }
                }
            }
        }
        else if (lexer.Lookahead == '.')
        {
            if (isS)
            {
                Adv(lexer);
                if (lexer.Lookahead == '.')
                {
                    Adv(lexer);
                    if (lexer.Lookahead == '.')
                    {
                        Adv(lexer);
                        if (IsWht(lexer.Lookahead))
                        {
                            if (validSymbols[Bl])
                            {
                                if (!TryPopInd())
                                {
                                    return false;
                                }

                                return RetSym(lexer, Bl);
                            }

                            MrkEnd(lexer);
                            return RetSym(lexer, SDocEnd);
                        }
                    }
                }
            }
        }
        else if (lexer.Lookahead == '\\')
        {
            bool isRDqtEscNwl = validSymbols[RDqtEscNwl] && isR;
            bool isBrDqtEscNwl = validSymbols[BrDqtEscNwl] && isBr;
            bool isRDqtEscSeq = validSymbols[RDqtEscSeq] && isR;
            bool isBrDqtEscSeq = validSymbols[BrDqtEscSeq] && isBr;
            if (isRDqtEscNwl || isBrDqtEscNwl || isRDqtEscSeq || isBrDqtEscSeq)
            {
                Adv(lexer);
                if (IsNwl(lexer.Lookahead))
                {
                    if (isRDqtEscNwl)
                    {
                        MrkEnd(lexer);
                        return RetSym(lexer, RDqtEscNwl);
                    }

                    if (isBrDqtEscNwl)
                    {
                        MrkEnd(lexer);
                        return RetSym(lexer, BrDqtEscNwl);
                    }
                }

                if (isRDqtEscSeq)
                {
                    return ScnDqtEscSeq(lexer, RDqtEscSeq);
                }

                if (isBrDqtEscSeq)
                {
                    return ScnDqtEscSeq(lexer, BrDqtEscSeq);
                }

                return false;
            }
        }
        else if (lexer.Lookahead == '|')
        {
            if (validSymbols[RBlkLitBgn] && isR)
            {
                return ScnBlkStrBgn(lexer, RBlkLitBgn);
            }

            if (validSymbols[BrBlkLitBgn] && isBr)
            {
                return ScnBlkStrBgn(lexer, BrBlkLitBgn);
            }
        }
        else if (lexer.Lookahead == '>')
        {
            if (validSymbols[RBlkFldBgn] && isR)
            {
                return ScnBlkStrBgn(lexer, RBlkFldBgn);
            }

            if (validSymbols[BrBlkFldBgn] && isBr)
            {
                return ScnBlkStrBgn(lexer, BrBlkFldBgn);
            }
        }

        bool maybeSglPlnBlk = (validSymbols[RSglPlnStrBlk] && isR) ||
                              (validSymbols[BrSglPlnStrBlk] && isBr) || (validSymbols[BSglPlnStrBlk] && isB);
        bool maybeSglPlnFlw = (validSymbols[RSglPlnStrFlw] && isR) || (validSymbols[BrSglPlnStrFlw] && isBr);
        bool maybeMtlPlnBlk = (validSymbols[RMtlPlnStrBlk] && isR) || (validSymbols[BrMtlPlnStrBlk] && isBr);
        bool maybeMtlPlnFlw = (validSymbols[RMtlPlnStrFlw] && isR) || (validSymbols[BrMtlPlnStrFlw] && isBr);

        if (maybeSglPlnBlk || maybeSglPlnFlw || maybeMtlPlnBlk || maybeMtlPlnFlw)
        {
            bool isInBlk = maybeSglPlnBlk || maybeMtlPlnBlk;
            bool inFlow = !isInBlk; // is_plain_safe: the flow function when not in a block
            if (_curCol - bgnCol == 0)
            {
                Adv(lexer);
            }

            if (_curCol - bgnCol == 1)
            {
                bool isPlainFirst =
                    (IsNsChar(bgnChr) && !IsCIndicator(bgnChr)) ||
                    ((bgnChr == '-' || bgnChr == '?' || bgnChr == ':') && IsPlainSafe(lexer.Lookahead, inFlow));
                if (!isPlainFirst)
                {
                    return false;
                }

                _schStt = SchemaCore.AdvSchStt(_schStt, _curChr, ref _rltSch);
            }
            else
            {
                // no need to check the following cases:
                // ..X
                // ...X
                // --X
                // ---X
                // X: lookahead
                _schStt = SchemaCore.SchSttFrz; // must be RS_STR
            }

            MrkEnd(lexer);

            for (;;)
            {
                if (!IsNwl(lexer.Lookahead))
                {
                    if (ScnPlnCnt(lexer, inFlow) != ScanStatus.Succ)
                    {
                        break;
                    }
                }

                if (lexer.Lookahead == 0 || !IsNwl(lexer.Lookahead))
                {
                    break;
                }

                for (;;)
                {
                    if (IsNwl(lexer.Lookahead))
                    {
                        AdvNwl(lexer);
                    }
                    else if (IsWsp(lexer.Lookahead))
                    {
                        Adv(lexer);
                    }
                    else
                    {
                        break;
                    }
                }

                if (lexer.Lookahead == 0 || _curCol <= curInd)
                {
                    break;
                }

                if (_curCol == 0 && ScnDrsDocEnd(lexer))
                {
                    break;
                }
            }

            if (_endRow == bgnRow)
            {
                if (maybeSglPlnBlk)
                {
                    MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                    return RetSym(lexer, isR
                        ? SglPlnSym(RSglPlnNulBlk, RSglPlnBolBlk, RSglPlnIntBlk, RSglPlnFltBlk, RSglPlnStrBlk)
                        : isBr
                            ? SglPlnSym(BrSglPlnNulBlk, BrSglPlnBolBlk, BrSglPlnIntBlk, BrSglPlnFltBlk, BrSglPlnStrBlk)
                            : SglPlnSym(BSglPlnNulBlk, BSglPlnBolBlk, BSglPlnIntBlk, BSglPlnFltBlk, BSglPlnStrBlk));
                }

                if (maybeSglPlnFlw)
                {
                    return RetSym(lexer, isR
                        ? SglPlnSym(RSglPlnNulFlw, RSglPlnBolFlw, RSglPlnIntFlw, RSglPlnFltFlw, RSglPlnStrFlw)
                        : SglPlnSym(BrSglPlnNulFlw, BrSglPlnBolFlw, BrSglPlnIntFlw, BrSglPlnFltFlw, BrSglPlnStrFlw));
                }
            }
            else
            {
                if (maybeMtlPlnBlk)
                {
                    MayUpdImpCol(bgnRow, bgnCol, hasTabInd);
                    return RetSym(lexer, isR ? RMtlPlnStrBlk : BrMtlPlnStrBlk);
                }

                if (maybeMtlPlnFlw)
                {
                    return RetSym(lexer, isR ? RMtlPlnStrFlw : BrMtlPlnStrFlw);
                }
            }

            return false;
        }

        return !validSymbols[ErrRec];
    }

    // ------------------------------------------------------------ IExternalScanner

    /// <summary>tree_sitter_yaml_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols) => ScanToken(lexer, validSymbols);

    /// <summary>tree_sitter_yaml_external_scanner_serialize (serialize in scanner.c)</summary>
    public uint Serialize(Span<byte> buffer)
    {
        int size = 0;
        WriteInt16(buffer, ref size, _row);
        WriteInt16(buffer, ref size, _col);
        WriteInt16(buffer, ref size, _blkImpRow);
        WriteInt16(buffer, ref size, _blkImpCol);
        WriteInt16(buffer, ref size, _blkImpTab);
        // The root indentation (index 0) is not written. Each pair takes four bytes: with more than
        // about 250 levels the last pair goes past the 1024 bytes, which the C code does not check
        // either (it is a buffer overrun there; here it is an IndexOutOfRangeException).
        for (int i = 1; i < _indTypStk.Count && size < SerializationBufferSize; i++)
        {
            WriteInt16(buffer, ref size, _indTypStk[i]);
            WriteInt16(buffer, ref size, _indLenStk[i]);
        }

        return (uint)size;
    }

    /// <summary>tree_sitter_yaml_external_scanner_deserialize (deserialize in scanner.c)</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        _row = 0;
        _col = 0;
        _blkImpRow = -1;
        _blkImpCol = -1;
        _blkImpTab = 0;
        _indTypStk.Clear();
        _indTypStk.Add(IndRot);
        _indLenStk.Clear();
        _indLenStk.Add(-1);
        if (buffer.Length > 0)
        {
            int size = 0;
            _row = ReadInt16(buffer, ref size);
            _col = ReadInt16(buffer, ref size);
            _blkImpRow = ReadInt16(buffer, ref size);
            _blkImpCol = ReadInt16(buffer, ref size);
            _blkImpTab = ReadInt16(buffer, ref size);
            while (size < buffer.Length)
            {
                _indTypStk.Add(ReadInt16(buffer, ref size));
                _indLenStk.Add(ReadInt16(buffer, ref size));
            }

            System.Diagnostics.Debug.Assert(size == buffer.Length);
        }
    }

    private static void WriteInt16(Span<byte> buffer, ref int size, short value)
    {
        buffer[size] = (byte)value;
        buffer[size + 1] = (byte)(value >> 8);
        size += 2;
    }

    private static short ReadInt16(ReadOnlySpan<byte> buffer, ref int size)
    {
        short value = (short)(buffer[size] | (buffer[size + 1] << 8));
        size += 2;
        return value;
    }
}
