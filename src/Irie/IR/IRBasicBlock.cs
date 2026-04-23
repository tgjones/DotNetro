namespace Irie.IR;

public sealed class IRBasicBlock() : IRValue(IRType.Label)
{
    public List<IRArgument> Arguments { get; } = [];
    public List<IRInstruction> Instructions { get; } = [];

    public IRArgument CreateArgument(IRType type)
    {
        var argument = new IRArgument(type);
        Arguments.Add(argument);
        return argument;
    }

    public IRInstruction CreateIntegerAdd(IRValue lhs, IRValue rhs)
    {
        return AddInstruction(new IRBinaryOperatorInstruction(BinaryOperatorKind.IntegerAdd, lhs, rhs));
    }

    public IRInstruction CreateIntegerLiteral(IRType type, long value)
    {
        return AddInstruction(new IRIntegerLiteralInstruction(type, value));
    }

    public IRInstruction CreateReturn(IRValue value)
    {
        return AddInstruction(new IRReturnInstruction(value));
    }

    public IRInstruction CreateReturn()
    {
        return AddInstruction(new IRReturnInstruction());
    }

    private IRInstruction AddInstruction(IRInstruction instruction)
    {
        Instructions.Add(instruction);
        return instruction;
    }
}
