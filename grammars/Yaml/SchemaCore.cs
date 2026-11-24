// Port of src/schema.core.c of tree-sitter-yaml (commit a1c4812a73ec5e089de8e441fdea3a921e8d5079,
// grammars/Yaml/upstream/src/schema.core.c), the core schema the YAML external scanner includes
// by default (YAML_SCHEMA core; the json and legacy schemas are not ported).
// - The state machine of adv_sch_stt is the same; the C pointer ResultSchema *rlt_sch is a ref parameter.
// - HAS_TIMESTAMP is 0 in the core schema: the timestamp branch of the scanner is not compiled.
// - Case 34 of the C switch calls abort(); no transition returns 34, so it is unreachable.

namespace TreeSitter.Grammars.Yaml;

/// <summary>ResultSchema (schema.core.c): the scalar type of the plain scalar being scanned.</summary>
internal enum ResultSchema
{
    RsStr,
    RsInt,
    RsNull,
    RsBool,
    RsFloat,
}

/// <summary>The core schema of the YAML external scanner (schema.core.c).</summary>
internal static class SchemaCore
{
    /// <summary>SCH_STT_FRZ: the schema state is frozen (the scalar is a string).</summary>
    internal const sbyte SchSttFrz = -1;

    /// <summary>HAS_TIMESTAMP</summary>
    internal const bool HasTimestamp = false;

