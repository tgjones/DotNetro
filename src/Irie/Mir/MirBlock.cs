namespace Irie.Mir;

public sealed class MirBlock
{
    // Virtual-register IDs of block parameters (typed/classed vregs); empty
    // on the entry block after ABI lowering and on all blocks after RA.
    public List<int> Parameters { get; } = [];

    // Physical-register IDs known to be live on entry to this block.
    // (1) On the entry block after ABI lowering: the calling convention live-ins.
    // (2) On any block after RA: the physical registers live-in per the final
    //     assignment.
    public List<int> LiveIns { get; } = [];

    public List<MirInstruction> Instructions { get; } = [];

    // Populated by MirFunction.RebuildCfg from terminator BlockTarget operands.
    public List<MirBlock> Predecessors { get; } = [];
    public List<MirBlock> Successors   { get; } = [];

    public MirFunction? Parent { get; internal set; }

    public MirInstruction AddInstruction(OpcodeRef opcode, params MirOperand[] operands)
    {
        var instruction = new MirInstruction(opcode, operands);
        instruction.Parent = this;
        Instructions.Add(instruction);
        return instruction;
    }

    public MirInstruction InsertInstruction(int index, OpcodeRef opcode, params MirOperand[] operands)
    {
        var instruction = new MirInstruction(opcode, operands);
        instruction.Parent = this;
        Instructions.Insert(index, instruction);
        return instruction;
    }
}
