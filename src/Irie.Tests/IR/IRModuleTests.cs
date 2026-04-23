using Irie.IR;

namespace DotNetro.Compiler.Tests.IR;

public class IRModuleTests
{
    [Test]
    public void CanBuildSimpleFunctionWithNoArgsAndNoReturn()
    {
        var module = new IRModule();

        module.CreateFunction("TestFunction", [], IRType.Void, function =>
        {
            function.CreateBasicBlock(block =>
            {
                var a = block.CreateIntegerLiteral(IRType.I32, 42);
                var b = block.CreateIntegerLiteral(IRType.I32, 1);

                var result = block.CreateIntegerAdd(a, b);

                block.CreateReturn(result);
            });
        });

        module.Write(Console.Out);
    }

    [Test]
    public void CanBuildSimpleFunctionWithTwoArgsAndReturn()
    {
        var module = new IRModule();

        module.CreateFunction("TestFunction", [IRType.I32, IRType.I32], IRType.I32, function =>
        {
            function.CreateBasicBlock(block =>
            {
                var a = block.CreateArgument(IRType.I32);
                var b = block.CreateArgument(IRType.I32);

                var result = block.CreateIntegerAdd(a, b);

                block.CreateReturn(result);
            });
        });

        module.Write(Console.Out);
    }

    [Test]
    public async Task RoundTrip_SimpleFunctionWithNoArgsAndNoReturn()
    {
        var module = new IRModule();
        module.CreateFunction("TestFunction", [], IRType.Void, function =>
        {
            function.CreateBasicBlock(block =>
            {
                var a = block.CreateIntegerLiteral(IRType.I32, 42);
                var b = block.CreateIntegerLiteral(IRType.I32, 1);
                var result = block.CreateIntegerAdd(a, b);
                block.CreateReturn(result);
            });
        });

        var sw1 = new StringWriter();
        module.Write(sw1);
        var ir1 = sw1.ToString();

        var parsedModule = IRModule.Parse(new StringReader(ir1));

        var sw2 = new StringWriter();
        parsedModule.Write(sw2);
        var ir2 = sw2.ToString();

        await Assert.That(ir2).IsEqualTo(ir1);
    }

    [Test]
    public async Task RoundTrip_SimpleFunctionWithTwoArgsAndReturn()
    {
        var module = new IRModule();
        module.CreateFunction("TestFunction", [IRType.I32, IRType.I32], IRType.I32, function =>
        {
            function.CreateBasicBlock(block =>
            {
                var a = block.CreateArgument(IRType.I32);
                var b = block.CreateArgument(IRType.I32);
                var result = block.CreateIntegerAdd(a, b);
                block.CreateReturn(result);
            });
        });

        var sw1 = new StringWriter();
        module.Write(sw1);
        var ir1 = sw1.ToString();

        var parsedModule = IRModule.Parse(new StringReader(ir1));

        var sw2 = new StringWriter();
        parsedModule.Write(sw2);
        var ir2 = sw2.ToString();

        await Assert.That(ir2).IsEqualTo(ir1);
    }
}
