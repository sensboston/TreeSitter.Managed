using System;
using System.Collections.Generic;
using System.IO;

namespace TreeSitter.Tests.Lexing;

internal sealed record CorpusEntry(string Name, string Input, IReadOnlyList<string> Attributes);

internal static class CorpusReader
{
    private static bool IsDivider(string line) => IsSeparatorLine(line, '=');
    private static bool IsCaseSeparator(string line) => IsSeparatorLine(line, '-');

    private static bool IsSeparatorLine(string line, char separator)
    {
        if (line.Length < 3 || line[0] != separator) return false;
        for (int i = 1; i < line.Length; i++)
            if (line[i] != separator) return false;
        return true;
    }

    internal static IReadOnlyList<CorpusEntry> Read(string path)
    {
        string[] lines = File.ReadAllLines(path);
        List<CorpusEntry> result = new();
        int i = 0;
        while (i < lines.Length)
        {
            while (i < lines.Length && !IsDivider(lines[i])) i++;
            if (i == lines.Length) break;
            i++;
            while (i < lines.Length && lines[i].Length == 0) i++;
            if (i == lines.Length) break;
            string name = lines[i++].Trim();
            List<string> attributes = new();
            while (i < lines.Length && lines[i].StartsWith(':')) attributes.Add(lines[i++].Trim());
            while (i < lines.Length && !IsDivider(lines[i])) i++;
            if (i < lines.Length) i++;
            while (i < lines.Length && lines[i].StartsWith(':')) attributes.Add(lines[i++].Trim());
            if (i < lines.Length && lines[i].Length == 0) i++;
            List<string> input = new();
            while (i < lines.Length && !IsCaseSeparator(lines[i])) input.Add(lines[i++]);
            if (i < lines.Length) i++;
            result.Add(new CorpusEntry(name, input.Count == 0 ? string.Empty : string.Join("\n", input) + "\n", attributes));
        }
        return result;
    }
}
