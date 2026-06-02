namespace DotLit.Tests;

public class LitTestRunnerTests
{
    [Test]
    [MethodDataSource(nameof(LitTestSource))]
    public void SuccessfulTests(string filePath)
    {
        LitTestRunner.Run(filePath);
    }

    public static IEnumerable<Func<string>> LitTestSource()
    {
        var result = new List<Func<string>>();

        var litTestFiles = Directory.EnumerateFiles(
            "Assets/Successful",
            "*.*",
            SearchOption.AllDirectories);

        foreach (var litTestFile in litTestFiles)
        {
            result.Add(() => litTestFile);
        }

        return result;
    }

    [Test]
    public async Task SimpleHelloWorldFailed()
    {
        await Assert
            .That(() => LitTestRunner.Run("Assets/SimpleHelloWorldFailed.txt"))
            .Throws<Exception>()
            .WithMessageContaining("CHECK failed");
    }

    [Test]
    public async Task OrphanCheckFailed()
    {
        await Assert
            .That(() => LitTestRunner.Run("Assets/OrphanCheckFailed.txt"))
            .Throws<Exception>()
            .WithMessageContaining("No RUN directive for label 'missing'");
    }
}
