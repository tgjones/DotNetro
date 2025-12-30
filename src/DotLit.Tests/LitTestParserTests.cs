using DotLit.Model;

namespace DotLit.Tests;

public class LitTestParserTests
{
    [Test]
    public async Task CanParseSimpleLitTest()
    {
        var testFileContents = """
            // RUN: echo Hello, World!
            // CHECK: Hello
            """;

        var testFile = LitTestParser.ParseString(testFileContents);

        await Assert.That(testFile.Commands).IsEquivalentTo(new TestCommand[]
        {
            new RunCommand("echo Hello, World!"),
            new CheckCommand("Hello")
        });
    }
}
