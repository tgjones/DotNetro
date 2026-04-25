namespace Irie.MachineCode;

public abstract record MachineCodeEntry;

public sealed record MachineCodeLabel(string Name) : MachineCodeEntry;

public sealed record MachineCodeInstruction(int Opcode, MachineCodeOperand[] Operands) : MachineCodeEntry;
