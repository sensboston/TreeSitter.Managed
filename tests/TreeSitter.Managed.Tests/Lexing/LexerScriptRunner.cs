using System.Globalization;
using System.Text;
using TreeSitter;

namespace TreeSitter.Tests.Lexing;

/// <summary>
/// Runs a lexer script (tests/Oracle/FORMATS.md, section "Lexer script") on the managed Lexer.
/// Must print exactly what oracle_lexer_script prints.
/// </summary>
internal static class LexerScriptRunner
{
    internal static string Run(string script)
    {
        Runner runner = new Runner();
        return runner.Run(script);
    }

    /// <summary>The interpreter of one script.</summary>
    private sealed class Runner
    {
        // The number of arguments of a line of C (SCRIPT_MAX_TOKENS).
        private const int MaxTokens = 256;

        private const int MaxBytes = 65536;

        private static readonly TreeSitter.Abi.LanguageTables JsonTables = TreeSitter.Grammars.Json.JsonGrammar.CreateTables();
        private static readonly TreeSitter.Abi.LanguageTables CTables = TreeSitter.Grammars.C.CGrammar.CreateTables();
        private static readonly TreeSitter.Abi.LanguageTables PythonTables = TreeSitter.Grammars.Python.PythonGrammar.CreateTables();

        private static TreeSitter.Abi.LanguageTables? GetTables(string grammar) => grammar switch
        {
            "json" => JsonTables,
            "c" => CTables,
            "python" => PythonTables,
            _ => null,
        };

        private readonly Lexer _lexer = new Lexer();
        private readonly StringBuilder _output = new StringBuilder();
        private readonly ScriptInput _input;
        private byte[] _bytes = [];
        private uint _chunkSize;

        public Runner() => _input = new ScriptInput(this);

        public string Run(string script)
        {
            _lexer.SetInput(_input);
            string[] lines = script.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!RunLine(lines[i], (uint)(i + 1)))
                {
                    break;
                }
            }

            _lexer.Delete();
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

        // ------------------------------------------------------------ tokens

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
            data = [];
            if (text == "-")
            {
                return true;
            }

            if ((text.Length % 2) != 0 || (text.Length / 2) > MaxBytes)
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

