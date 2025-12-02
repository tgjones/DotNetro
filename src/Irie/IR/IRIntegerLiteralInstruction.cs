namespace Irie.IR;

public sealed class IRIntegerLiteralInstruction : IRInstruction
{
    public long Value { get; }

    public IRIntegerLiteralInstruction(IRType type, long value)
        : base(type, [])
    {
        Value = value;
    }
}
