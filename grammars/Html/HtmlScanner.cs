// Port of src/scanner.c and src/tag.h of tree-sitter-html (commit 73a3947324f6efddf9e17c0ea58d454843590cc0,
// grammars/Html/upstream/src/scanner.c and grammars/Html/upstream/src/tag.h), the external
// scanner of the HTML grammar.
// - Scanner is this class; create is the constructor, destroy is not needed.
// - Array(Tag) tags is List<Tag>; Tag.custom_tag_name (String = Array(char)) is List<byte>.
// - The serialized state is the same bytes as the C scanner writes (little-endian).
// - iswspace/iswalnum of the C locale: the six ASCII space characters, ASCII letters and
//   digits (as in Query.cs); towupper of the C locale maps only 'a'..'z' to 'A'..'Z'.


namespace TreeSitter.Grammars.Html;

/// <summary>The external scanner of the HTML grammar (tags and raw text).</summary>
internal sealed class HtmlScanner : IExternalScanner
{
    // enum TokenType
    private const int StartTagName = 0;
    private const int ScriptStartTagName = 1;
    private const int StyleStartTagName = 2;
    private const int EndTagName = 3;
    private const int ErroneousEndTagName = 4;
    private const int SelfClosingTagDelimiter = 5;
    private const int ImplicitEndTag = 6;
    private const int RawText = 7;
    private const int Comment = 8;

    // TREE_SITTER_SERIALIZATION_BUFFER_SIZE
    private const int SerializationBufferSize = 1024;

    private readonly List<Tag> _tags = new List<Tag>();

    /// <summary>tree_sitter_html_external_scanner_create</summary>
    public HtmlScanner()
    {
    }

    // enum TagType (tag.h)
    private enum TagType
    {
        Area,
        Base,
        Basefont,
        Bgsound,
        Br,
        Col,
        Command,
        Embed,
        Frame,
        Hr,
        Image,
        Img,
        Input,
        Isindex,
        Keygen,
        Link,
        Menuitem,
        Meta,
        Nextid,
        Param,
        Source,
        Track,
        Wbr,
        EndOfVoidTags,

        A,
        Abbr,
        Address,
        Article,
        Aside,
        Audio,
        B,
        Bdi,
        Bdo,
        Blockquote,
        Body,
        Button,
        Canvas,
        Caption,
        Cite,
        Code,
        Colgroup,
        Data,
        Datalist,
        Dd,
        Del,
        Details,
        Dfn,
        Dialog,
        Div,
        Dl,
        Dt,
        Em,
        Fieldset,
        Figcaption,
        Figure,
        Footer,
        Form,
        H1,
        H2,
        H3,
        H4,
        H5,
        H6,
        Head,
        Header,
        Hgroup,
        Html,
        I,
        Iframe,
        Ins,
        Kbd,
        Label,
        Legend,
        Li,
        Main,
        Map,
        Mark,
        Math,
        Menu,
        Meter,
        Nav,
        Noscript,
        Object,
        Ol,
        Optgroup,
        Option,
        Output,
        P,
        Picture,
        Pre,
        Progress,
        Q,
        Rb,
        Rp,
        Rt,
        Rtc,
        Ruby,
        S,
        Samp,
        Script,
        Section,
        Select,
        Slot,
        Small,
        Span,
        Strong,
        Style,
        Sub,
        Summary,
        Sup,
        Svg,
        Table,
        Tbody,
        Td,
        Template,
        Textarea,
        Tfoot,
        Th,
        Thead,
        Time,
        Title,
        Tr,
        U,
        Ul,
        Var,
        Video,

        Custom,

        End,
    }

    // typedef Array(char) String; typedef struct { TagType type; String custom_tag_name; } Tag;
    // The class copies share custom_tag_name, as the C struct copies share the contents.
    private sealed class Tag
    {
        public TagType Type;

        public List<byte> CustomTagName = new List<byte>();
    }

    private readonly record struct TagMapEntry(string TagName, TagType TagType);