    /// <summary>adv_sch_stt</summary>
    internal static sbyte AdvSchStt(sbyte schStt, int curChr, ref ResultSchema rltSch)
    {
        switch (schStt)
        {
            case SchSttFrz:
                break;
            case 0:
                if (curChr == '.') { rltSch = ResultSchema.RsStr; return 6; }
                if (curChr == '0') { rltSch = ResultSchema.RsInt; return 37; }
                if (curChr == 'F') { rltSch = ResultSchema.RsStr; return 2; }
                if (curChr == 'N') { rltSch = ResultSchema.RsStr; return 16; }
                if (curChr == 'T') { rltSch = ResultSchema.RsStr; return 13; }
                if (curChr == 'f') { rltSch = ResultSchema.RsStr; return 17; }
                if (curChr == 'n') { rltSch = ResultSchema.RsStr; return 29; }
                if (curChr == 't') { rltSch = ResultSchema.RsStr; return 26; }
                if (curChr == '~') { rltSch = ResultSchema.RsNull; return 35; }
                if (curChr == '+') { rltSch = ResultSchema.RsStr; return 1; }
                if (curChr == '-') { rltSch = ResultSchema.RsStr; return 1; }
                if ('1' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsInt; return 38; }
                break;
            case 1:
                if (curChr == '.') { rltSch = ResultSchema.RsStr; return 7; }
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsInt; return 38; }
                break;
            case 2:
                if (curChr == 'A') { rltSch = ResultSchema.RsStr; return 9; }
                if (curChr == 'a') { rltSch = ResultSchema.RsStr; return 22; }
                break;
            case 3:
                if (curChr == 'A') { rltSch = ResultSchema.RsStr; return 12; }
                if (curChr == 'a') { rltSch = ResultSchema.RsStr; return 12; }
                break;
            case 4:
                if (curChr == 'E') { rltSch = ResultSchema.RsBool; return 36; }
                break;
            case 5:
                if (curChr == 'F') { rltSch = ResultSchema.RsFloat; return 41; }
                break;
            case 6:
                if (curChr == 'I') { rltSch = ResultSchema.RsStr; return 11; }
                if (curChr == 'N') { rltSch = ResultSchema.RsStr; return 3; }
                if (curChr == 'i') { rltSch = ResultSchema.RsStr; return 24; }
                if (curChr == 'n') { rltSch = ResultSchema.RsStr; return 18; }
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsFloat; return 42; }
                break;
            case 7:
                if (curChr == 'I') { rltSch = ResultSchema.RsStr; return 11; }
                if (curChr == 'i') { rltSch = ResultSchema.RsStr; return 24; }
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsFloat; return 42; }
                break;
            case 8:
                if (curChr == 'L') { rltSch = ResultSchema.RsNull; return 35; }
                break;
            case 9:
                if (curChr == 'L') { rltSch = ResultSchema.RsStr; return 14; }
                break;
            case 10:
                if (curChr == 'L') { rltSch = ResultSchema.RsStr; return 8; }
                break;
            case 11:
                if (curChr == 'N') { rltSch = ResultSchema.RsStr; return 5; }
                if (curChr == 'n') { rltSch = ResultSchema.RsStr; return 20; }
                break;
            case 12:
                if (curChr == 'N') { rltSch = ResultSchema.RsFloat; return 41; }
                break;
            case 13:
                if (curChr == 'R') { rltSch = ResultSchema.RsStr; return 15; }
                if (curChr == 'r') { rltSch = ResultSchema.RsStr; return 28; }
                break;
            case 14:
                if (curChr == 'S') { rltSch = ResultSchema.RsStr; return 4; }
                break;
            case 15:
                if (curChr == 'U') { rltSch = ResultSchema.RsStr; return 4; }
                break;
            case 16:
                if (curChr == 'U') { rltSch = ResultSchema.RsStr; return 10; }
                if (curChr == 'u') { rltSch = ResultSchema.RsStr; return 23; }
                break;
            case 17:
                if (curChr == 'a') { rltSch = ResultSchema.RsStr; return 22; }
                break;
            case 18:
                if (curChr == 'a') { rltSch = ResultSchema.RsStr; return 25; }
                break;
            case 19:
                if (curChr == 'e') { rltSch = ResultSchema.RsBool; return 36; }
                break;
            case 20:
                if (curChr == 'f') { rltSch = ResultSchema.RsFloat; return 41; }
                break;
            case 21:
                if (curChr == 'l') { rltSch = ResultSchema.RsNull; return 35; }
                break;
            case 22:
                if (curChr == 'l') { rltSch = ResultSchema.RsStr; return 27; }
                break;
            case 23:
                if (curChr == 'l') { rltSch = ResultSchema.RsStr; return 21; }
                break;
            case 24:
                if (curChr == 'n') { rltSch = ResultSchema.RsStr; return 20; }
                break;
            case 25:
                if (curChr == 'n') { rltSch = ResultSchema.RsFloat; return 41; }
                break;
            case 26:
                if (curChr == 'r') { rltSch = ResultSchema.RsStr; return 28; }
                break;
            case 27:
                if (curChr == 's') { rltSch = ResultSchema.RsStr; return 19; }
                break;
            case 28:
                if (curChr == 'u') { rltSch = ResultSchema.RsStr; return 19; }
                break;
            case 29:
                if (curChr == 'u') { rltSch = ResultSchema.RsStr; return 23; }
                break;
            case 30:
                if (curChr == '+' || curChr == '-') { rltSch = ResultSchema.RsStr; return 32; }
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsFloat; return 43; }
                break;
            case 31:
                if ('0' <= curChr && curChr <= '7') { rltSch = ResultSchema.RsInt; return 39; }
                break;
            case 32:
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsFloat; return 43; }
                break;
            case 33:
                if (('0' <= curChr && curChr <= '9') || ('A' <= curChr && curChr <= 'F') || ('a' <= curChr && curChr <= 'f'))
                {
                    rltSch = ResultSchema.RsInt;
                    return 40;
                }
                break;
            case 34:
                throw new InvalidOperationException("adv_sch_stt: state 34 is not reachable (abort in schema.core.c).");
            case 35:
                rltSch = ResultSchema.RsNull;
                break;
            case 36:
                rltSch = ResultSchema.RsBool;
                break;
            case 37:
                rltSch = ResultSchema.RsInt;
                if (curChr == '.') { rltSch = ResultSchema.RsFloat; return 42; }
                if (curChr == 'o') { rltSch = ResultSchema.RsStr; return 31; }
                if (curChr == 'x') { rltSch = ResultSchema.RsStr; return 33; }
                if (curChr == 'E' || curChr == 'e') { rltSch = ResultSchema.RsStr; return 30; }
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsInt; return 38; }
                break;
            case 38:
                rltSch = ResultSchema.RsInt;
                if (curChr == '.') { rltSch = ResultSchema.RsFloat; return 42; }
                if (curChr == 'E' || curChr == 'e') { rltSch = ResultSchema.RsStr; return 30; }
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsInt; return 38; }
                break;
            case 39:
                rltSch = ResultSchema.RsInt;
                if ('0' <= curChr && curChr <= '7') { rltSch = ResultSchema.RsInt; return 39; }
                break;
            case 40:
                rltSch = ResultSchema.RsInt;
                if (('0' <= curChr && curChr <= '9') || ('A' <= curChr && curChr <= 'F') || ('a' <= curChr && curChr <= 'f'))
                {
                    rltSch = ResultSchema.RsInt;
                    return 40;
                }
                break;
            case 41:
                rltSch = ResultSchema.RsFloat;
                break;
            case 42:
                rltSch = ResultSchema.RsFloat;
                if (curChr == 'E' || curChr == 'e') { rltSch = ResultSchema.RsStr; return 30; }
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsFloat; return 42; }
                break;
            case 43:
                rltSch = ResultSchema.RsFloat;
                if ('0' <= curChr && curChr <= '9') { rltSch = ResultSchema.RsFloat; return 43; }
                break;
            default:
                rltSch = ResultSchema.RsStr;
                return SchSttFrz;
        }

        if (curChr != '\r' && curChr != '\n' && curChr != ' ' && curChr != 0)
        {
            rltSch = ResultSchema.RsStr;
        }

        return SchSttFrz;
    }
}
