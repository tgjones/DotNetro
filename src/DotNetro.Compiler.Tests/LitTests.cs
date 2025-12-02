using Irie.Testing.Lit;

namespace DotNetro.Compiler.Tests;

public sealed class LitTests
{
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void ListTest(LitTest test)
    {
        LitTestExecutor.Execute(test.FilePath);
    }

    public static IEnumerable<Func<LitTest>> LitTestSource()
    {
        var result = new List<Func<LitTest>>();

        var litTestFiles = Directory.EnumerateFiles(
            "Lit",
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var litTestFile in litTestFiles)
        {
            result.Add(() => new LitTest(litTestFile));
        }

        return result;
    }
}

public record LitTest(string FilePath);
