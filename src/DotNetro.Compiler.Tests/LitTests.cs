using System.Collections.Immutable;

using DotLit;

namespace DotNetro.Compiler.Tests;

public sealed class LitTests
{
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void LitTest(string filePath)
    {
        var buildConfig = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar)[^2];
        LitTestRunner.Run(filePath, new LitTestConfiguration(new Dictionary<string, string>
        {
            ["cs_compiler"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../DotNetro.Compiler.Tests.CsCompiler/bin/{buildConfig}/net10.0/DotNetro.Compiler.Tests.CsCompiler")),
            ["dnrc"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../DotNetro.Compiler.Driver/bin/{buildConfig}/net10.0/dnrc")),
        }.ToImmutableDictionary()));
    }

    public static IEnumerable<Func<string>> LitTestSource()
    {
        var result = new List<Func<string>>();

        var litTestFiles = Directory.EnumerateFiles(
            "Lit",
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var litTestFile in litTestFiles)
        {
            result.Add(() => litTestFile);
        }

        return result;
    }
}
