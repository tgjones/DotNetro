namespace DotLit;

public static class LitTestRunner
{
    public static void Run(string filePath)
    {
        var testFile = LitTestParser.ParseFile(filePath);
        var result = LitTestExecutor.Execute(testFile);

        if (!result.Successful)
            throw new Exception($"Lit test failed:{Environment.NewLine}{string.Join(Environment.NewLine, result.ErrorMessages)}");
    }
}
