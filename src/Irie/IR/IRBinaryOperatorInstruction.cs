namespace Irie.IR;

public sealed class IRBinaryOperatorInstruction : IRInstruction
{
    public BinaryOperatorKind Kind { get; }

    public IRBinaryOperatorInstruction(BinaryOperatorKind kind, IRValue lhs, IRValue rhs)
        : base(lhs.Type, [lhs, rhs])
    {
        Kind = kind;

        if (lhs.Type != rhs.Type)
        {
            throw new ArgumentException("Operand types must match.");
        }
    }
}

public enum BinaryOperatorKind
{
    IntegerAdd,
}