namespace Irie.CodeGen;

public sealed class MachineBasicBlock
{
    // Virtual register IDs of block parameters (the SSA block-argument form used throughout the pipeline).
    public List<int> Parameters { get; } = [];

    public List<MachineInstruction> Instructions { get; } = [];

    public List<MachineBasicBlock> Predecessors { get; } = [];

    public List<MachineBasicBlock> Successors { get; } = [];

    public MachineFunction? Parent { get; internal set; }

    public MachineInstruction AddInstruction(int opcode, params MachineOperand[] operands)
    {
        var instruction = new MachineInstruction(opcode, operands);
        instruction.Parent = this;
        Instructions.Add(instruction);
        return instruction;
    }
}
