namespace Irie.Mir;

// Insertion-point-based helper for emitting MirInstructions. Mirrors today's
// MachineIRBuilder but talks in terms of OpcodeRef and the new operand types.
// Dialect-specific Build* helpers (e.g. for pseudo.copy, arith.addi_with_carry,
// ...) layer on top of this in later steps; for now only the core insertion
// mechanics are provided.
public sealed class MirBuilder(MirFunction function)
{
    private MirBlock? _block;
    private int _insertionIndex;
    private IMirObserver? _observer;

    public MirFunction Function => function;

    public void SetObserver(IMirObserver? observer) => _observer = observer;

    public void SetInsertionPointAtEnd(MirBlock block)
    {
        _block = block;
        _insertionIndex = block.Instructions.Count;
    }

    public void SetInsertionPointBefore(MirInstruction instruction)
    {
        _block = instruction.Parent!;
        _insertionIndex = _block.Instructions.IndexOf(instruction);
    }

    // Mark a physical register as live-in on the current block.
    public void AddLiveIn(int physReg) => _block!.LiveIns.Add(physReg);

    // Emit an instruction at the current insertion point. Defs first in
    // operands, then uses, per dialect convention.
    public MirInstruction BuildInstruction(OpcodeRef opcode, params MirOperand[] operands) =>
        Insert(opcode, operands);

    // Remove an instruction from its block and detach it (Parent = null).
    public void Remove(MirInstruction instruction)
    {
        instruction.Parent?.Instructions.Remove(instruction);
        instruction.Parent = null;
        _observer?.OnInstructionErased(instruction);
    }

    private MirInstruction Insert(OpcodeRef opcode, MirOperand[] operands)
    {
        var instruction = new MirInstruction(opcode, operands);
        instruction.Parent = _block!;
        _block!.Instructions.Insert(_insertionIndex, instruction);
        _insertionIndex++;
        _observer?.OnInstructionCreated(instruction);
        return instruction;
    }
}
