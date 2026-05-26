namespace Irie.Mir;

public sealed class MirInstruction(OpcodeRef opcode, MirOperand[] operands)
{
    public OpcodeRef Opcode { get; } = opcode;

    // Defs first, then uses.
    public MirOperand[] Operands { get; internal set; } = operands;

    public MirBlock? Parent { get; internal set; }
}
