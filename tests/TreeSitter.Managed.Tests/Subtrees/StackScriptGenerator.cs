using System.Text;

namespace TreeSitter.Tests.Subtrees;

/// <summary>
/// Random stack scripts (FORMATS.md, "Stack commands") for the differential test of the stack.
/// The same seed gives the same script. The generator calls a command only when the C code accepts
/// it: the versions and the registers it names exist and hold what the command needs, a paused version
/// is not paused again, and no NULL subtree is pushed (two NULL links of one node would be merged, and
/// ts_subtree_dynamic_precedence would read NULL). The versions are followed only as far as the
/// commands need them. A merge may or may not happen, so after a merge only version 0 is named, and
/// the versions that the pops add are not named.
/// </summary>
internal static class StackScriptGenerator
{
    private const int RegisterCount = 64;

    /// <summary>Returns the script of the seed.</summary>
    /// <param name="seed">Random seed.</param>
    internal static string Generate(int seed) => new Builder(new Random(seed)).Run();

    private sealed class Builder
    {
        private readonly Random _random;
        private readonly StringBuilder _lines = new StringBuilder();

        /// <summary>The paused flag of each version of the prefix that the script names.</summary>
        private readonly List<bool> _paused = new List<bool> { false };

        /// <summary>The registers that surely hold a subtree.</summary>
        private readonly bool[] _full = new bool[RegisterCount];

        /// <summary>A merge happened, or may have happened: only version 0 is named afterwards.</summary>
        private bool _uncertain;

        /// <summary>A pop happened: the versions it adds are at the end and are not named.</summary>
        private bool _popped;

        internal Builder(Random random)
        {
            _random = random;
        }

        internal string Run()
        {
            Add("lang json");
            Add("snew");
            int operations = _random.Next(20, 46);
            for (int i = 0; i < operations; i++)
            {
                Step(_random.NextDouble());
            }

            Add("sdump");
            Add("sdelete");
            return _lines.ToString();
        }

        private void Add(string line) => _lines.Append(line).Append('\n');

        private void Step(double choice)
        {
            if (choice < 0.18)
            {
                AddLeaf(_random.Next(RegisterCount / 4));
            }
            else if (choice < 0.34)
            {
                Push();
            }
            else if (choice < 0.40)
            {
                int version = PickVersion();
                int count = _random.Next(0, 3);
                Add($"spopcount {version} {count} r0");
                ClearRegisters();
                _popped = true;
            }
            else if (choice < 0.44)
            {
                Add($"spopall {PickVersion()} r0");
                ClearRegisters();
                _popped = true;
            }
            else if (choice < 0.50)
            {
                if (!_uncertain && _paused.Count >= 2)
                {
                    Add($"smerge 0 {_random.Next(1, _paused.Count)}");
                    _uncertain = true;
                }
            }
            else if (choice < 0.56)
            {
                int first = PickVersion();
                Add($"scanmerge {first} {PickVersion()}");
            }
            else if (choice < 0.62)
            {
                Copy();
            }
            else if (choice < 0.67)
            {
                Pause();
            }
            else if (choice < 0.71)
            {
                Resume();
            }
            else if (choice < 0.74)
            {
                int version = PickVersion();
                Add($"ssummary {version} {_random.Next(0, 4)}");
            }
            else if (choice < 0.77)
            {
                // A paused version is not halted: it holds a lookahead (see FORMATS.md).
                int version = PickVersion();
                if (!_paused[version])
                {
                    Add($"shalt {version}");
                }
            }
            else if (choice < 0.80)
            {
                Add($"sext {PickVersion()} -");
            }
            else if (choice < 0.82)
            {
                Swap();
            }
            else if (choice < 0.84)
            {
                Renumber();
            }
            else if (choice < 0.86)
            {
                Remove();
            }
            else if (choice < 0.88)
            {
                Add("sgraph");
            }
            else if (choice < 0.90)
            {
                Add("sclear");
                _paused.Clear();
                _paused.Add(false);
                _uncertain = false;
                _popped = false;
            }
            else if (choice < 0.93)
            {
                ErrorPop();
            }
            else
            {
                Add("sdump");
            }
        }

