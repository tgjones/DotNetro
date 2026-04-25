namespace Irie.CodeGen;

public sealed class MachineInstruction(int opcode, MachineOperand[] operands)
{
    public int Opcode => opcode;

    // Defs first, then uses — consistent with LLVM convention.
    public MachineOperand[] Operands => operands;

    public MachineBasicBlock? Parent { get; internal set; }
}