    // static const TagMapEntry TAG_TYPES_BY_TAG_NAME[126]
    private static readonly TagMapEntry[] TagTypesByTagName =
    [
        new TagMapEntry("AREA", TagType.Area),
        new TagMapEntry("BASE", TagType.Base),
        new TagMapEntry("BASEFONT", TagType.Basefont),
        new TagMapEntry("BGSOUND", TagType.Bgsound),
        new TagMapEntry("BR", TagType.Br),
        new TagMapEntry("COL", TagType.Col),
        new TagMapEntry("COMMAND", TagType.Command),
        new TagMapEntry("EMBED", TagType.Embed),
        new TagMapEntry("FRAME", TagType.Frame),
        new TagMapEntry("HR", TagType.Hr),
        new TagMapEntry("IMAGE", TagType.Image),
        new TagMapEntry("IMG", TagType.Img),
        new TagMapEntry("INPUT", TagType.Input),
        new TagMapEntry("ISINDEX", TagType.Isindex),
        new TagMapEntry("KEYGEN", TagType.Keygen),
        new TagMapEntry("LINK", TagType.Link),
        new TagMapEntry("MENUITEM", TagType.Menuitem),
        new TagMapEntry("META", TagType.Meta),
        new TagMapEntry("NEXTID", TagType.Nextid),
        new TagMapEntry("PARAM", TagType.Param),
        new TagMapEntry("SOURCE", TagType.Source),
        new TagMapEntry("TRACK", TagType.Track),
        new TagMapEntry("WBR", TagType.Wbr),
        new TagMapEntry("A", TagType.A),
        new TagMapEntry("ABBR", TagType.Abbr),
        new TagMapEntry("ADDRESS", TagType.Address),
        new TagMapEntry("ARTICLE", TagType.Article),
        new TagMapEntry("ASIDE", TagType.Aside),
        new TagMapEntry("AUDIO", TagType.Audio),
        new TagMapEntry("B", TagType.B),
        new TagMapEntry("BDI", TagType.Bdi),
        new TagMapEntry("BDO", TagType.Bdo),
        new TagMapEntry("BLOCKQUOTE", TagType.Blockquote),
        new TagMapEntry("BODY", TagType.Body),
        new TagMapEntry("BUTTON", TagType.Button),
        new TagMapEntry("CANVAS", TagType.Canvas),
        new TagMapEntry("CAPTION", TagType.Caption),
        new TagMapEntry("CITE", TagType.Cite),
        new TagMapEntry("CODE", TagType.Code),
        new TagMapEntry("COLGROUP", TagType.Colgroup),
        new TagMapEntry("DATA", TagType.Data),
        new TagMapEntry("DATALIST", TagType.Datalist),
        new TagMapEntry("DD", TagType.Dd),
        new TagMapEntry("DEL", TagType.Del),
        new TagMapEntry("DETAILS", TagType.Details),
        new TagMapEntry("DFN", TagType.Dfn),
        new TagMapEntry("DIALOG", TagType.Dialog),
        new TagMapEntry("DIV", TagType.Div),
        new TagMapEntry("DL", TagType.Dl),
        new TagMapEntry("DT", TagType.Dt),
        new TagMapEntry("EM", TagType.Em),
        new TagMapEntry("FIELDSET", TagType.Fieldset),
        new TagMapEntry("FIGCAPTION", TagType.Figcaption),
        new TagMapEntry("FIGURE", TagType.Figure),
        new TagMapEntry("FOOTER", TagType.Footer),
        new TagMapEntry("FORM", TagType.Form),
        new TagMapEntry("H1", TagType.H1),
        new TagMapEntry("H2", TagType.H2),
        new TagMapEntry("H3", TagType.H3),
        new TagMapEntry("H4", TagType.H4),
        new TagMapEntry("H5", TagType.H5),
        new TagMapEntry("H6", TagType.H6),
        new TagMapEntry("HEAD", TagType.Head),
        new TagMapEntry("HEADER", TagType.Header),
        new TagMapEntry("HGROUP", TagType.Hgroup),
        new TagMapEntry("HTML", TagType.Html),
        new TagMapEntry("I", TagType.I),
        new TagMapEntry("IFRAME", TagType.Iframe),
        new TagMapEntry("INS", TagType.Ins),
        new TagMapEntry("KBD", TagType.Kbd),
        new TagMapEntry("LABEL", TagType.Label),
        new TagMapEntry("LEGEND", TagType.Legend),
        new TagMapEntry("LI", TagType.Li),
        new TagMapEntry("MAIN", TagType.Main),
        new TagMapEntry("MAP", TagType.Map),
        new TagMapEntry("MARK", TagType.Mark),
        new TagMapEntry("MATH", TagType.Math),
        new TagMapEntry("MENU", TagType.Menu),
        new TagMapEntry("METER", TagType.Meter),
        new TagMapEntry("NAV", TagType.Nav),
        new TagMapEntry("NOSCRIPT", TagType.Noscript),
        new TagMapEntry("OBJECT", TagType.Object),
        new TagMapEntry("OL", TagType.Ol),
        new TagMapEntry("OPTGROUP", TagType.Optgroup),
        new TagMapEntry("OPTION", TagType.Option),
        new TagMapEntry("OUTPUT", TagType.Output),
        new TagMapEntry("P", TagType.P),
        new TagMapEntry("PICTURE", TagType.Picture),
        new TagMapEntry("PRE", TagType.Pre),
        new TagMapEntry("PROGRESS", TagType.Progress),
        new TagMapEntry("Q", TagType.Q),
        new TagMapEntry("RB", TagType.Rb),
        new TagMapEntry("RP", TagType.Rp),
        new TagMapEntry("RT", TagType.Rt),
        new TagMapEntry("RTC", TagType.Rtc),
        new TagMapEntry("RUBY", TagType.Ruby),
        new TagMapEntry("S", TagType.S),
        new TagMapEntry("SAMP", TagType.Samp),
        new TagMapEntry("SCRIPT", TagType.Script),
        new TagMapEntry("SECTION", TagType.Section),
        new TagMapEntry("SELECT", TagType.Select),
        new TagMapEntry("SLOT", TagType.Slot),
        new TagMapEntry("SMALL", TagType.Small),
        new TagMapEntry("SPAN", TagType.Span),
        new TagMapEntry("STRONG", TagType.Strong),
        new TagMapEntry("STYLE", TagType.Style),
        new TagMapEntry("SUB", TagType.Sub),
        new TagMapEntry("SUMMARY", TagType.Summary),
        new TagMapEntry("SUP", TagType.Sup),
        new TagMapEntry("SVG", TagType.Svg),
        new TagMapEntry("TABLE", TagType.Table),
        new TagMapEntry("TBODY", TagType.Tbody),
        new TagMapEntry("TD", TagType.Td),
        new TagMapEntry("TEMPLATE", TagType.Template),
        new TagMapEntry("TEXTAREA", TagType.Textarea),
        new TagMapEntry("TFOOT", TagType.Tfoot),
        new TagMapEntry("TH", TagType.Th),
        new TagMapEntry("THEAD", TagType.Thead),
        new TagMapEntry("TIME", TagType.Time),
        new TagMapEntry("TITLE", TagType.Title),
        new TagMapEntry("TR", TagType.Tr),
        new TagMapEntry("U", TagType.U),
        new TagMapEntry("UL", TagType.Ul),
        new TagMapEntry("VAR", TagType.Var),
        new TagMapEntry("VIDEO", TagType.Video),
        new TagMapEntry("CUSTOM", TagType.Custom),
    ];

