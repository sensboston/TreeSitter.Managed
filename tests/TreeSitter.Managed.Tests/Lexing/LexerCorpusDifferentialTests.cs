using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TreeSitter.Abi;
using TreeSitter.Tests.Oracle;
using Xunit;

namespace TreeSitter.Tests.Lexing;

public sealed class LexerCorpusDifferentialTests
{
    [OracleTheory]
    [InlineData("json", "Json")]
    [InlineData("c", "C")]
    [InlineData("python", "Python")]
    public void CorpusLeafLexingMatchesNative(string language, string grammarDirectory)
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "grammars", grammarDirectory, "upstream", "test", "corpus");
        LanguageTables tables = Tables(language);
        int leaves = 0;
        int skipped = 0;
        foreach (string corpusFile in Directory.GetFiles(path, "*.txt", SearchOption.TopDirectoryOnly))
        {
            foreach (CorpusEntry entry in CorpusReader.Read(corpusFile))
            {
                byte[] input = Encoding.UTF8.GetBytes(entry.Input);
                string nativeLeaves = NativeOracle.OracleLeaves(language, input);
                List<(string Position, string State)> operations = new();
                string[] leafLines = nativeLeaves.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string previousEnd = "0:0:0";
                foreach (string leaf in leafLines)
                {
                    if (leaf.StartsWith("skipped = ", StringComparison.Ordinal))
                    {
                        skipped += int.Parse(leaf[10..], CultureInfo.InvariantCulture);
                        continue;
                    }
                    string[] fields = leaf.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length != 4 || !ushort.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out ushort state))
                        throw new InvalidOperationException($"Bad oracle_leaves line '{leaf}'.");
                    string start = fields[0][1..^1];
                    string end = fields[1][1..^1];
                    ushort lexState = tables.LexModes![state].LexState;
                    string stateText = lexState.ToString(CultureInfo.InvariantCulture);
                    operations.Add((start, stateText));
                    operations.Add((previousEnd, stateText));
                    previousEnd = end;
                    leaves++;
                }
                StringBuilder script = BuildScript(input, language, operations, tables, null);
                string expected = NativeOracle.OracleLexerScript(script.ToString())!;
                string actual = LexerScriptRunner.Run(script.ToString());
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    Assert.Fail($"{language} corpus '{entry.Name}', input '{entry.Input}': first output difference: {FirstDifference(expected, actual)}");
                if (tables.KeywordLexFn is not null)
                {
                    StringBuilder keywordScript = BuildScript(input, language, operations, tables, actual);
                    expected = NativeOracle.OracleLexerScript(keywordScript.ToString())!;
                    actual = LexerScriptRunner.Run(keywordScript.ToString());
                    if (!string.Equals(expected, actual, StringComparison.Ordinal))
                        Assert.Fail($"{language} keyword corpus '{entry.Name}', input '{entry.Input}': {FirstDifference(expected, actual)}");
                }
            }
        }
        Assert.True(leaves > 0, $"No leaves checked for {language}; skipped errors: {skipped}.");
    }

    private static StringBuilder BuildScript(byte[] input, string language, System.Collections.Generic.IReadOnlyList<(string Position, string State)> operations, LanguageTables tables, string? previousOutput)
    {
        StringBuilder script = new StringBuilder();
        script.Append("input utf8 0 ").Append(input.Length == 0 ? "-" : Convert.ToHexString(input)).Append('\n');
        string[] results = previousOutput?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        int resultIndex = 0;
        foreach ((string position, string state) in operations)
        {
            script.Append("reset ").Append(position).Append("\nlex ").Append(language).Append(" main ").Append(state).Append('\n');
            if (previousOutput is null) continue;
            while (resultIndex < results.Length && !results[resultIndex].StartsWith("lex = ", StringComparison.Ordinal)) resultIndex++;
            if (resultIndex >= results.Length) break;
            string[] parts = results[resultIndex++].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 6 && ushort.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out ushort symbol) && symbol == tables.KeywordCaptureToken)
                script.Append("reset ").Append(position).Append("\nlex ").Append(language).Append(" keyword 0\n");
        }
        return script;
    }

    private static string FirstDifference(string expected, string actual)
    {
        string[] left = expected.Split('\n');
        string[] right = actual.Split('\n');
        int i = 0;
        while (i < left.Length && i < right.Length && string.Equals(left[i], right[i], StringComparison.Ordinal)) i++;
        return $"line {i + 1}: native '{(i < left.Length ? left[i] : "<end>")}', managed '{(i < right.Length ? right[i] : "<end>")}'";
    }

    private static LanguageTables Tables(string language) => language switch
    {
        "json" => TreeSitter.Grammars.Json.JsonGrammar.CreateTables(),
        "c" => TreeSitter.Grammars.C.CGrammar.CreateTables(),
        _ => TreeSitter.Grammars.Python.PythonGrammar.CreateTables(),
    };

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TreeSitter.Managed.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