        /// <summary>spoperror on a version that is not paused. The popped error is renamed to the
        /// version (ts_stack_renumber_version), so the number of versions does not change.</summary>
        private void ErrorPop()
        {
            int version = PickVersion();
            if (_paused[version])
            {
                return;
            }

            Add($"spoperror {version} r0");
            ClearRegisters();
            _popped = true;
        }

        /// <summary>The versions that may be named: the prefix, or version 0 after a merge.</summary>
        private int PickVersion() => _uncertain ? 0 : _random.Next(_paused.Count);

        /// <summary>A register that surely holds a subtree, or -1.</summary>
        private int PickFull()
        {
            List<int> candidates = new List<int>();
            for (int r = 0; r < RegisterCount; r++)
            {
                if (_full[r])
                {
                    candidates.Add(r);
                }
            }

            return candidates.Count == 0 ? -1 : candidates[_random.Next(candidates.Count)];
        }

        private void ClearRegisters() => Array.Clear(_full, 0, _full.Length);

        private void AddLeaf(int register)
        {
            double kind = _random.NextDouble();
            if (kind < 0.25)
            {
                // A zero-size missing leaf: no bytes, but an error cost.
                int symbol = _random.Next(1, 10);
                Add($"missing r{register} {symbol} {_random.Next(0, 10)} 0:0:0 1");
            }
            else if (kind < 0.40)
            {
                // An error leaf of one byte; its state may be 0 (ERROR_STATE).
                Add($"error r{register} 64 0:0:0 1:0:1 1 {_random.Next(0, 10)}");
            }
            else
            {
                int symbol = _random.Next(1, 10);
                int state = _random.Next(1, 10);
                Add($"leaf r{register} {symbol} 0:0:0 1:0:1 1 {state} 0 0 0");
            }

            _full[register] = true;
        }

        private void Push()
        {
            int version = PickVersion();
            int register = PickFull();
            int pending = _random.Next(0, 2);
            int state = _random.Next(0, 13);
            if (register < 0)
            {
                // A NULL subtree is never pushed (see the summary of the class).
                register = _random.Next(RegisterCount / 4);
                AddLeaf(register);
            }

            Add($"spush {version} r{register} {pending} {state}");
            _full[register] = false;
        }

        private void Copy()
        {
            int version = PickVersion();
            if (_paused[version])
            {
                return;
            }

            Add($"scopy {version}");
            if (!_uncertain && !_popped)
            {
                _paused.Add(false);
            }
        }

        private void Pause()
        {
            int version = PickVersion();
            if (_paused[version])
            {
                return;
            }

            int register = PickFull();
            if (register < 0 || _random.NextDouble() < 0.5)
            {
                Add($"spause {version} -");
            }
            else
            {
                Add($"spause {version} r{register}");
                _full[register] = false;
            }

            _paused[version] = true;
        }

        private void Resume()
        {
            int version = PickVersion();
            if (!_paused[version])
            {
                return;
            }

            int register = _random.Next(RegisterCount / 4);
            Add($"sresume {version} r{register}");
            _full[register] = false;
            _paused[version] = false;
        }

        private void Swap()
        {
            if (_uncertain || _paused.Count < 2)
            {
                return;
            }

            int first = _random.Next(_paused.Count);
            int second = _random.Next(_paused.Count);
            Add($"sswap {first} {second}");
            (_paused[first], _paused[second]) = (_paused[second], _paused[first]);
        }

        private void Renumber()
        {
            if (_uncertain || _paused.Count < 2)
            {
                return;
            }

            int source = _random.Next(1, _paused.Count);
            int target = _random.Next(0, source);
            Add($"srenumber {source} {target}");
            _paused[target] = _paused[source];
            _paused.RemoveAt(source);
        }

        private void Remove()
        {
            if (_uncertain || _paused.Count < 2)
            {
                return;
            }

            int version = _random.Next(1, _paused.Count);
            Add($"sremove {version}");
            _paused.RemoveAt(version);
        }
    }
}