    // static const TagType TAG_TYPES_NOT_ALLOWED_IN_PARAGRAPHS[]
    private static readonly TagType[] TagTypesNotAllowedInParagraphs =
    [
        TagType.Address, TagType.Article, TagType.Aside, TagType.Blockquote, TagType.Details, TagType.Div, TagType.Dl,
        TagType.Fieldset, TagType.Figcaption, TagType.Figure, TagType.Footer, TagType.Form, TagType.H1, TagType.H2,
        TagType.H3, TagType.H4, TagType.H5, TagType.H6, TagType.Header, TagType.Hr, TagType.Main,
        TagType.Nav, TagType.Ol, TagType.P, TagType.Pre, TagType.Section,
    ];

    // tag_type_for_name
    private static TagType TagTypeForName(List<byte> tagName)
    {
        for (int i = 0; i < 126; i++)
        {
            TagMapEntry entry = TagTypesByTagName[i];
            if (
                entry.TagName.Length == tagName.Count &&
                NameEquals(tagName, entry.TagName)
            )
            {
                return entry.TagType;
            }
        }

        return TagType.Custom;
    }

    // strlen(entry->tag_name) == tag_name->size is checked by the caller; memcmp over the name.
    private static bool NameEquals(List<byte> tagName, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (tagName[i] != name[i])
            {
                return false;
            }
        }

