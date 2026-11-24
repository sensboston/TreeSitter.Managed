using System.Globalization;
using System.Text;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// Generates valid random subtree scripts (tests/Oracle/FORMATS.md, "Subtree script") for
/// differential testing (task M1a-T6b). Deterministic for a seed (new Random(seed)).
/// </summary>
/// <remarks>
/// The generator only emits commands the oracle accepts: every command uses a live register,
/// symbols and productions are taken from the language tables, and the commands that need a
/// heap subtree (clone, summarize, extstate) only see registers known to be heap subtrees
/// (nodes, error leaves, external tokens). Every mutation is followed by a dump of all live
/// registers.
/// </remarks>
internal static class SubtreeScriptGenerator
{
    private const int RegisterCount = 64;
    private const int MaxLive = 6;

    /// <param name="seed">Random seed.</param>
    /// <param name="language">"json", "c" or "python".</param>
    /// <param name="commandCount">Number of mutating and query commands (dump commands are extra).</param>
    internal static string Generate(int seed, string language, int commandCount)
    {
        ScriptBuilder builder = new ScriptBuilder(new Random(seed), GetLanguage(language));
        return builder.Build(language, commandCount);
    }

    private static LanguageInfo GetLanguage(string language)
    {
        return language switch
        {
            "json" => new LanguageInfo(25, 2, 32, [22, 23, 24]),
            "c" => new LanguageInfo(360, 131, 2015, RepeatRange(324, 359)),
            "python" => new LanguageInfo(273, 150, 2788, RepeatRange(241, 272)),
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "unknown language"),
        };
    }

    private static ushort[] RepeatRange(int first, int last)
    {
        ushort[] result = new ushort[last - first + 1];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (ushort)(first + i);
        }

        return result;
    }

    private sealed class LanguageInfo
    {
        internal LanguageInfo(int symbolCount, int productionCount, int stateCount, ushort[] repeatSymbols)
        {
            SymbolCount = symbolCount;
            ProductionCount = productionCount;
            StateCount = stateCount;
            RepeatSymbols = repeatSymbols;
        }

        internal int SymbolCount { get; }

        internal int ProductionCount { get; }

        internal int StateCount { get; }

        internal ushort[] RepeatSymbols { get; }
    }

    private sealed class ScriptBuilder
    {
        private readonly Random random;
        private readonly LanguageInfo language;
        private readonly StringBuilder output = new StringBuilder();
        private readonly bool[] live = new bool[RegisterCount];
        private readonly bool[] knownHeap = new bool[RegisterCount];
        private readonly bool[] knownExternal = new bool[RegisterCount];
        private readonly bool[] isRepeat = new bool[RegisterCount];
        private readonly int[] childCounts = new int[RegisterCount];
        private int emitted;

        internal ScriptBuilder(Random random, LanguageInfo language)
        {
            this.random = random;
            this.language = language;
        }

        internal string Build(string languageName, int commandCount)
        {
            output.Append("lang ").Append(languageName).Append('\n');
            while (emitted < commandCount)
            {
                if (CountLive() >= MaxLive)
                {
                    Release();
                    continue;
                }

                Step(random.Next(0, 100));
            }

            return output.ToString();
        }

        private void Step(int pick)
        {
            if (pick < 14)
            {
                CreateLeaf();
            }
            else if (pick < 18)
            {
                CreateError();
            }
            else if (pick < 22)
            {
                CreateMissing();
            }
            else if (pick < 25)
            {
                SetExternalState();
            }
            else if (pick < 37)
            {
                CreateNode();
            }
            else if (pick < 41)
            {
                CreateErrorNode();
            }
            else if (pick < 46)
            {
                Duplicate();
            }
            else if (pick < 51)
            {
                Release();
            }
            else if (pick < 55)
            {
                MakeMutable();
            }
            else if (pick < 59)
            {
                Clone();
            }
            else if (pick < 63)
            {
                SetExtra();
            }
            else if (pick < 68)
            {
                SetSymbol();
            }
            else if (pick < 76)
            {
                Compress();
            }
            else if (pick < 80)
            {
                Summarize();
            }
            else if (pick < 90)
            {
                Edit();
            }
            else if (pick < 94)
            {
                Print();
            }
            else if (pick < 97)
            {
                Compare();
            }
            else
            {
                LastExternal();
            }
        }

        // ------------------------------------------------------------ registers

        private int CountLive()
        {
            int count = 0;
            for (int i = 0; i < RegisterCount; i++)
            {
                if (live[i])
                {
                    count++;
                }
            }

            return count;
        }

        private static List<int> Registers(Func<int, bool> predicate)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < RegisterCount; i++)
            {
                if (predicate(i))
                {
                    result.Add(i);
                }
            }

            return result;
        }

        private bool IsLive(int index) => live[index];

        private int PickRandom(List<int> candidates) => candidates[random.Next(candidates.Count)];

        private List<int> LiveRegisters() => Registers(IsLive);

        private List<int> EmptyRegisters() => Registers(index => !live[index]);

        private int PickLive() => PickRandom(LiveRegisters());

        private int PickEmpty() => PickRandom(EmptyRegisters());

        private void Free(int index)
        {
            live[index] = false;
            knownHeap[index] = false;
            knownExternal[index] = false;
            isRepeat[index] = false;
            childCounts[index] = 0;
        }

        private void Occupy(int index, bool heap, bool external, int children)
        {
            live[index] = true;
            knownHeap[index] = heap;
            knownExternal[index] = external;
            childCounts[index] = children;
            isRepeat[index] = false;
        }

        private void CopyKnowledge(int source, int destination)
        {
            live[destination] = true;
            knownHeap[destination] = knownHeap[source];
            knownExternal[destination] = knownExternal[source];
            childCounts[destination] = childCounts[source];
            isRepeat[destination] = isRepeat[source];
        }

        private List<int> PickHeapRegisters() => Registers(index => live[index] && knownHeap[index]);

        // ------------------------------------------------------------ text helpers

        private static string Reg(int index) => "r" + index.ToString(CultureInfo.InvariantCulture);

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Len(int bytes, int row, int column) =>
            Num(bytes) + ":" + Num(row) + ":" + Num(column);

        private void Line(string text)
        {
            output.Append(text).Append('\n');
            emitted++;
        }

        private void DumpAll()
        {
            for (int i = 0; i < RegisterCount; i++)
            {
                if (live[i])
                {
                    output.Append("dump ").Append(Reg(i)).Append('\n');
                }
            }
        }

        // ------------------------------------------------------------ random values

        private int RealSymbol() => random.Next(1, language.SymbolCount);

        private int InlineSymbol() => random.Next(1, Math.Min(255, language.SymbolCount));

        private int State() => random.Next(0, language.StateCount);

        private int Production() => random.Next(0, language.ProductionCount);

        private int Bytes(int max) => random.Next(0, max + 1);

        // Leaves never become inline: a lookahead of 16 or more keeps them on the heap, and
        // edits do not change the lookahead. An edit that turns an inline leaf into a heap node
        // leaves parts of the new node uninitialized in the runtime (ts_subtree_edit), so the
        // script never triggers it.
        private int HeapLookahead() => 16 + Bytes(5);

        // Padding and size as "bytes:row:column": the padding is usually short and on
        // one row, the size is a short token. Heap-sized values appear now and then.
        private string Padding() => Len(Bytes(5), random.Next(0, 3), Bytes(5));

        private string Size()
        {
            int bytes = random.Next(1, 6);
            return Len(bytes, 0, bytes);
        }

        // ------------------------------------------------------------ commands

        private void CreateLeaf()
        {
            if (EmptyRegisters().Count == 0)
            {
                Release();
                return;
            }

            int destination = PickEmpty();
            bool external = random.Next(0, 5) == 0;
            int symbol = external ? RealSymbol() : (random.Next(0, 2) == 0 ? InlineSymbol() : RealSymbol());
            string padding = Padding();
            string size = Size();
            int lookahead = HeapLookahead();
            int state = State();
            int column = random.Next(0, 2);
            int keyword = random.Next(0, 2);
            Line("leaf " + Reg(destination) + " " + Num(symbol) + " " + padding + " " + size + " " +
                Num(lookahead) + " " + Num(state) + " " + Num(external ? 1 : 0) + " " +
                Num(column) + " " + Num(keyword));
            Occupy(destination, true, external, 0);
            DumpAll();
        }

        private void CreateError()
        {
            if (EmptyRegisters().Count == 0)
            {
                Release();
                return;
            }

            int destination = PickEmpty();
            int[] characters = [-1, 0, 9, 10, 13, 32, 65, 126, 200];
            int character = characters[random.Next(characters.Length)];
            Line("error " + Reg(destination) + " " + Num(character) + " " + Padding() + " " + Size() + " " +
                Num(Bytes(5)) + " " + Num(State()));
            Occupy(destination, true, false, 0);
            DumpAll();
        }

        private void CreateMissing()
        {
            if (EmptyRegisters().Count == 0)
            {
                Release();
                return;
            }

            int destination = PickEmpty();
            Line("missing " + Reg(destination) + " " + Num(RealSymbol()) + " " + Num(State()) + " " +
                Padding() + " " + Num(HeapLookahead()));
            Occupy(destination, true, false, 0);
            DumpAll();
        }

        private void SetExternalState()
        {
            List<int> candidates = Registers(index => live[index] && knownExternal[index] && childCounts[index] == 0);
            if (candidates.Count == 0)
            {
                CreateExternalLeaf();
                candidates = Registers(index => live[index] && knownExternal[index] && childCounts[index] == 0);
                if (candidates.Count == 0)
                {
                    return;
                }
            }

            int index = PickRandom(candidates);
            Line("extstate " + Reg(index) + " " + RandomHex());
            DumpAll();
        }

        private void CreateExternalLeaf()
        {
            if (EmptyRegisters().Count == 0)
            {
                return;
            }

            int destination = PickEmpty();
            Line("leaf " + Reg(destination) + " " + Num(RealSymbol()) + " " + Padding() + " " + Size() + " " +
                Num(HeapLookahead()) + " " + Num(State()) + " 1 0 0");
            Occupy(destination, true, true, 0);
            DumpAll();
        }

        private string RandomHex()
        {
            int kind = random.Next(0, 3);
            if (kind == 0)
            {
                return "-";
            }

            int bytes = kind == 1 ? random.Next(1, 6) : random.Next(25, 35);
            StringBuilder hex = new StringBuilder();
            for (int i = 0; i < bytes; i++)
            {
                hex.Append(random.Next(0, 256).ToString("x2", CultureInfo.InvariantCulture));
            }

            return hex.ToString();
        }

        private List<int> PickChildren(int maxCount)
        {
            List<int> shuffled = LiveRegisters();
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = random.Next(0, i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            int count = random.Next(1, Math.Min(maxCount, shuffled.Count) + 1);
            return shuffled.GetRange(0, count);
        }

        private void CreateNode()
        {
            if (LiveRegisters().Count == 0 || EmptyRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            List<int> children = PickChildren(3);
            int destination = PickEmpty();
            int symbol = RealSymbol();
            int production = Production();
            StringBuilder text = new StringBuilder();
            text.Append("node ").Append(Reg(destination)).Append(' ').Append(Num(symbol)).Append(' ')
                .Append(Num(production));
            AppendChildren(text, children);
            Line(text.ToString());
            Adopt(destination, children, symbol, false);
            DumpAll();
        }

        private void CreateErrorNode()
        {
            if (LiveRegisters().Count == 0 || EmptyRegisters().Count == 0)
            {
                CreateError();
                return;
            }

            List<int> children = PickChildren(2);
            int destination = PickEmpty();
            StringBuilder text = new StringBuilder();
            text.Append("errnode ").Append(Reg(destination)).Append(' ').Append(Num(random.Next(0, 2)));
            AppendChildren(text, children);
            Line(text.ToString());
            Adopt(destination, children, 65535, true);
            DumpAll();
        }

        private static void AppendChildren(StringBuilder text, List<int> children)
        {
            foreach (int child in children)
            {
                text.Append(' ').Append(Reg(child));
            }
        }

        // Children are consumed: their registers become empty and the parent takes the first one.
        private void Adopt(int destination, List<int> children, int symbol, bool error)
        {
            int childCount = children.Count;
            foreach (int child in children)
            {
                Free(child);
            }

            Occupy(destination, true, false, childCount);
            isRepeat[destination] = !error && children.Count == 2 && IsRepeat(symbol);
        }

        private bool IsRepeat(int symbol)
        {
            foreach (ushort repeat in language.RepeatSymbols)
            {
                if (repeat == symbol)
                {
                    return true;
                }
            }

            return false;
        }

        private void Duplicate()
        {
            if (LiveRegisters().Count == 0 || EmptyRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            int source = PickLive();
            int destination = PickEmpty();
            Line("dup " + Reg(destination) + " " + Reg(source));
            CopyKnowledge(source, destination);
            DumpAll();
        }

        private void Release()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            int index = PickLive();
            Line("release " + Reg(index));
            Free(index);
            DumpAll();
        }

        private void MakeMutable()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            Line("mut " + Reg(PickLive()));
            DumpAll();
        }

        private void Clone()
        {
            List<int> sources = PickHeapRegisters();
            if (sources.Count == 0 || EmptyRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            int source = PickRandom(sources);
            int destination = PickEmpty();
            Line("clone " + Reg(destination) + " " + Reg(source));
            CopyKnowledge(source, destination);
            DumpAll();
        }

        // setextra and setsym require a mutable subtree: "mut" always comes first.
        private void SetExtra()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            int index = PickLive();
            Line("mut " + Reg(index));
            Line("setextra " + Reg(index) + " " + Num(random.Next(0, 2)));
            DumpAll();
        }

        private void SetSymbol()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            int index = PickLive();
            // An inline leaf keeps its symbol under 255, so the symbol always fits.
            int symbol = InlineSymbol();
            Line("mut " + Reg(index));
            Line("setsym " + Reg(index) + " " + Num(symbol));
            isRepeat[index] = false;
            DumpAll();
        }

        private void Compress()
        {
            List<int> repeats = Registers(index => live[index] && isRepeat[index]);
            int index;
            if (repeats.Count > 0 && random.Next(0, 4) != 0)
            {
                index = PickRandom(repeats);
            }
            else if (LiveRegisters().Count > 0 && random.Next(0, 4) == 0)
            {
                index = PickLive();
            }
            else
            {
                index = BuildRepeatChain();
            }

            Line("mut " + Reg(index));
            Line("compress " + Reg(index) + " " + Num(random.Next(0, 6)));
            DumpAll();
        }

        // Builds a left-leaning chain of repeat nodes with two children each:
        // aux_sym_*_repeat(aux_sym_*_repeat(aux_sym_*_repeat(leaf, leaf), leaf), leaf) ...
        private int BuildRepeatChain()
        {
            while (EmptyRegisters().Count < 3 && LiveRegisters().Count > 0)
            {
                Release();
            }

            int repeat = language.RepeatSymbols[random.Next(language.RepeatSymbols.Length)];
            int depth = random.Next(2, 7);
            int first = PickEmpty();
            EmitRepeatLeaf(first);
            int second = PickEmpty();
            EmitRepeatLeaf(second);
            int current = PickEmpty();
            EmitRepeatNode(current, first, second, repeat);
            for (int level = 1; level < depth; level++)
            {
                int leaf = PickEmpty();
                EmitRepeatLeaf(leaf);
                int next = PickEmpty();
                EmitRepeatNode(next, current, leaf, repeat);
                current = next;
            }

            return current;
        }

        private void EmitRepeatLeaf(int destination)
        {
            Line("leaf " + Reg(destination) + " " + Num(InlineSymbol()) + " " + Padding() + " " + Size() + " " +
                Num(HeapLookahead()) + " " + Num(State()) + " 0 0 0");
            Occupy(destination, true, false, 0);
            DumpAll();
        }

        private void EmitRepeatNode(int destination, int left, int right, int repeat)
        {
            Line("node " + Reg(destination) + " " + Num(repeat) + " " + Num(Production()) + " " +
                Reg(left) + " " + Reg(right));
            Adopt(destination, new List<int> { left, right }, repeat, false);
            DumpAll();
        }

        private void Summarize()
        {
            List<int> nodes = Registers(index => live[index] && knownHeap[index] && childCounts[index] > 0);
            if (nodes.Count == 0)
            {
                CreateNode();
                return;
            }

            Line("summarize " + Reg(PickRandom(nodes)));
            DumpAll();
        }

        private void Edit()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            int index = PickLive();
            int span = 1 + Bytes(12);
            int start = random.Next(0, span + 1);
            int startRow = random.Next(0, 3);
            int startColumn = startRow == 0 ? start : Bytes(5);
            int oldEnd = start + random.Next(0, 6);
            int newEnd = start + random.Next(0, 6);
            int oldEndRow = startRow;
            int newEndRow = random.Next(0, 3) == 0 ? startRow + 1 : startRow;
            int oldEndColumn = oldEnd - start + startColumn;
            int newEndColumn = newEndRow == startRow ? newEnd - start + startColumn : Bytes(5);
            if (random.Next(0, 3) == 0)
            {
                // Pure deletion: the new end is the start.
                newEnd = start;
                newEndRow = startRow;
                newEndColumn = startColumn;
                oldEnd = start + 1 + Bytes(4);
                oldEndColumn = oldEnd - start + startColumn;
            }

            Line("edit " + Reg(index) + " " + Len(start, startRow, startColumn) + " " +
                Len(oldEnd, oldEndRow, oldEndColumn) + " " + Len(newEnd, newEndRow, newEndColumn));
            DumpAll();
        }

        private void Print()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            int index = PickLive();
            int alias = random.Next(0, 2) == 0 ? 0 : RealSymbol();
            Line("string " + Reg(index) + " " + Num(alias) + " " + Num(random.Next(0, 2)) + " " +
                Num(random.Next(0, 2)));
        }

        private void Compare()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            Line("compare " + Reg(PickLive()) + " " + Reg(PickLive()));
        }

        private void LastExternal()
        {
            if (LiveRegisters().Count == 0)
            {
                CreateLeaf();
                return;
            }

            Line("lastext " + Reg(PickLive()));
        }
    }
}
