using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace TreeSitter.Tests.Lexing;

public sealed class CorpusReaderTests
{
    [Fact]
    public void ReadsAllThreeGrammarCorpusEntryCounts()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "TreeSitter.Managed.slnx")))
        {
            DirectoryInfo? parent = Directory.GetParent(root);
            Assert.NotNull(parent);
            root = parent.FullName;
        }
        Check("Json", new Dictionary<string, int> { ["main.txt"] = 7 });
        Check("C", new Dictionary<string, int>
        {
            ["ambiguities.txt"] = 7, ["crlf.txt"] = 1, ["declarations.txt"] = 19,
            ["expressions.txt"] = 31, ["microsoft.txt"] = 4, ["preprocessor.txt"] = 11,
            ["statements.txt"] = 10, ["types.txt"] = 2,
        });
        Check("Python", new Dictionary<string, int>
        {
            ["errors.txt"] = 2, ["expressions.txt"] = 33, ["literals.txt"] = 21,
            ["pattern_matching.txt"] = 25, ["statements.txt"] = 36,
        });

        void Check(string grammar, Dictionary<string, int> expected)
        {
            string corpus = Path.Combine(root, "grammars", grammar, "upstream", "test", "corpus");
            Assert.Equal(expected.Keys.OrderBy(name => name, StringComparer.Ordinal),
                Directory.GetFiles(corpus, "*.txt", SearchOption.TopDirectoryOnly).Select(file => Path.GetFileName(file) ?? string.Empty).OrderBy(name => name, StringComparer.Ordinal));
            foreach ((string file, int count) in expected)
                Assert.Equal(count, CorpusReader.Read(Path.Combine(corpus, file)).Count);
        }
    }
}
