namespace Irie.IR;

public sealed class IRReturnInstruction : IRInstruction
{
    public IRReturnInstruction(IRValue operand)
        : base(IRType.Void, [operand])
    {
    }

    public IRReturnInstruction()
        : base(IRType.Void, [])
    {
    }
}
