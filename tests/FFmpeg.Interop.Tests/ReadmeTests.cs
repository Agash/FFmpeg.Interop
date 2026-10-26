using System.Text.RegularExpressions;

namespace FFmpeg.Interop.Tests;

/// <summary>The README's C# snippets are the compiled ones in <see cref="ReadmeSnippets"/>, line for line.</summary>
[TestClass]
public sealed partial class ReadmeTests
{
    [TestMethod]
    public void EverySnippet_IsCompiledCode()
    {
        string root = RepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md")).ReplaceLineEndings("\n");
        string compiled = Normalize(
            File.ReadAllText(
                Path.Combine(root, "tests", "FFmpeg.Interop.Tests", "ReadmeSnippets.cs")
            )
        );

        MatchCollection snippets = CSharpBlock().Matches(readme);
        Assert.IsGreaterThan(0, snippets.Count);
        foreach (Match snippet in snippets)
        {
            // The using directive is the README's; the snippets file has it implicitly.
            string body = Normalize(
                string.Join(
                    '\n',
                    snippet
                        .Groups[1]
                        .Value.Split('\n')
                        .Where(static l =>
                            !l.TrimStart()
                                .StartsWith("using FFmpeg.Interop;", StringComparison.Ordinal)
                        )
                )
            );
            Assert.Contains(
                body,
                compiled,
                $"README snippet not in ReadmeSnippets.cs:\n{snippet.Value}"
            );
        }
    }

    // Whitespace dropped: a README block and the formatted method body wrap and indent differently, but
    // every token must be the same.
    private static string Normalize(string code) =>
        string.Concat(code.Where(static c => !char.IsWhiteSpace(c)));

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FFmpeg.Interop.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("No repository root above the tests.");
    }

    [GeneratedRegex("```csharp\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CSharpBlock();
}
