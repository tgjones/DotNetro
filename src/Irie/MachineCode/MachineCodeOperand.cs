namespace Irie.MachineCode;

public abstract record MachineCodeOperand
{
    public sealed record Register(int RegNum) : MachineCodeOperand;
    public sealed record Immediate(long Value) : MachineCodeOperand;
    public sealed record LabelRef(string Name) : MachineCodeOperand;
    public sealed record ExternalRef(string Name) : MachineCodeOperand;
}
