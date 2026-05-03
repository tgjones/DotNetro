namespace Irie.CodeGen;

public sealed class MachineInstruction(int opcode, MachineOperand[] operands)
{
    public int Opcode => opcode;

    // Defs first, then uses.
    public MachineOperand[] Operands { get; internal set; } = operands;

    public MachineBasicBlock? Parent { get; internal set; }
}
