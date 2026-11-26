using System.Globalization;
using System.Text;
using TreeSitter;
using TreeSitter.Parsing;
using TreeSitter.Subtrees;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// Runs a subtree script (tests/Oracle/FORMATS.md, section "Subtree script") on the managed
/// port and returns its output. Must print exactly what oracle_subtree_script prints.
/// </summary>
internal static class SubtreeScriptRunner
{
    internal static string Run(string script)
    {
        Runner runner = new Runner();
        return runner.Run(script);
    }

    /// <summary>The `dump` of one subtree, with fresh labels (oracle_parse prints the tree so).</summary>
    internal static string Dump(Subtree tree)
    {
        Runner runner = new Runner();
        return runner.DumpTree(tree);
    }

    /// <summary>The interpreter of one script.</summary>
    // The tree is deleted by the script itself (tdelete or the end of Run), as in the C shim.
#pragma warning disable CA1001
    private sealed class Runner
    {
        private const int RegisterCount = 64;
        private const int MaxTokens = 256;
        private const int MaxHexBytes = 4096;
        private const string HexDigits = "0123456789abcdef";

        private readonly Subtree[] _registers = new Subtree[RegisterCount];
        private readonly Node?[] _nodes = new Node?[RegisterCount];
        private readonly SubtreePool _pool = new SubtreePool(32);
        private readonly StringBuilder _output = new StringBuilder();

        /// <summary>The label of every heap node that was printed, keyed by arena handle.</summary>
        private readonly Dictionary<int, int> _labels = new Dictionary<int, int>();

        /// <summary>Handles of nodes not reachable from a register any more. Their slot can be
        /// reused by the next allocation, so they must not be mistaken for the old node.</summary>
        private readonly HashSet<int> _dead = new HashSet<int>();

        private int _nextLabel;
        private Language? _language;
        private Tree? _tree;
        private Range[] _treeRanges = [];

        /// <summary>The stack of the script (snew); null until snew, and after sdelete.</summary>
        private Stack? _stack;

        /// <summary>The most versions that a pop may give (SCRIPT_MAX_POP_COUNT of the oracle).</summary>
        private const uint MaxPopCount = 4096;

        public string Run(string script)
        {
            string[] lines = script.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!RunLine(lines[i], (uint)(i + 1)))
                {
                    break;
                }
            }

