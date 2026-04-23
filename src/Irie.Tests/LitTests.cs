using System.Collections.Immutable;
using DotLit;

namespace Irie.Tests;

public sealed class LitTests
{
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void LitTest(string filePath)
    {
        LitTestRunner.Run(filePath, new LitTestConfiguration(new Dictionary<string, string>
        {
            ["irie-as"] = Path.Combine(AppContext.BaseDirectory, "irie-as"),
            ["irie-dis"] = Path.Combine(AppContext.BaseDirectory, "irie-dis"),
        }.ToImmutableDictionary()));
    }

    public static IEnumerable<Func<string>> LitTestSource()
    {
        var result = new List<Func<string>>();

        var litTestFiles = Directory.EnumerateFiles(
            "Lit",
            "*.irie",
            SearchOption.AllDirectories);

        foreach (var litTestFile in litTestFiles)
        {
            result.Add(() => litTestFile);
        }

        return result;
    }
}
