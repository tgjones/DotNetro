using DotLit.Model;

namespace DotLit.Tests;

public class LitTestExecutorTests
{
    [Test]
    public async Task CanExecuteSimpleSuccessfulCheck()
    {
        var testFile = new TestFile(
        [
            new RunCommand("echo Hello, World!"),
            new CheckCommand("Hello"),
        ]);

        var testResult = LitTestExecutor.Execute(testFile);

        await Assert.That(testResult.Successful).IsTrue();
        await Assert.That(testResult.ErrorMessages).IsEmpty();
        await Assert.That(testResult.RunOutput).IsEqualTo("");
    }
}