        private static bool TryParseRange(string text, out Range value)
        {
            value = default;
            int dash = text.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
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

        // ------------------------------------------------------------ output

        private static void PutLength(StringBuilder builder, Length length) =>
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}:{1}:{2}",
                length.Bytes,
                length.Extent.Row,
                length.Extent.Column);

        private void Dump()
        {
            _output.AppendFormat(CultureInfo.InvariantCulture, "lookahead = {0}\n", _lexer.Lookahead);
            _output.Append("current = ");
            PutLength(_output, _lexer.CurrentPosition);
            _output.Append('\n');
            _output.Append("token_start = ");
            PutLength(_output, _lexer.TokenStartPosition);
            _output.Append('\n');
            _output.Append("token_end = ");
            PutLength(_output, _lexer.TokenEndPosition);
            _output.Append('\n');
            _output.AppendFormat(CultureInfo.InvariantCulture, "lookahead_size = {0}\n", _lexer.LookaheadSize);
            _output.AppendFormat(
                CultureInfo.InvariantCulture,
                "included_range_index = {0}\n",
                _lexer.CurrentIncludedRangeIndex);
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
                case "lex":
                    return RunLex(arguments, lineNumber);
                case "input":
                    return RunInput(arguments, lineNumber);
                case "ranges":
                    return RunRanges(arguments, lineNumber);
                case "reset":
                    return RunReset(arguments, lineNumber);
                case "start":
                    return RunNoArguments(arguments, lineNumber, command, () => _lexer.Start());
                case "advance":
                    return RunAdvance(arguments, lineNumber);
                case "mark_end":
                    return RunNoArguments(arguments, lineNumber, command, () => _lexer.MarkEndInternal());
                case "column":
                    return RunNoArguments(arguments, lineNumber, command, () =>
                        _output.AppendFormat(CultureInfo.InvariantCulture, "column = {0}\n", _lexer.GetColumn()));
                case "eof":
                    return RunNoArguments(arguments, lineNumber, command, () =>
                        _output.AppendFormat(CultureInfo.InvariantCulture, "eof = {0}\n", _lexer.Eof() ? 1 : 0));
                case "at_range_start":
                    return RunNoArguments(arguments, lineNumber, command, () =>
                        _output.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "at_range_start = {0}\n",
                            _lexer.IsAtIncludedRangeStart() ? 1 : 0));
                case "finish":
                    return RunFinish(arguments, lineNumber);
                case "dump":
                    return RunNoArguments(arguments, lineNumber, command, Dump);
                default:
                    return Error(lineNumber, "unknown command '{0}'", command);
            }
        }

        private bool RunLex(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 3)
                return Error(lineNumber, "wrong number of arguments for '{0}'", "lex");
            if (!TryParseU32(arguments[2], out uint state) || state > ushort.MaxValue)
                return Error(lineNumber, "state must be in 0..65535");
            TreeSitter.Abi.LanguageTables? tables = GetTables(arguments[0]);
            if (tables is null) return Error(lineNumber, "unknown language");
            TreeSitter.LexFunction? function = arguments[1] switch
            {
                "main" => tables.LexFn,
                "keyword" => tables.KeywordLexFn,
                _ => null,
            };
            if (arguments[1] is not ("main" or "keyword"))
                return Error(lineNumber, "lexer kind must be main or keyword");
            if (function is null)
                return Error(lineNumber, "language has no keyword lexer");
            _lexer.Start();
            bool result = function(_lexer, (ushort)state);
            _output.AppendFormat(CultureInfo.InvariantCulture, "lex = {0} result_symbol = {1}\n", result ? 1 : 0, _lexer.ResultSymbol);
            uint end = 0;
            _lexer.Finish(ref end);
            _output.AppendFormat(CultureInfo.InvariantCulture, "lookahead_end_byte = {0}\n", end);
            Dump();
            return true;
        }

        private bool RunNoArguments(List<string> arguments, uint lineNumber, string command, Action action)
        {
            if (arguments.Count != 0)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", command);
            }

            action();
            return true;
        }

        private bool RunInput(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 3)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "input");
            }

            InputEncoding encoding = arguments[0] switch
            {
                "utf8" => InputEncoding.Utf8,
                "utf16le" => InputEncoding.Utf16LE,
                "utf16be" => InputEncoding.Utf16BE,
                _ => (InputEncoding)(-1),
            };
            if ((int)encoding < 0)
            {
                return Error(lineNumber, "invalid encoding '{0}'", arguments[0]);
            }

            if (!TryParseU32(arguments[1], out uint chunkSize))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[1]);
            }

            if (!TryParseHex(arguments[2], out byte[] bytes))
            {
                return Error(lineNumber, "invalid hex data '{0}'", arguments[2]);
            }

            _bytes = bytes;
            _chunkSize = chunkSize;
            _input.Encoding = encoding;
            _lexer.SetInput(_input);
            return true;
        }

        private bool RunRanges(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count < 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "ranges");
            }

            if (!TryParseU32(arguments[0], out uint count))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[0]);
            }

            if (arguments.Count != (int)count + 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "ranges");
            }

            // The count cannot exceed the number of arguments of a line: such a line has
            // more arguments than MaxTokens allows.

            Range[] ranges = new Range[count];
            for (int i = 0; i < count; i++)
            {
                if (!TryParseRange(arguments[i + 1], out ranges[i]))
                {
                    return Error(lineNumber, "invalid length '{0}'", arguments[i + 1]);
                }
            }

            bool ok = _lexer.SetIncludedRanges(ranges);
            _output.AppendFormat(CultureInfo.InvariantCulture, "ranges = {0}\n", ok ? 1 : 0);
            return true;
        }

        private bool RunReset(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "reset");
            }

            if (!TryParseLength(arguments[0], out Length position))
            {
                return Error(lineNumber, "invalid length '{0}'", arguments[0]);
            }

            _lexer.Reset(position);
            return true;
        }

        private bool RunAdvance(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "advance");
            }

            if (!TryParseU32(arguments[0], out uint skip) || skip > 1)
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[0]);
            }

            _lexer.Advance(skip != 0);
            return true;
        }

        private bool RunFinish(List<string> arguments, uint lineNumber)
        {
            if (arguments.Count != 1)
            {
                return Error(lineNumber, "wrong number of arguments for '{0}'", "finish");
            }

            if (!TryParseU32(arguments[0], out uint lookaheadEndByte))
            {
                return Error(lineNumber, "invalid number '{0}'", arguments[0]);
            }

            _lexer.Finish(ref lookaheadEndByte);
            _output.AppendFormat(CultureInfo.InvariantCulture, "lookahead_end_byte = {0}\n", lookaheadEndByte);
            return true;
        }

        /// <summary>The input of the script: the bytes, at most chunk_size of them per read.</summary>
        private sealed class ScriptInput : IParseInput
        {
            private readonly Runner _runner;

            internal ScriptInput(Runner runner) => _runner = runner;

            public InputEncoding Encoding { get; set; } = InputEncoding.Utf8;

            public DecodeFunction? Decode => null;

            public ReadOnlyMemory<byte> Read(uint byteIndex, Point position)
            {
                _ = position;
                byte[] bytes = _runner._bytes;
                if (byteIndex >= bytes.Length)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                uint remaining = (uint)bytes.Length - byteIndex;
                uint size = _runner._chunkSize == 0 || _runner._chunkSize > remaining
                    ? remaining
                    : _runner._chunkSize;
                return bytes.AsMemory((int)byteIndex, (int)size);
            }
        }
    }
}

/// <summary>Valid random lexer scripts, deterministic for a seed (task M1b-B2).</summary>
internal static class LexerScriptGenerator
{
    internal static string Generate(int seed, int commandCount) =>
        LexerScriptGeneratorCore.Generate(seed, commandCount);
}
