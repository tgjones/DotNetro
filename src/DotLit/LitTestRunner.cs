using System.Collections.Immutable;

namespace DotLit;

public static class LitTestRunner
{
    public static void Run(string filePath, LitTestConfiguration? configuration = null)
    {
        configuration ??= LitTestConfiguration.Default;

        var absolutePath = Path.GetFullPath(filePath);
        var testFile = LitTestParser.ParseFile(absolutePath, configuration);
        var result = LitTestExecutor.Execute(testFile);

        if (!result.Successful)
        {
            throw new Exception($"Lit test failed:{Environment.NewLine}{string.Join(Environment.NewLine, result.ErrorMessages)}");
        }
    }
}

public sealed record LitTestConfiguration(ImmutableDictionary<string, string> Variables)
{
    public static readonly LitTestConfiguration Default = new([]);
}