            // The stack is released before the pool, as in oracle_subtree_script.
            _stack?.Delete();
            _stack = null;
            _tree?.Dispose();
            _tree = null;
            _pool.Delete();
            return _output.ToString();
        }

        // ------------------------------------------------------------ errors

        private bool Error(uint lineNumber, string message)
        {
            _output.AppendFormat(CultureInfo.InvariantCulture, "error line {0}: {1}\n", lineNumber, message);
            return false;
        }

        private bool Error(uint lineNumber, string format, string argument) =>
            Error(lineNumber, string.Format(CultureInfo.InvariantCulture, format, argument));

        private bool Error(uint lineNumber, string format, uint argument) =>
            Error(lineNumber, string.Format(CultureInfo.InvariantCulture, format, argument));

        // ------------------------------------------------------------ helpers

        private static bool IsSpace(char c) =>
            c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\v' || c == '\f';

        private static List<string> Tokenize(string line)
        {
            List<string> tokens = new List<string>();
            int index = 0;
            while (index < line.Length)
            {
                while (index < line.Length && IsSpace(line[index]))
                {
                    index++;
                }

                if (index >= line.Length || line[index] == '#')
                {
                    break;
                }

                int start = index;
                while (index < line.Length && !IsSpace(line[index]))
                {
                    index++;
                }

                tokens.Add(line.Substring(start, index - start));
            }

            return tokens;
        }

        private static bool TryParseU32(string text, out uint value)
        {
            value = 0;
            if (text.Length == 0)
            {
                return false;
            }

            ulong result = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }

                result = (result * 10) + (ulong)(c - '0');
                if (result > uint.MaxValue)
                {
                    return false;
                }
            }

            value = (uint)result;
            return true;
        }

        private static bool TryParseI32(string text, out int value)
        {
            value = 0;
            if (text.Length == 0)
            {
                return false;
            }

            int index = 0;
            bool negative = false;
            if (text[0] == '-')
            {
                negative = true;
                index = 1;
            }

            if (index >= text.Length)
            {
                return false;
            }

            ulong result = 0;
            for (; index < text.Length; index++)
            {
                char c = text[index];
                if (c < '0' || c > '9')
                {
                    return false;
                }

                result = (result * 10) + (ulong)(c - '0');
                if (result > 2147483648UL)
                {
                    return false;
                }
            }

            if (negative)
            {
                if (result > 2147483648UL)
                {
                    return false;
                }

                value = result == 2147483648UL ? int.MinValue : -(int)result;
            }
            else
            {
                if (result > 2147483647UL)
                {
                    return false;
                }

                value = (int)result;
            }

            return true;
        }

        private static bool TryParseLength(string text, out Length value)
        {
            value = Length.Zero;
            string[] parts = text.Split(':');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!TryParseU32(parts[0], out uint bytes) ||
                !TryParseU32(parts[1], out uint row) ||
                !TryParseU32(parts[2], out uint column))
            {
                return false;
            }

            value = new Length(bytes, new Point(row, column));
            return true;
        }

        private static bool TryParseRegister(string text, out int index)
        {
            index = -1;
            if (text.Length < 2 || text[0] != 'r')
            {
                return false;
            }

            int result = 0;
            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }

                result = (result * 10) + (c - '0');
                if (result >= RegisterCount)
                {
                    return false;
                }
            }

            index = result;
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            return -1;
        }

        private static bool TryParseHex(string text, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (text == "-")
            {
                return true;
            }

            if ((text.Length % 2) != 0 || (text.Length / 2) > MaxHexBytes)
            {
                return false;
            }

            byte[] buffer = new byte[text.Length / 2];
            for (int i = 0; i < buffer.Length; i++)
            {
                int high = HexDigit(text[i * 2]);
                int low = HexDigit(text[(i * 2) + 1]);
                if (high < 0 || low < 0)
                {
                    return false;
                }

                buffer[i] = (byte)((high << 4) | low);
            }

            data = buffer;
            return true;
        }

        /// <summary>The symbol is known by the language when it is in the symbol table or
        /// when it is one of the built-in error symbols.</summary>
        private bool SymbolIsKnown(uint symbol)
        {
            if (symbol == BuiltinSymbols.Error || symbol == BuiltinSymbols.ErrorRepeat)
            {
                return true;
            }

            return symbol < Language().SymbolCount;
        }

        // ------------------------------------------------------------ commands

        private bool RunLine(string line, uint lineNumber)
        {
            List<string> tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                return true;
            }

            string command = tokens[0];
            List<string> arguments = tokens.GetRange(1, tokens.Count - 1);
            if (arguments.Count > MaxTokens)
            {
                return Error(lineNumber, "too many arguments");
            }

            switch (command)
            {
                case "lang":
                    return RunLang(arguments, lineNumber);
                case "leaf":
                    return RunLeaf(arguments, lineNumber);
                case "error":
                    return RunError(arguments, lineNumber);
                case "missing":
                    return RunMissing(arguments, lineNumber);
                case "extstate":
                    return RunExternalState(arguments, lineNumber);
                case "node":
                    return RunNode(arguments, lineNumber, false);
                case "errnode":
                    return RunNode(arguments, lineNumber, true);
                case "dup":
                    return RunDup(arguments, lineNumber);
                case "release":
                    return RunRelease(arguments, lineNumber);
                case "mut":
                    return RunMut(arguments, lineNumber);
                case "clone":
                    return RunClone(arguments, lineNumber);
                case "setextra":
                    return RunSetExtra(arguments, lineNumber);
                case "setsym":
                    return RunSetSymbol(arguments, lineNumber);
                case "summarize":
                    return RunSummarize(arguments, lineNumber);
                case "string":
                    return RunString(arguments, lineNumber);
                case "dump":
                    return RunDump(arguments, lineNumber);
                case "compress":
                    return RunCompress(arguments, lineNumber);
                case "edit":
                    return RunEdit(arguments, lineNumber);
                case "compare":
                    return RunCompare(arguments, lineNumber);
                case "lastext":
                    return RunLastExternalToken(arguments, lineNumber);
                case "tranges":
                    return RunTreeRanges(arguments, lineNumber);
                case "tincluded":
                case "tcopy":
                case "tdelete":
                    return RunTreeCommand(arguments, lineNumber, command);
                case "toffset":
                    return RunTreeOffset(arguments, lineNumber);
                case "tdump":
                    return RunTDump(arguments, lineNumber);
                case "cwalk":
                case "cinternal":
                    return RunCursorWalk(arguments, lineNumber, command);
                case "cdesc":
                case "cbyte":
                case "cpoint":
                case "ccopy":
                    return RunCursorGoal(arguments, lineNumber, command);
                case "tree":
                    return RunTree(arguments, lineNumber);
                case "root":
                    return RunRoot(arguments, lineNumber);
                case "parent":
                    return RunParent(arguments, lineNumber);
                case "child":
                    return RunChild(arguments, lineNumber);
                case "named_child":
                    return RunNamedChild(arguments, lineNumber);
                case "next_sibling":
                    return RunNextSibling(arguments, lineNumber);
                case "prev_sibling":
                    return RunPrevSibling(arguments, lineNumber);
                case "next_named_sibling":
                    return RunNextNamedSibling(arguments, lineNumber);
                case "prev_named_sibling":
                    return RunPrevNamedSibling(arguments, lineNumber);
                case "first_child_for_byte":
                    return RunFirstChildForByte(arguments, lineNumber, false, "first_child_for_byte");
                case "first_named_child_for_byte":
                    return RunFirstChildForByte(arguments, lineNumber, true, "first_named_child_for_byte");
                case "descendant_for_byte_range":
                    return RunDescendantForByteRange(arguments, lineNumber, false, "descendant_for_byte_range");
                case "named_descendant_for_byte_range":
                    return RunDescendantForByteRange(arguments, lineNumber, true, "named_descendant_for_byte_range");
                case "descendant_for_point_range":
                    return RunDescendantForPointRange(arguments, lineNumber, false, "descendant_for_point_range");
                case "named_descendant_for_point_range":
                    return RunDescendantForPointRange(arguments, lineNumber, true, "named_descendant_for_point_range");
                case "start_byte":
                    return RunStartByte(arguments, lineNumber);
                case "start_point":
                    return RunStartPoint(arguments, lineNumber);
                case "end_byte":
                    return RunEndByte(arguments, lineNumber);
                case "end_point":
                    return RunEndPoint(arguments, lineNumber);
                case "type":
                    return RunType(arguments, lineNumber);
                case "symbol":
                    return RunSymbol(arguments, lineNumber);
                case "grammar_type":
                    return RunGrammarType(arguments, lineNumber);
                case "grammar_symbol":
                    return RunGrammarSymbol(arguments, lineNumber);
                case "child_count":
                    return RunChildCount(arguments, lineNumber);
                case "named_child_count":
                    return RunNamedChildCount(arguments, lineNumber);
                case "descendant_count":
                    return RunDescendantCount(arguments, lineNumber);
                case "is_null":
                    return RunIsNull(arguments, lineNumber);
                case "is_named":
                    return RunIsNamed(arguments, lineNumber);
                case "is_missing":
                    return RunIsMissing(arguments, lineNumber);
                case "is_extra":
                    return RunIsExtra(arguments, lineNumber);
                case "has_error":
                    return RunHasError(arguments, lineNumber);
                case "is_error":
                    return RunIsError(arguments, lineNumber);
                case "parse_state":
                    return RunParseState(arguments, lineNumber);
                case "next_parse_state":
                    return RunNextParseState(arguments, lineNumber);
                case "field_name_for_child":
                    return RunFieldNameForChild(arguments, lineNumber);
                case "field_name_for_named_child":
                    return RunFieldNameForNamedChild(arguments, lineNumber);
                case "child_by_field_name":
                    return RunChildByFieldName(arguments, lineNumber);
                case "child_by_field_id":
                    return RunChildByFieldId(arguments, lineNumber);
                default:
                    if (IsStackCommand(command))
                    {
                        return RunStackCommand(command, arguments, lineNumber);
                    }

                    return Error(lineNumber, "unknown command '{0}'", command);
            }
        }

        private bool RunLang(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "lang");
            }

            Language? language = ScriptLanguages.Get(arguments[0]);
            if (language is null)
            {
                return Error(lineNumber, "unknown language '{0}'", arguments[0]);
            }

            _language = language;
            return true;
        }

        private bool RunLeaf(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 9)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "leaf");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int destination))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint symbol))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (!SymbolIsKnown(symbol))
            {
                return Error(lineNumber, "invalid symbol '{0}'", symbol);
            }

            if (!TryParseLength(arguments[2], out Length padding))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[2]);
            }

            if (!TryParseLength(arguments[3], out Length size))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[3]);
            }

            if (!TryParseU32(arguments[4], out uint lookaheadBytes))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[4]);
            }

            if (!TryParseU32(arguments[5], out uint parseState))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[5]);
            }

            if (!TryParseU32(arguments[6], out uint hasExternalTokens))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[6]);
            }

            if (!TryParseU32(arguments[7], out uint dependsOnColumn))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[7]);
            }

            if (!TryParseU32(arguments[8], out uint isKeyword))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[8]);
            }

            _registers[destination] = SubtreeOps.NewLeaf(
                _pool,
                (ushort)symbol,
                padding,
                size,
                lookaheadBytes,
                (ushort)parseState,
                hasExternalTokens != 0,
                dependsOnColumn != 0,
                isKeyword != 0,
                Language());
            return true;
        }

        private bool RunError(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 6)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "error");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int destination))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseI32(arguments[1], out int lookaheadChar))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (!TryParseLength(arguments[2], out Length padding))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[2]);
            }

            if (!TryParseLength(arguments[3], out Length size))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[3]);
            }

            if (!TryParseU32(arguments[4], out uint bytesScanned))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[4]);
            }

            if (!TryParseU32(arguments[5], out uint parseState))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[5]);
            }

            _registers[destination] = SubtreeOps.NewError(
                _pool,
                lookaheadChar,
                padding,
                size,
                bytesScanned,
                (ushort)parseState,
                Language());
            return true;
        }

        private bool RunMissing(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 5)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "missing");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int destination))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint symbol))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (!SymbolIsKnown(symbol))
            {
                return Error(lineNumber, "invalid symbol '{0}'", symbol);
            }

            if (!TryParseU32(arguments[2], out uint parseState))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[2]);
            }

            if (!TryParseLength(arguments[3], out Length padding))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[3]);
            }

            if (!TryParseU32(arguments[4], out uint lookaheadBytes))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[4]);
            }

            _registers[destination] = SubtreeOps.NewMissingLeaf(
                _pool,
                (ushort)symbol,
                (ushort)parseState,
                padding,
                lookaheadBytes,
                Language());
            return true;
        }

        private bool RunExternalState(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "extstate");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            Subtree tree = _registers[index];
            if (tree.IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (tree.IsInline)
            {
                return Error(lineNumber, "register {0} is not a heap subtree", arguments[0]);
            }

            if (!tree.HasExternalTokens)
            {
                return Error(lineNumber, "register {0} has no external tokens", arguments[0]);
            }

            if (tree.ChildCount != 0)
            {
                // The state is stored in the union of the heap data: a node with children
                // would lose its summary.
                return Error(lineNumber, "register {0} is not an external token", arguments[0]);
            }

            if (!TryParseHex(arguments[1], out byte[] data))
            {
                return Error(lineNumber, "invalid hex data '{0}'", arguments[1]);
            }

            {
                ExternalScannerState state = ExternalScannerState.Create(data);
                ref SubtreeHeapData hd = ref SubtreeArena.Get(tree);
                state.WriteTo(ref hd.ExternalScannerState);
            }
            return true;
        }

        private bool RunNode(List<string> arguments, uint lineNumber, bool errorNode)
        {
            string command = errorNode ? "errnode" : "node";
            int minimum = errorNode ? 2 : 3;
            if (arguments.Count < minimum)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", command);
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int destination))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            ushort symbol = BuiltinSymbols.Error;
            uint productionId = 0;
            bool extra = false;
            int firstChild = 0;
            if (errorNode)
            {
                if (!TryParseU32(arguments[1], out uint extraValue))
                {
                    return Error(lineNumber, "invalid number '{0}'", arguments[1]);
                }

                extra = extraValue != 0;
                firstChild = 2;
            }
            else
            {
                if (!TryParseU32(arguments[1], out uint symbolValue))
                {
                    return Error(lineNumber, "invalid number '{0}'", arguments[1]);
                }

                if (!SymbolIsKnown(symbolValue))
                {
                    return Error(lineNumber, "invalid symbol '{0}'", symbolValue);
                }

                if (!TryParseU32(arguments[2], out productionId))
                {
                    return Error(lineNumber, "invalid number '{0}'", arguments[2]);
                }

                if (productionId >= Language().ProductionIdCount)
                {
                    return Error(lineNumber, "invalid production id '{0}'", productionId);
                }

                symbol = (ushort)symbolValue;
                firstChild = 3;
            }

            int[] children = new int[arguments.Count - firstChild];
            TsArray<Subtree> array = new TsArray<Subtree>();
            for (int i = firstChild; i < arguments.Count; i++)
            {
                if (!TryParseRegister(arguments[i], out int index))
                {
                    return Error(lineNumber, "invalid register '{0}'", arguments[i]);
                }

                if (_registers[index].IsNull)
                {
                    return Error(lineNumber, "register {0} is null", arguments[i]);
                }

                children[i - firstChild] = index;
                array.Push(_registers[index]);
            }

            if (errorNode)
            {
                _registers[destination] = SubtreeOps.NewErrorNode(ref array, extra, Language());
            }
            else
            {
                _registers[destination] = SubtreeOps.NewNode(symbol, ref array, productionId, Language());
            }

            // The node owns the children now.
            for (int i = 0; i < children.Length; i++)
            {
                _registers[children[i]] = Subtree.Null;
            }

            return true;
        }

        private bool RunDup(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "dup");
            }

            if (!TryParseRegister(arguments[0], out int destination))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseRegister(arguments[1], out int source))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[1]);
            }

            if (_registers[source].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[1]);
            }

            _registers[destination] = _registers[source];
            SubtreeOps.Retain(_registers[source]);
            return true;
        }

        private bool RunRelease(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "release");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            SubtreeOps.Release(_pool, _registers[index]);
            _registers[index] = Subtree.Null;
            MarkDead();
            return true;
        }

        private bool RunMut(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "mut");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            _registers[index] = SubtreeOps.MakeMut(_pool, _registers[index]);
            return true;
        }

        private bool RunClone(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "clone");
            }

            if (!TryParseRegister(arguments[0], out int destination))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseRegister(arguments[1], out int source))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[1]);
            }

            if (_registers[source].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[1]);
            }

            if (_registers[source].IsInline)
            {
                return Error(lineNumber, "register {0} is not a heap subtree", arguments[1]);
            }

            _registers[destination] = SubtreeOps.Clone(_registers[source]);
            return true;
        }

        private bool RunSetExtra(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "setextra");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint extra))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            Subtree tree = _registers[index];
            SubtreeOps.SetExtra(ref tree, extra != 0);
            _registers[index] = tree;
            return true;
        }

        private bool RunSetSymbol(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "setsym");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint symbol))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (!SymbolIsKnown(symbol))
            {
                return Error(lineNumber, "invalid symbol '{0}'", symbol);
            }

            if (_registers[index].IsInline && symbol >= byte.MaxValue)
            {
                return Error(lineNumber, "symbol {0} does not fit into an inline subtree", symbol);
            }

            Subtree tree = _registers[index];
            SubtreeOps.SetSymbol(ref tree, (ushort)symbol, Language());
            _registers[index] = tree;
            return true;
        }

        private bool RunSummarize(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "summarize");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (_registers[index].IsInline)
            {
                return Error(lineNumber, "register {0} is not a heap subtree", arguments[0]);
            }

            SubtreeOps.SummarizeChildren(_registers[index], Language());
            return true;
        }

        private bool RunString(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 4)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "string");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint aliasSymbol))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (!TryParseU32(arguments[2], out uint aliasIsNamed))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[2]);
            }

            if (!TryParseU32(arguments[3], out uint includeAll))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[3]);
            }

            string text = SubtreeOps.ToSExpression(
                _registers[index],
                (ushort)aliasSymbol,
                aliasIsNamed != 0,
                Language(),
                includeAll != 0);
            _output.Append(text).Append('\n');
            return true;
        }

        private bool RunDump(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "dump");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            Dump(_registers[index], 0);
            return true;
        }

        private bool RunCompress(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "compress");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint count))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            TsArray<Subtree> stack = new TsArray<Subtree>();
            SubtreeOps.Compress(_registers[index], count, Language(), ref stack);
            stack.Delete();
            return true;
        }

        private bool RunEdit(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 4)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "edit");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (!TryParseLength(arguments[1], out Length start))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[1]);
            }

            if (!TryParseLength(arguments[2], out Length oldEnd))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[2]);
            }

            if (!TryParseLength(arguments[3], out Length newEnd))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[3]);
            }

            InputEdit edit = new InputEdit(
                start.Bytes,
                oldEnd.Bytes,
                newEnd.Bytes,
                start.Extent,
                oldEnd.Extent,
                newEnd.Extent);
            _registers[index] = SubtreeOps.Edit(_registers[index], edit, _pool);
            return true;
        }

        private bool RunCompare(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "compare");
            }

            if (!TryParseRegister(arguments[0], out int leftIndex))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[leftIndex].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (!TryParseRegister(arguments[1], out int rightIndex))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[1]);
            }

            if (_registers[rightIndex].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[1]);
            }

            int result = SubtreeOps.Compare(_registers[leftIndex], _registers[rightIndex], _pool);
            _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", result);
            return true;
        }

        private bool RunLastExternalToken(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "lastext");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            Subtree result = SubtreeOps.LastExternalToken(_registers[index]);
            Dump(result, 0);
            return true;
        }

        // ------------------------------------------------------------ tree commands

        private bool RunTree(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "tree");
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", arguments[0]);
            }

            if (_tree is not null)
            {
                _tree.Dispose();
            }

            _tree = new Tree(_registers[index], _language, _treeRanges);
            // Clear all node registers. (Matches C oracle: tree does not populate nodes[].)
            for (int i = 0; i < _nodes.Length; i++) _nodes[i] = null;
            _registers[index] = Subtree.Null;
            return true;
        }

        // ------------------------------------------------------------ tree commands

        private const int TreeMaxRanges = 8;

        // script_parse_range
        private static bool TryParseRange(string text, out Range value)
        {
            value = default;
            int dash = text.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0)
            {
                return false;
            }

            if (!TryParseLength(text[..dash], out Length start) ||
                !TryParseLength(text[(dash + 1)..], out Length end))
            {
                return false;
            }

            value = new Range(start.Extent, end.Extent, start.Bytes, end.Bytes);
            return true;
        }

        // script_print_included_ranges
        private void PrintIncludedRanges(Tree tree)
        {
            Range[] ranges = tree.IncludedRanges();
            _output.AppendFormat(CultureInfo.InvariantCulture, "ranges = {0}\n", ranges.Length);
            foreach (Range range in ranges)
            {
                _output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}-{3}:{4}:{5}\n",
                    range.StartByte,
                    range.StartPoint.Row,
                    range.StartPoint.Column,
                    range.EndByte,
                    range.EndPoint.Row,
                    range.EndPoint.Column);
            }
        }

        private bool RunTreeRanges(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count > TreeMaxRanges)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "tranges");
            }

            Range[] ranges = new Range[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                if (!TryParseRange(arguments[i], out ranges[i]))
                {
                    return Error(lineNumber, "invalid length '{0}'", arguments[i]);
                }
            }

            _treeRanges = ranges;
            return true;
        }

        private bool RunTreeCommand(List<string> arguments, uint lineNumber, string command)
        {
            if (arguments.Count != 0)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", command);
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (command == "tincluded")
            {
                PrintIncludedRanges(_tree);
            }
            else if (command == "tcopy")
            {
                Tree copy = _tree.Copy();
                TDumpNode(copy.RootNode, 0);
                PrintIncludedRanges(copy);
                _output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "same_language = {0}\n",
                    ReferenceEquals(copy.Language, _tree.Language) ? 1 : 0);
                copy.Dispose();
            }
            else
            {
                _tree.Dispose();
                _tree = null;

                // The registers that hold nodes point into the deleted tree.
                for (int i = 0; i < _nodes.Length; i++)
                {
                    if (_nodes[i] is not null)
                    {
                        _registers[i] = Subtree.Null;
                    }

                    _nodes[i] = null;
                }
            }

            return true;
        }

        private bool RunTreeOffset(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "toffset");
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseLength(arguments[1], out Length offset))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[1]);
            }

            SetNodeRegister(index, _tree.RootNodeWithOffset(offset.Bytes, offset.Extent));
            return true;
        }

        // TDumpNode
        private void TDumpNode(Node node, uint depth)
        {
            if (node.IsNull)
            {
                return;
            }

            for (uint i = 0; i < depth; i++)
            {
                _output.Append("  ");
            }

            _output.AppendFormat(
                CultureInfo.InvariantCulture,
                "type={0} start_byte={1} start_point={2}:{3} end_byte={4} end_point={5}:{6}\n",
                node.Type,
                node.StartByte,
                node.StartPoint.Row,
                node.StartPoint.Column,
                node.EndByte,
                node.EndPoint.Row,
                node.EndPoint.Column);

            uint childCount = node.ChildCount;
            for (uint i = 0; i < childCount; i++)
            {
                TDumpNode(node.Child(i), depth + 1);
            }
        }

        private bool RunTDump(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 0)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "tdump");
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            TDumpNode(_tree.RootNode, 0);
            return true;
        }

        // ------------------------------------------------------------ cursor commands

        // CursorLine
        private void CursorLine(TreeCursor cursor)
        {
            Node node = cursor.CurrentNode;
            Node parent = cursor.ParentNode();
            string? fieldName = cursor.CurrentFieldName;
            _output.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0} {1}-{2} depth={3} descendant={4} field={5} field_id={6} parent={7}",
                node.Type,
                node.StartByte,
                node.EndByte,
                cursor.CurrentDepth,
                cursor.CurrentDescendantIndex,
                fieldName ?? "null",
                cursor.CurrentFieldId,
                parent.IsNull ? "null" : parent.Type);
            if (!parent.IsNull)
            {
                _output.AppendFormat(CultureInfo.InvariantCulture, " {0}-{1}", parent.StartByte, parent.EndByte);
            }

            Span<ushort> supertypes = stackalloc ushort[64];
            cursor.CurrentStatus(
                out ushort fieldId,
                out bool hasLaterSiblings,
                out bool hasLaterNamedSiblings,
                out bool canHaveLaterSiblingsWithThisField,
                supertypes,
                out uint supertypeCount);
            _output.AppendFormat(
                CultureInfo.InvariantCulture,
                " status={0},{1},{2},{3}",
                fieldId,
                hasLaterSiblings ? 1 : 0,
                hasLaterNamedSiblings ? 1 : 0,
                canHaveLaterSiblingsWithThisField ? 1 : 0);
            for (int i = 0; i < supertypeCount; i++)
            {
                _output.AppendFormat(CultureInfo.InvariantCulture, ",{0}", supertypes[i]);
            }

            _output.Append('\n');
        }

        // CWalk
        private void CWalk(Tree tree)
        {
            TreeCursor cursor = new TreeCursor(tree.RootNode);
            for (int reverse = 0; reverse < 2; reverse++)
            {
                _output.Append(reverse != 0 ? "reverse\n" : "forward\n");
                bool done = false;
                while (!done)
                {
                    CursorLine(cursor);
                    if (reverse != 0 ? cursor.GotoLastChild() : cursor.GotoFirstChild())
                    {
                        continue;
                    }

                    for (;;)
                    {
                        if (reverse != 0 ? cursor.GotoPreviousSibling() : cursor.GotoNextSibling())
                        {
                            break;
                        }

                        if (!cursor.GotoParent())
                        {
                            done = true;
                            break;
                        }
                    }
                }
            }
        }

        // CInternal
        private void CInternal(Tree tree)
        {
            TreeCursor cursor = new TreeCursor(tree.RootNode);
            for (int reverse = 0; reverse < 2; reverse++)
            {
                _output.Append(reverse != 0 ? "reverse\n" : "forward\n");
                for (;;)
                {
                    TreeCursorStep step = reverse != 0
                        ? cursor.GotoLastChildInternal()
                        : cursor.GotoFirstChildInternal();
                    if (step == TreeCursorStep.None)
                    {
                        bool moved = false;
                        while (cursor.Stack.Size > 1)
                        {
                            step = reverse != 0
                                ? cursor.GotoPreviousSiblingInternal()
                                : cursor.GotoNextSiblingInternal();
                            if (step != TreeCursorStep.None)
                            {
                                moved = true;
                                break;
                            }

                            cursor.Stack.Size--;
                        }

                        if (!moved)
                        {
                            break;
                        }
                    }

                    _output.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "{0} stack={1} ",
                        step == TreeCursorStep.Hidden ? "hidden" : "visible",
                        cursor.Stack.Size);
                    CursorLine(cursor);
                }
            }
        }

        // CFirstChildFor
        private void CFirstChildFor(Tree tree, bool byPoint, uint goalByte, Point goalPoint)
        {
            TreeCursor cursor = new TreeCursor(tree.RootNode);
            for (;;)
            {
                long index = byPoint
                    ? cursor.GotoFirstChildForPoint(goalPoint)
                    : cursor.GotoFirstChildForByte(goalByte);
                _output.AppendFormat(CultureInfo.InvariantCulture, "index={0}\n", index);
                if (index < 0)
                {
                    break;
                }

                CursorLine(cursor);
            }
        }

        // CCopy
        private void CCopy(Tree tree, uint goal)
        {
            Node root = tree.RootNode;
            TreeCursor cursor = new TreeCursor(root);
            cursor.GotoDescendant(goal);
            TreeCursor copy = cursor.Copy();
            CursorLine(copy);

            TreeCursor other = new TreeCursor(root);
            other.GotoFirstChild();
            other.ResetTo(copy);
            _output.AppendFormat(CultureInfo.InvariantCulture, "parent={0}\n", other.GotoParent() ? 1 : 0);
            CursorLine(other);

            copy.Reset(other.CurrentNode);
            CursorLine(copy);
            _output.AppendFormat(CultureInfo.InvariantCulture, "parent={0}\n", copy.GotoParent() ? 1 : 0);
        }

        private bool RunCursorWalk(List<string> arguments, uint lineNumber, string command)
        {
            if (arguments.Count != 0)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", command);
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (command == "cwalk")
            {
                CWalk(_tree);
            }
            else
            {
                CInternal(_tree);
            }

            return true;
        }

        private bool RunCursorGoal(List<string> arguments, uint lineNumber, string command)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", command);
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (command == "cpoint")
            {
                if (!TryParseLength(arguments[0], out Length point))
                {
                    return Error(lineNumber, "invalid length '{0}'", arguments[0]);
                }

                CFirstChildFor(_tree, true, 0, point.Extent);
                return true;
            }

            if (!TryParseU32(arguments[0], out uint value))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[0]);
            }

            if (command == "cbyte")
            {
                CFirstChildFor(_tree, false, value, new Point(0, 0));
            }
            else if (command == "ccopy")
            {
                CCopy(_tree, value);
            }
            else
            {
                TreeCursor cursor = new TreeCursor(_tree.RootNode);
                cursor.GotoDescendant(value);
                CursorLine(cursor);
            }

            return true;
        }

        private bool RunRoot(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "root");
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            SetNodeRegister(index, _tree.RootNode);
            return true;
        }

        /// <summary>Gets the Node for a register, caching it in _nodes. Returns null on error.</summary>
        private Node? GetNodeFromRegister(List<string> arguments, uint lineNumber, out int index)
        {
            index = -1;

            if (_tree is null)
            {
                Error(lineNumber, "no tree created");
                return null;
            }

            if (!TryParseRegister(arguments[0], out index))
            {
                Error(lineNumber, "invalid register '{0}'", arguments[0]);
                return null;
            }

            // Match C oracle: node commands read from nodes[], not registers[].
            // If the cached node is null (e.g. cleared by `tree`), treat it as
            // a null node regardless of what's in the register.
            Node? stored = _nodes[index];
            if (stored is not null)
            {
                return stored.Value;
            }

            return null;
        }

        private void SetNodeRegister(int index, Node node)
        {
            _nodes[index] = node;
            _registers[index] = node.Subtree;
        }

        private void ClearNodeRegister(int index)
        {
            _nodes[index] = null;
            _registers[index] = Subtree.Null;
        }

        private bool RunParent(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "parent");
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            // GetNodeFromRegister returns null (with index set) when the register
            // is null - that is not an error, just a null node. It returns null
            // with index == -1 only when an error was already printed.
            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node parent = node.Parent;
            if (parent.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, parent);
            }

            return true;
        }

        private bool RunChild(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "child");
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint childIndex))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node child = node.Child(childIndex);
            if (child.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, child);
            }

            return true;
        }

        private bool RunNamedChild(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "named_child");
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint childIndex))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node child = node.NamedChild(childIndex);
            if (child.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, child);
            }

            return true;
        }

        private bool RunNextSibling(List<string> arguments, uint lineNumber) => RunSibling(arguments, lineNumber, false, false, "next_sibling");
        private bool RunPrevSibling(List<string> arguments, uint lineNumber) => RunSibling(arguments, lineNumber, true, false, "prev_sibling");
        private bool RunNextNamedSibling(List<string> arguments, uint lineNumber) => RunSibling(arguments, lineNumber, false, true, "next_named_sibling");
        private bool RunPrevNamedSibling(List<string> arguments, uint lineNumber) => RunSibling(arguments, lineNumber, true, true, "prev_named_sibling");

        private bool RunSibling(List<string> arguments, uint lineNumber, bool prev, bool named, string? commandName = null)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node sibling = prev
                ? named ? node.PrevNamedSibling : node.PrevSibling
                : named ? node.NextNamedSibling : node.NextSibling;

            if (sibling.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, sibling);
            }

            return true;
        }

        private bool RunFirstChildForByte(List<string> arguments, uint lineNumber, bool named, string? commandName = null)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint byteValue))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node child = named
                ? node.FirstNamedChildForByte(byteValue)
                : node.FirstChildForByte(byteValue);

            if (child.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, child);
            }

            return true;
        }

        private bool RunDescendantForByteRange(List<string> arguments, uint lineNumber, bool named, string? commandName = null)
        {
            if (arguments.Count != 3)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint start))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }
            if (!TryParseU32(arguments[2], out uint end))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[2]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node descendant = named
                ? node.NamedDescendantForByteRange(start, end)
                : node.DescendantForByteRange(start, end);

            if (descendant.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, descendant);
            }

            return true;
        }

        private bool RunDescendantForPointRange(List<string> arguments, uint lineNumber, bool named, string? commandName = null)
        {
            if (arguments.Count != 3)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseLength(arguments[1], out Length start))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[1]);
            }
            if (!TryParseLength(arguments[2], out Length end))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[2]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node descendant = named
                ? node.NamedDescendantForPointRange(start.Extent, end.Extent)
                : node.DescendantForPointRange(start.Extent, end.Extent);

            if (descendant.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, descendant);
            }

            return true;
        }

        // ------------------------------------------------------------ node property commands

        private bool RunStartByte(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.StartByte), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunStartPoint(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}:{1}\n", n.StartPoint.Row, n.StartPoint.Column), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0:0\n"));
        private bool RunEndByte(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.EndByte), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunEndPoint(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}:{1}\n", n.EndPoint.Row, n.EndPoint.Column), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0:0\n"));
        private bool RunType(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.Type), () => _output.AppendFormat(CultureInfo.InvariantCulture, "null\n"));
        private bool RunSymbol(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.Symbol), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunGrammarType(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.GrammarType), () => _output.AppendFormat(CultureInfo.InvariantCulture, "null\n"));
        private bool RunGrammarSymbol(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.GrammarSymbol), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunChildCount(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.ChildCount), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunNamedChildCount(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.NamedChildCount), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunDescendantCount(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.DescendantCount), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunParseState(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.ParseState), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"));
        private bool RunNextParseState(List<string> arguments, uint lineNumber) => RunNodeProperty(arguments, lineNumber, n => _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", n.NextParseState), () => _output.AppendFormat(CultureInfo.InvariantCulture, "0\n"), "next_parse_state");

        private bool RunNodeProperty(List<string> arguments, uint lineNumber, Action<Node> print, Action printNull, string? commandName = null)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            // Match C oracle: node commands require a tree.
            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            // Match C oracle: node commands read from nodes[], not registers[].
            // If the cached node is null (e.g. cleared by `tree`), treat it as
            // a null node regardless of what's in the register.
            if (_nodes[index] is { } node)
            {
                print(node);
                return true;
            }

            printNull();
            return true;
        }

        private bool RunIsNull(List<string> arguments, uint lineNumber) => RunNodeFlag(arguments, lineNumber, n => n.IsNull, true);
        private bool RunIsNamed(List<string> arguments, uint lineNumber) => RunNodeFlag(arguments, lineNumber, n => n.IsNamed, false);
        private bool RunIsMissing(List<string> arguments, uint lineNumber) => RunNodeFlag(arguments, lineNumber, n => n.IsMissing, false);
        private bool RunIsExtra(List<string> arguments, uint lineNumber) => RunNodeFlag(arguments, lineNumber, n => n.IsExtra, false);
        private bool RunHasError(List<string> arguments, uint lineNumber) => RunNodeFlag(arguments, lineNumber, n => n.HasError, false);
        private bool RunIsError(List<string> arguments, uint lineNumber) => RunNodeFlag(arguments, lineNumber, n => n.IsError, false);

        private bool RunNodeFlag(List<string> arguments, uint lineNumber, Func<Node, bool> get, bool isNullFlag, string? commandName = null)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return false;
            }

            // Match C oracle: node flag commands read from nodes[], not registers[].
            // If the cached node is null (e.g. cleared by `tree`), treat it as
            // a null node regardless of what's in the register.
            // C oracle: is_null returns 1 for null nodes, all other flags return 0.
            if (_nodes[index] is { } node)
            {
                _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", get(node) ? 1 : 0);
                return true;
            }

            _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", isNullFlag ? 1 : 0);
            return true;
        }

        private bool RunFieldNameForChild(List<string> arguments, uint lineNumber) => RunFieldName(arguments, lineNumber, false, "field_name_for_child");
        private bool RunFieldNameForNamedChild(List<string> arguments, uint lineNumber) => RunFieldName(arguments, lineNumber, true, "field_name_for_named_child");

        private bool RunFieldName(List<string> arguments, uint lineNumber, bool named, string? commandName = null)
        {
            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint childIndex))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out int _) is not { } node)
            {
                if (index >= 0)
                {
                    _output.AppendFormat(CultureInfo.InvariantCulture, "null\n");
                    return true;
                }

                return false;
            }

            string? name = named
                ? node.FieldNameForNamedChild(childIndex)
                : node.FieldNameForChild(childIndex);

            if (name is null)
            {
                _output.AppendFormat(CultureInfo.InvariantCulture, "null\n");
            }
            else
            {
                _output.AppendFormat(CultureInfo.InvariantCulture, "{0}\n", name);
            }

            return true;
        }

        private bool RunChildByFieldName(List<string> arguments, uint lineNumber) => RunChildByField(arguments, lineNumber, true, "child_by_field_name");
        private bool RunChildByFieldId(List<string> arguments, uint lineNumber) => RunChildByField(arguments, lineNumber, false, "child_by_field_id");

        private bool RunChildByField(List<string> arguments, uint lineNumber, bool byName, string? commandName = null)
        {
            if (_tree is null)
            {
                return Error(lineNumber, "no tree created");
            }

            if (arguments.Count != 2)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", commandName ?? arguments[0]);
            }

            if (!TryParseRegister(arguments[0], out int index))
            {
                return Error(lineNumber, "invalid register '{0}'", arguments[0]);
            }

            if (GetNodeFromRegister(arguments, lineNumber, out index) is not { } node)
            {
                if (index >= 0)
                {
                    ClearNodeRegister(index);
                    return true;
                }

                return false;
            }

            Node result;
            if (byName)
            {
                result = node.ChildByFieldName(arguments[1]);
            }
            else
            {
                if (!TryParseU32(arguments[1], out uint fieldId))
                {
                    return Error(lineNumber, "invalid number '{0}'", arguments[1]);
                }

                result = node.ChildByFieldId((ushort)fieldId);
            }

            if (result.IsNull)
            {
                ClearNodeRegister(index);
            }
            else
            {
                SetNodeRegister(index, result);
            }

            return true;
        }

        // ------------------------------------------------------------ labels

        /// <summary>The heap nodes that are still reachable from the registers.</summary>
        private static void CollectLive(Subtree tree, HashSet<int> live)
        {
            if (tree.IsNull || tree.IsInline)
            {
                return;
            }

            int handle = tree.Handle;
            if (!live.Add(handle))
            {
                return;
            }

            ReadOnlySpan<Subtree> children = tree.Children;
            for (int i = 0; i < children.Length; i++)
            {
                CollectLive(children[i], live);
            }
        }

        /// <summary>A node that is not reachable from a register any more was freed. Its
        /// memory can be reused by the next allocation, so it must not be mistaken for the
        /// node that was printed before.</summary>
        private void MarkDead()
        {
            HashSet<int> live = new HashSet<int>();
            for (int i = 0; i < RegisterCount; i++)
            {
                CollectLive(_registers[i], live);
            }

            foreach (int handle in _labels.Keys)
            {
                if (!live.Contains(handle))
                {
                    _dead.Add(handle);
                }
            }
        }

        // ------------------------------------------------------------ dump

        internal string DumpTree(Subtree tree)
        {
            Dump(tree, 0);
            return _output.ToString();
        }

        private void Dump(Subtree tree, int indent)
        {
            for (int i = 0; i < indent; i++)
            {
                _output.Append("  ");
            }

            if (tree.IsNull)
            {
                _output.Append("null\n");
                return;
            }

            if (tree.IsInline)
            {
                Length padding = tree.Padding;
                Length size = tree.Size;
                _output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "inline symbol={0} parse_state={1} visible={2} named={3} extra={4} has_changes={5} " +
                    "is_missing={6} is_keyword={7} depends_on_column={8} padding={9}:{10}:{11} " +
                    "size={12}:{13}:{14} lookahead_bytes={15}\n",
                    tree.Symbol,
                    tree.ParseState,
                    tree.Visible ? 1 : 0,
                    tree.Named ? 1 : 0,
                    tree.Extra ? 1 : 0,
                    tree.HasChanges ? 1 : 0,
                    tree.IsMissing ? 1 : 0,
                    tree.IsKeyword ? 1 : 0,
                    tree.DependsOnColumn ? 1 : 0,
                    padding.Bytes,
                    padding.Extent.Row,
                    padding.Extent.Column,
                    size.Bytes,
                    size.Extent.Row,
                    size.Extent.Column,
                    tree.LookaheadBytes);
                return;
            }

            int handle = tree.Handle;
            if (_labels.TryGetValue(handle, out int known) && !_dead.Contains(handle))
            {
                _output.AppendFormat(CultureInfo.InvariantCulture, "@#{0}\n", known);
                return;
            }

            int label = _nextLabel++;
            _labels[handle] = label;
            _dead.Remove(handle);

            ref SubtreeHeapData heap = ref SubtreeArena.Get(tree);
            Length heapPadding = heap.Padding;
            Length heapSize = heap.Size;
            _output.AppendFormat(
                CultureInfo.InvariantCulture,
                "heap #{0} ref_count={1} symbol={2} parse_state={3} visible={4} named={5} extra={6} " +
                "fragile_left={7} fragile_right={8} has_changes={9} has_external_tokens={10} " +
                "has_external_scanner_state_change={11} depends_on_column={12} is_missing={13} " +
                "is_keyword={14} padding={15}:{16}:{17} size={18}:{19}:{20} lookahead_bytes={21} " +
                "error_cost={22} child_count={23} ",
                label,
                heap.RefCount,
                heap.Symbol,
                heap.ParseState,
                heap.HasFlag(SubtreeFlags.Visible) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.Named) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.Extra) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.FragileLeft) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.FragileRight) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.HasChanges) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.HasExternalTokens) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.HasExternalScannerStateChange) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.DependsOnColumn) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.IsMissing) ? 1 : 0,
                heap.HasFlag(SubtreeFlags.IsKeyword) ? 1 : 0,
                heapPadding.Bytes,
                heapPadding.Extent.Row,
                heapPadding.Extent.Column,
                heapSize.Bytes,
                heapSize.Extent.Row,
                heapSize.Extent.Column,
                heap.LookaheadBytes,
                heap.ErrorCost,
                heap.ChildCount);

            if (heap.ChildCount > 0)
            {
                _output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "visible_child_count={0} named_child_count={1} visible_descendant_count={2} " +
                    "dynamic_precedence={3} repeat_depth={4} production_id={5} first_leaf_symbol={6} " +
                    "first_leaf_parse_state={7}\n",
                    heap.VisibleChildCount,
                    heap.NamedChildCount,
                    heap.VisibleDescendantCount,
                    heap.DynamicPrecedence,
                    heap.RepeatDepth,
                    heap.ProductionId,
                    // Match C oracle's ts_subtree_new_leaf: for a leaf node (no children),
                    // first_leaf_symbol is the node's own symbol, not heap.FirstLeafSymbol
                    // (which NewLeaf doesn't initialize).
                    heap.ChildCount == 0 ? heap.Symbol : heap.FirstLeafSymbol,
                    heap.ChildCount == 0 ? heap.ParseState : heap.FirstLeafParseState);
            }
            else if (heap.HasFlag(SubtreeFlags.HasExternalTokens))
            {
                _output.Append("external_state=");
                ReadOnlySpan<byte> data = ExternalScannerState.DataOf(in heap.ExternalScannerState);
                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];
                    _output.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0x0F]);
                }

                _output.Append('\n');
            }
            else
            {
                _output.AppendFormat(CultureInfo.InvariantCulture, "lookahead_char={0}\n", heap.LookaheadChar);
            }

            ReadOnlySpan<Subtree> children = tree.Children;
            for (int i = 0; i < children.Length; i++)
            {
                Dump(children[i], indent + 1);
            }
        }

        // ------------------------------------------------------------ stack


        /// <summary>The stack commands of the oracle (FORMATS.md, "Stack commands").</summary>
        private static bool IsStackCommand(string command) => command switch
        {
            "snew" or "sdelete" or "spush" or "spopcount" or "spoppending" or "spopall" or "spoperror"
                or "smerge" or "scanmerge" or "scopy" or "sremove" or "srenumber" or "sswap" or "shalt"
                or "sclear" or "spause" or "sresume" or "ssummary" or "sext" or "sdump" or "sgraph" => true,
            _ => false,
        };

        private bool RunStackCommand(string command, List<string> arguments, uint lineNumber)
        {
            switch (command)
            {
                case "snew":
                    return RunNewStack(arguments, lineNumber);
                case "sdelete":
                    return RunDeleteStack(arguments, lineNumber);
                case "spush":
                    return RunPush(arguments, lineNumber);
                case "spopcount":
                    return RunPopCount(arguments, lineNumber);
                case "spoppending":
                    return RunPopPending(arguments, lineNumber);
                case "spopall":
                    return RunPopAll(arguments, lineNumber);
                case "spoperror":
                    return RunPopError(arguments, lineNumber);
                case "smerge":
                    return RunMerge(arguments, lineNumber, false);
                case "scanmerge":
                    return RunMerge(arguments, lineNumber, true);
                case "scopy":
                    return RunCopy(arguments, lineNumber);
                case "sremove":
                    return RunRemoveVersion(arguments, lineNumber);
                case "srenumber":
                    return RunRenumber(arguments, lineNumber);
                case "sswap":
                    return RunSwap(arguments, lineNumber);
                case "shalt":
                    return RunHalt(arguments, lineNumber);
                case "sclear":
                    return RunClearStack(arguments, lineNumber);
                case "spause":
                    return RunPause(arguments, lineNumber);
                case "sresume":
                    return RunResume(arguments, lineNumber);
                case "ssummary":
                    return RunSummary(arguments, lineNumber);
                case "sext":
                    return RunSetExternalToken(arguments, lineNumber);
                case "sdump":
                    return RunDumpStack(arguments, lineNumber);
                case "sgraph":
                    return RunGraph(arguments, lineNumber);
                default:
                    return Error(lineNumber, "unknown command '{0}'", command);
            }
        }

        /// <summary>The argument count check of the oracle: script_check_count.</summary>
        private bool CheckCount(List<string> arguments, int expected, string command, uint lineNumber)
        {
            if (arguments.Count == expected)
            {
                return true;
            }

            return Error(lineNumber, "wrong number of arguments for '{0}'", command);
        }

        /// <summary>script_require_stack.</summary>
        private bool RequireStack(uint lineNumber)
        {
            if (_stack is not null)
            {
                return true;
            }

            return Error(lineNumber, "no stack");
        }

        /// <summary>script_parse_version: a number below the version count of the stack.</summary>
        private bool TryParseVersion(uint lineNumber, string text, out uint version)
        {
            if (!TryParseU32(text, out version))
            {
                Error(lineNumber, "invalid number '{0}'", text);
                return false;
            }

            if (version >= _stack!.VersionCount)
            {
                Error(lineNumber, "invalid version '{0}'", text);
                return false;
            }

            return true;
        }

        /// <summary>script_parse_target: a register that may be written.</summary>
        private bool TryParseTarget(uint lineNumber, string text, out int index)
        {
            if (!TryParseRegister(text, out index))
            {
                Error(lineNumber, "invalid register '{0}'", text);
                return false;
            }

            return true;
        }

        /// <summary>script_check_node_register: a register that holds a subtree.</summary>
        private bool CheckNodeRegister(uint lineNumber, string text, out int index)
        {
            if (!TryParseTarget(lineNumber, text, out index))
            {
                return false;
            }

            if (_registers[index].IsNull)
            {
                return Error(lineNumber, "register {0} is null", text);
            }

            return true;
        }

        /// <summary>script_check_external_register: a register that holds an external token.</summary>
        private bool CheckExternalRegister(uint lineNumber, string text, out int index)
        {
            if (!CheckNodeRegister(lineNumber, text, out index))
            {
                return false;
            }

            Subtree tree = _registers[index];
            if (tree.IsInline)
            {
                return Error(lineNumber, "register {0} is not a heap subtree", text);
            }

            if (!tree.HasExternalTokens)
            {
                return Error(lineNumber, "register {0} has no external tokens", text);
            }

            if (tree.ChildCount != 0)
            {
                return Error(lineNumber, "register {0} is not an external token", text);
            }

            return true;
        }

        /// <summary>store_slices of the oracle: the subtrees of the slices go to the registers from first on,
        /// or every subtree is released and the script stops when they do not fit.</summary>
        private bool StoreSlices(uint lineNumber, TsArray<StackSlice> slices, uint first)
        {
            uint total = 0;
            for (uint i = 0; i < slices.Size; i++)
            {
                total += slices.Get(i).Subtrees.Size;
            }

            if (first + total > RegisterCount)
            {
                uint next = first;
                for (uint i = 0; i < slices.Size; i++)
                {
                    ref StackSlice slice = ref slices.Get(i);
                    for (uint j = 0; j < slice.Subtrees.Size; j++)
                    {
                        Subtree tree = slice.Subtrees.Get(j);
                        if (next < RegisterCount)
                        {
                            _registers[next++] = tree;
                        }
                        else
                        {
                            SubtreeOps.Release(_pool, tree);
                        }
                    }

                    slice.Subtrees.Delete();
                }

                return Error(lineNumber, "not enough registers for the subtrees");
            }

            _output.AppendFormat(CultureInfo.InvariantCulture, "slices = {0}\n", slices.Size);
            uint nextRegister = first;
            for (uint i = 0; i < slices.Size; i++)
            {
                ref StackSlice slice = ref slices.Get(i);
                _output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "slice version = {0} subtrees = {1}\n",
                    slice.Version,
                    slice.Subtrees.Size);
                for (uint j = 0; j < slice.Subtrees.Size; j++)
                {
                    _registers[nextRegister++] = slice.Subtrees.Get(j);
                }

                slice.Subtrees.Delete();
            }

            return true;
        }

        /// <summary>store_error of the oracle: the subtrees of one error go to the registers from first on.</summary>
        private bool StoreError(uint lineNumber, TsArray<Subtree> subtrees, uint first)
        {
            if (first + subtrees.Size > RegisterCount)
            {
                uint next = first;
                for (uint j = 0; j < subtrees.Size; j++)
                {
                    Subtree tree = subtrees.Get(j);
                    if (next < RegisterCount)
                    {
                        _registers[next++] = tree;
                    }
                    else
                    {
                        SubtreeOps.Release(_pool, tree);
                    }
                }

                subtrees.Delete();
                return Error(lineNumber, "not enough registers for the subtrees");
            }

            _output.AppendFormat(CultureInfo.InvariantCulture, "subtrees = {0}\n", subtrees.Size);
            for (uint j = 0; j < subtrees.Size; j++)
            {
                _registers[first + j] = subtrees.Get(j);
            }

            subtrees.Delete();
            return true;
        }

        private bool RunNewStack(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 0, "snew", lineNumber))
            {
                return false;
            }

            _stack?.Delete();
            _stack = new Stack(_pool);
            return true;
        }

        private bool RunDeleteStack(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 0, "sdelete", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            _stack!.Delete();
            _stack = null;
            return true;
        }

        private bool RunPush(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 4, "spush", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            bool hasSubtree = arguments[1] != "-";
            int source = 0;
            if (hasSubtree && !CheckNodeRegister(lineNumber, arguments[1], out source))
            {
                return false;
            }

            if (!TryParseU32(arguments[2], out uint pending) || pending > 1)
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[2]);
            }

            if (!TryParseU32(arguments[3], out uint state))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[3]);
            }

            if (state > ushort.MaxValue)
            {
                return Error(lineNumber, "invalid state '{0}'", arguments[3]);
            }

            Subtree subtree = Subtree.Null;
            if (hasSubtree)
            {
                subtree = _registers[source];
                _registers[source] = Subtree.Null;
            }

            _stack!.Push(version, subtree, pending != 0, (ushort)state);
            return true;
        }

        private bool RunPopCount(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 3, "spopcount", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            if (!TryParseU32(arguments[1], out uint count) || count > MaxPopCount)
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (!TryParseTarget(lineNumber, arguments[2], out int first))
            {
                return false;
            }

            TsArray<StackSlice> slices = _stack!.PopCount(version, count);
            return StoreSlices(lineNumber, slices, (uint)first);
        }

        private bool RunPopPending(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "spoppending", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version)
                || !TryParseTarget(lineNumber, arguments[1], out int first))
            {
                return false;
            }

            TsArray<StackSlice> slices = _stack!.PopPending(version);
            return StoreSlices(lineNumber, slices, (uint)first);
        }

        private bool RunPopAll(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "spopall", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version)
                || !TryParseTarget(lineNumber, arguments[1], out int first))
            {
                return false;
            }

            TsArray<StackSlice> slices = _stack!.PopAll(version);
            return StoreSlices(lineNumber, slices, (uint)first);
        }

        private bool RunPopError(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "spoperror", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version)
                || !TryParseTarget(lineNumber, arguments[1], out int first))
            {
                return false;
            }

            TsArray<Subtree> errors = _stack!.PopError(version);
            return StoreError(lineNumber, errors, (uint)first);
        }

        private bool RunMerge(List<string> arguments, uint lineNumber, bool onlyCheck)
        {
            string command = onlyCheck ? "scanmerge" : "smerge";
            if (!CheckCount(arguments, 2, command, lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint first)
                || !TryParseVersion(lineNumber, arguments[1], out uint second))
            {
                return false;
            }

            if (onlyCheck)
            {
                bool canMerge = _stack!.CanMerge(first, second);
                _output.AppendFormat(CultureInfo.InvariantCulture, "can_merge = {0}\n", canMerge ? 1 : 0);
                return true;
            }

            if (first == second)
            {
                return Error(lineNumber, "the versions must differ");
            }

            bool merged = _stack!.Merge(first, second);
            _output.AppendFormat(CultureInfo.InvariantCulture, "merge = {0}\n", merged ? 1 : 0);
            return true;
        }

        private bool RunCopy(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 1, "scopy", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            if (_stack!.IsPaused(version))
            {
                return Error(lineNumber, "version is paused");
            }

            uint copy = _stack!.CopyVersion(version);
            _output.AppendFormat(CultureInfo.InvariantCulture, "copy = {0}\n", copy);
            return true;
        }

        private bool RunRemoveVersion(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 1, "sremove", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            _stack!.RemoveVersion(version);
            return true;
        }

        private bool RunRenumber(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "srenumber", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint source)
                || !TryParseVersion(lineNumber, arguments[1], out uint target))
            {
                return false;
            }

            if (source != target && target > source)
            {
                return Error(lineNumber, "the target version must be lower");
            }

            _stack!.RenumberVersion(source, target);
            return true;
        }

        private bool RunSwap(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "sswap", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint first)
                || !TryParseVersion(lineNumber, arguments[1], out uint second))
            {
                return false;
            }

            _stack!.SwapVersions(first, second);
            return true;
        }

        private bool RunHalt(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 1, "shalt", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            // A paused version holds a lookahead: the script refuses to halt it (see oracle.c).
            if (_stack!.IsPaused(version))
            {
                return Error(lineNumber, "version is paused");
            }

            _stack!.Halt(version);
            return true;
        }

        private bool RunClearStack(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 0, "sclear", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            _stack!.Clear();
            return true;
        }

        private bool RunPause(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "spause", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            bool hasLookahead = arguments[1] != "-";
            int source = 0;
            if (hasLookahead && !CheckNodeRegister(lineNumber, arguments[1], out source))
            {
                return false;
            }

            if (_stack!.IsPaused(version))
            {
                return Error(lineNumber, "version is paused");
            }

            Subtree lookahead = Subtree.Null;
            if (hasLookahead)
            {
                lookahead = _registers[source];
                _registers[source] = Subtree.Null;
            }

            _stack!.Pause(version, lookahead);
            return true;
        }

        private bool RunResume(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "sresume", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version)
                || !TryParseTarget(lineNumber, arguments[1], out int target))
            {
                return false;
            }

            if (!_stack!.IsPaused(version))
            {
                return Error(lineNumber, "version is not paused");
            }

            _registers[target] = _stack!.Resume(version);
            return true;
        }

        private bool RunSummary(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "ssummary", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            if (!TryParseU32(arguments[1], out uint maxDepth))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            _stack!.RecordSummary(version, maxDepth);
            return true;
        }

        private bool RunSetExternalToken(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 2, "sext", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (!TryParseVersion(lineNumber, arguments[0], out uint version))
            {
                return false;
            }

            Subtree token = Subtree.Null;
            if (arguments[1] != "-")
            {
                if (!CheckExternalRegister(lineNumber, arguments[1], out int source))
                {
                    return false;
                }

                token = _registers[source];
            }

            _stack!.SetLastExternalToken(version, token);
            return true;
        }

        private bool RunDumpStack(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 0, "sdump", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            Stack stack = _stack!;
            uint count = stack.VersionCount;
            _output.AppendFormat(
                CultureInfo.InvariantCulture,
                "versions = {0} halted = {1}\n",
                count,
                stack.HaltedVersionCount());
            for (uint version = 0; version < count; version++)
            {
                string status = stack.IsActive(version) ? "active" : stack.IsPaused(version) ? "paused" : "halted";

                // The order of the calls matters: node_count_since_error changes the value that
                // has_advanced_since_error reads (the same order as the oracle).
                ushort state = stack.State(version);
                Length position = stack.Position(version);
                uint errorCost = stack.ErrorCost(version);
                uint sinceError = stack.NodeCountSinceError(version);
                int precedence = stack.DynamicPrecedence(version);
                bool advanced = stack.HasAdvancedSinceError(version);
                _output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "version {0} status={1} state={2} position={3}:{4}:{5} error_cost={6} node_count_since_error={7} dynamic_precedence={8} advanced={9} summary=",
                    version,
                    status,
                    state,
                    position.Bytes,
                    position.Extent.Row,
                    position.Extent.Column,
                    errorCost,
                    sinceError,
                    precedence,
                    advanced ? 1 : 0);
                TsArray<StackSummaryEntry>? summary = stack.GetSummary(version);
                if (summary is null)
                {
                    _output.Append("null");
                }
                else
                {
                    ReadOnlySpan<StackSummaryEntry> entries = summary.Value.AsSpan();
                    for (int j = 0; j < entries.Length; j++)
                    {
                        _output.Append(j > 0 ? "," : string.Empty);
                        _output.Append(entries[j].State.ToString(CultureInfo.InvariantCulture));
                    }
                }

                _output.Append("\nlast_external:\n");
                Dump(stack.LastExternalToken(version), 0);
            }

            return true;
        }

        private bool RunGraph(List<string> arguments, uint lineNumber)
        {
            if (!CheckCount(arguments, 0, "sgraph", lineNumber) || !RequireStack(lineNumber))
            {
                return false;
            }

            if (_language is null)
            {
                return Error(lineNumber, "no language selected");
            }

            StringWriter writer = new StringWriter(CultureInfo.InvariantCulture);
            _ = _stack!.PrintDotGraph(Language(), writer);
            _output.Append(writer.ToString());
            return true;
        }

        private Language Language() =>
            _language ?? throw new InvalidOperationException("The script does not select a language.");
    }
#pragma warning restore CA1001
}
