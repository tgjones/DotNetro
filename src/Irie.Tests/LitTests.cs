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
            ["irie-as"]  = Path.Combine(AppContext.BaseDirectory, "irie-as"),
            ["irie-cg"]  = Path.Combine(AppContext.BaseDirectory, "irie-cg"),
            ["irie-dis"] = Path.Combine(AppContext.BaseDirectory, "irie-dis"),
            ["iriec"]    = Path.Combine(AppContext.BaseDirectory, "iriec"),
            ["irie-mc"]  = Path.Combine(AppContext.BaseDirectory, "irie-mc"),
        }.ToImmutableDictionary()));
    }

    public static IEnumerable<Func<string>> LitTestSource()
    {
        var result = new List<Func<string>>();

        foreach (var pattern in new[] { "*.irie", "*.mir", "*.s" })
        foreach (var litTestFile in Directory.EnumerateFiles("Lit", pattern, SearchOption.AllDirectories))
            result.Add(() => litTestFile);

        return result;
    }
}
