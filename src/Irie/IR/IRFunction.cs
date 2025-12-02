namespace Irie.IR;

public sealed class IRFunction(string name, IRType[] parameterTypes, IRType returnType)
{
    public string Name => name;

    public IRType[] ParameterTypes => parameterTypes;

    public IRType ReturnType => returnType;

    public List<IRBasicBlock> Blocks { get; } = [];

    public IRBasicBlock CreateBasicBlock(Action<IRBasicBlock> configure)
    {
        var block = new IRBasicBlock();
        configure(block);
        Blocks.Add(block);
        return block;
    }
}