        return true;
    }

    // tag_new
    private static Tag TagNew()
    {
        Tag tag = new Tag();
        tag.Type = TagType.End;
        tag.CustomTagName = new List<byte>();
        return tag;
    }

    // tag_for_name; the name is moved into the tag for CUSTOM (tag_free/array_delete are
    // not needed: the memory is garbage collected).
    private static Tag TagForName(List<byte> name)
    {
        Tag tag = TagNew();
        tag.Type = TagTypeForName(name);
        if (tag.Type == TagType.Custom)
        {
            tag.CustomTagName = name;
        }

        return tag;
    }

    // tag_is_void
    private static bool TagIsVoid(Tag self) => self.Type < TagType.EndOfVoidTags;

    // tag_eq
    private static bool TagEq(Tag self, Tag other)
    {
        if (self.Type != other.Type)
        {
            return false;
        }

        if (self.Type == TagType.Custom)
        {
            if (self.CustomTagName.Count != other.CustomTagName.Count)
            {
                return false;
            }

            for (int i = 0; i < self.CustomTagName.Count; i++)
            {
                if (self.CustomTagName[i] != other.CustomTagName[i])
                {
                    return false;
                }
            }
        }

        return true;
    }

    // tag_can_contain
    private static bool TagCanContain(Tag self, Tag other)
    {
        TagType child = other.Type;

        switch (self.Type)
        {
            case TagType.Li:
                return child != TagType.Li;

            case TagType.Dt:
            case TagType.Dd:
                return child != TagType.Dt && child != TagType.Dd;

            case TagType.P:
                for (int i = 0; i < 26; i++)
                {
                    if (child == TagTypesNotAllowedInParagraphs[i])
                    {
                        return false;
                    }
                }

                return true;

            case TagType.Colgroup:
                return child == TagType.Col;

            case TagType.Rb:
            case TagType.Rt:
            case TagType.Rp:
                return child != TagType.Rb && child != TagType.Rt && child != TagType.Rp;

            case TagType.Optgroup:
                return child != TagType.Optgroup;

            case TagType.Tr:
                return child != TagType.Tr;

            case TagType.Td:
            case TagType.Th:
                return child != TagType.Td && child != TagType.Th && child != TagType.Tr;

            default:
                return true;
        }
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    // iswspace of the C locale.
    private static bool IsWSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // iswalnum of the C locale.
    private static bool IsWAlNum(int c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    // towupper of the C locale: only 'a'..'z' map to 'A'..'Z', every other character is
    // unchanged (even for characters outside ASCII).
    private static int ToWUpper(int c) => c >= 'a' && c <= 'z' ? c - 'a' + 'A' : c;

    /// <summary>tree_sitter_html_external_scanner_serialize</summary>
    public uint Serialize(Span<byte> buffer)
    {
        ushort tagCount = _tags.Count > ushort.MaxValue ? ushort.MaxValue : (ushort)_tags.Count;
        ushort serializedTagCount = 0;

        int size = sizeof(ushort);
        buffer[size] = (byte)(tagCount & 0xFF);
        buffer[size + 1] = (byte)((tagCount >> 8) & 0xFF);
        size += sizeof(ushort);

        for (; serializedTagCount < tagCount; serializedTagCount++)
        {
            Tag tag = _tags[serializedTagCount];
            if (tag.Type == TagType.Custom)
            {
                int nameLength = tag.CustomTagName.Count;
                if (nameLength > byte.MaxValue)
                {
                    nameLength = byte.MaxValue;
                }

                if (size + 2 + nameLength >= SerializationBufferSize)
                {
                    break;
                }

                buffer[size++] = (byte)tag.Type;
                buffer[size++] = (byte)nameLength;
                for (int i = 0; i < nameLength; i++)
                {
                    buffer[size + i] = tag.CustomTagName[i];
                }

                size += nameLength;
            }
            else
            {
                if (size + 1 >= SerializationBufferSize)
                {
                    break;
                }

                buffer[size++] = (byte)tag.Type;
            }
        }

        buffer[0] = (byte)(serializedTagCount & 0xFF);
        buffer[1] = (byte)((serializedTagCount >> 8) & 0xFF);
        return (uint)size;
    }

    /// <summary>tree_sitter_html_external_scanner_deserialize</summary>
    public void Deserialize(ReadOnlySpan<byte> buffer)
    {
        // tag_free of every tag is not needed: the memory is garbage collected.
        _tags.Clear();

        if (buffer.Length > 0)
        {
            int size = 0;
            ushort tagCount = 0;
            ushort serializedTagCount = 0;

            serializedTagCount = (ushort)(buffer[size] | (buffer[size + 1] << 8));
            size += sizeof(ushort);

            tagCount = (ushort)(buffer[size] | (buffer[size + 1] << 8));
            size += sizeof(ushort);

            // array_reserve(&scanner->tags, tag_count) is not needed: the list grows as needed.
            if (tagCount > 0)
            {
                int iter = 0;
                for (iter = 0; iter < serializedTagCount; iter++)
                {
                    Tag tag = TagNew();
                    tag.Type = (TagType)buffer[size++];
                    if (tag.Type == TagType.Custom)
                    {
                        int nameLength = buffer[size++];
                        for (int i = 0; i < nameLength; i++)
                        {
                            tag.CustomTagName.Add(buffer[size + i]);
                        }

                        size += nameLength;
                    }

                    _tags.Add(tag);
                }

                // add zero tags if we didn't read enough, this is because the
                // buffer had no more room but we held more tags.
                for (; iter < tagCount; iter++)
                {
                    _tags.Add(TagNew());
                }
            }
        }
    }

    // scan_tag_name. towupper(...) is pushed into Array(char): only its low byte is kept,
    // as in C.
    private static List<byte> ScanTagName(Lexer lexer)
    {
        List<byte> tagName = new List<byte>();
        while (IsWAlNum(lexer.Lookahead) || lexer.Lookahead == '-' || lexer.Lookahead == ':')
        {
            tagName.Add((byte)ToWUpper(lexer.Lookahead));
            Advance(lexer);
        }

        return tagName;
    }

    // scan_comment
    private static bool ScanComment(Lexer lexer)
    {
        if (lexer.Lookahead != '-')
        {
            return false;
        }

        Advance(lexer);
        if (lexer.Lookahead != '-')
        {
            return false;
        }

        Advance(lexer);

        int dashes = 0;
        while (lexer.Lookahead != 0)
        {
            switch (lexer.Lookahead)
            {
                case '-':
                    ++dashes;
                    break;
                case '>':
                    if (dashes >= 2)
                    {
                        lexer.ResultSymbol = Comment;
                        Advance(lexer);
                        lexer.MarkEnd();
                        return true;
                    }

                    goto default;
                default:
                    dashes = 0;
                    break;
            }

            Advance(lexer);
        }

        return false;
    }

    // scan_raw_text
    private bool ScanRawText(Lexer lexer)
    {
        if (_tags.Count == 0)
        {
            return false;
        }

        lexer.MarkEnd();

        ReadOnlySpan<byte> endDelimiter = _tags[^1].Type == TagType.Script ? "</SCRIPT"u8 : "</STYLE"u8;

        int delimiterIndex = 0;
        while (lexer.Lookahead != 0)
        {
            if (ToWUpper(lexer.Lookahead) == endDelimiter[delimiterIndex])
            {
                delimiterIndex++;
                if (delimiterIndex == endDelimiter.Length)
                {
                    break;
                }

                Advance(lexer);
            }
            else
            {
                delimiterIndex = 0;
                Advance(lexer);
                lexer.MarkEnd();
            }
        }

        lexer.ResultSymbol = RawText;
        return true;
    }

    // pop_tag; tag_free is not needed: the memory is garbage collected.
    private void PopTag()
    {
        _tags.RemoveAt(_tags.Count - 1);
    }

    // scan_implicit_end_tag
    private bool ScanImplicitEndTag(Lexer lexer)
    {
        Tag? parent = _tags.Count == 0 ? null : _tags[^1];
        bool isClosingTag = false;
        if (lexer.Lookahead == '/')
        {
            isClosingTag = true;
            Advance(lexer);
        }
        else
        {
            if (parent is not null && TagIsVoid(parent))
            {
                PopTag();
                lexer.ResultSymbol = ImplicitEndTag;
                return true;
            }
        }

        List<byte> tagName = ScanTagName(lexer);
        if (tagName.Count == 0 && !lexer.Eof())
        {
            // array_delete(&tag_name) is not needed: the memory is garbage collected.
            return false;
        }

        Tag nextTag = TagForName(tagName);

        if (isClosingTag)
        {
            // The tag correctly closes the topmost element on the stack
            if (_tags.Count > 0 && TagEq(_tags[^1], nextTag))
            {
                return false;
            }

            // Otherwise, dig deeper and queue implicit end tags (to be nice in
            // the case of malformed HTML)
            for (int i = _tags.Count; i > 0; i--)
            {
                if (_tags[i - 1].Type == nextTag.Type)
                {
                    PopTag();
                    lexer.ResultSymbol = ImplicitEndTag;
                    return true;
                }
            }
        }
        else if (
            parent is not null &&
            (
                !TagCanContain(parent, nextTag) ||
                ((parent.Type == TagType.Html || parent.Type == TagType.Head || parent.Type == TagType.Body) && lexer.Eof())
            )
        )
        {
            PopTag();
            lexer.ResultSymbol = ImplicitEndTag;
            return true;
        }

        return false;
    }

    // scan_start_tag_name
    private bool ScanStartTagName(Lexer lexer)
    {
        List<byte> tagName = ScanTagName(lexer);
        if (tagName.Count == 0)
        {
            return false;
        }

        Tag tag = TagForName(tagName);
        _tags.Add(tag);
        switch (tag.Type)
        {
            case TagType.Script:
                lexer.ResultSymbol = ScriptStartTagName;
                break;
            case TagType.Style:
                lexer.ResultSymbol = StyleStartTagName;
                break;
            default:
                lexer.ResultSymbol = StartTagName;
                break;
        }

        return true;
    }

    // scan_end_tag_name
    private bool ScanEndTagName(Lexer lexer)
    {
        List<byte> tagName = ScanTagName(lexer);

        if (tagName.Count == 0)
        {
            return false;
        }

        Tag tag = TagForName(tagName);
        if (_tags.Count > 0 && TagEq(_tags[^1], tag))
        {
            PopTag();
            lexer.ResultSymbol = EndTagName;
        }
        else
        {
            lexer.ResultSymbol = ErroneousEndTagName;
        }

        return true;
    }

    // scan_self_closing_tag_delimiter
    private bool ScanSelfClosingTagDelimiter(Lexer lexer)
    {
        Advance(lexer);
        if (lexer.Lookahead == '>')
        {
            Advance(lexer);
            if (_tags.Count > 0)
            {
                PopTag();
                lexer.ResultSymbol = SelfClosingTagDelimiter;
            }

            return true;
        }

        return false;
    }

    // scan
    /// <summary>tree_sitter_html_external_scanner_scan</summary>
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[RawText] && !validSymbols[StartTagName] && !validSymbols[EndTagName])
        {
            return ScanRawText(lexer);
        }

        while (IsWSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        switch (lexer.Lookahead)
        {
            case '<':
                lexer.MarkEnd();
                Advance(lexer);

                if (lexer.Lookahead == '!')
                {
                    Advance(lexer);
                    return ScanComment(lexer);
                }

                if (validSymbols[ImplicitEndTag])
                {
                    return ScanImplicitEndTag(lexer);
                }

                break;

            case '\0':
                if (validSymbols[ImplicitEndTag])
                {
                    return ScanImplicitEndTag(lexer);
                }

                break;

            case '/':
                if (validSymbols[SelfClosingTagDelimiter])
                {
                    return ScanSelfClosingTagDelimiter(lexer);
                }

                break;

            default:
                if ((validSymbols[StartTagName] || validSymbols[EndTagName]) && !validSymbols[RawText])
                {
                    return validSymbols[StartTagName] ? ScanStartTagName(lexer) : ScanEndTagName(lexer);
                }

                break;
        }

        return false;
    }
}
