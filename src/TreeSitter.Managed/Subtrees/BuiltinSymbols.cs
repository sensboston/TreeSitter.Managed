// parser.h / language.h: ts_builtin_sym_end, ts_builtin_sym_error, ts_builtin_sym_error_repeat.

namespace TreeSitter.Subtrees;

internal static class BuiltinSymbols
{
    internal const ushort End = 0;
    internal const ushort Error = ushort.MaxValue;
    internal const ushort ErrorRepeat = ushort.MaxValue - 1;
}
