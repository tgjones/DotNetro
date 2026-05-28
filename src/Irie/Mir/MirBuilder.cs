using Irie.Dialects.Pseudo;
using Irie.IR;

namespace Irie.Mir;

// Insertion-point-based helper for emitting MirInstructions. Mirrors today's
// MachineIRBuilder but talks in terms of OpcodeRef and the new operand types.
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

    public void SetInsertionPointAtStart(MirBlock block)
    {
        _block = block;
        _insertionIndex = 0;
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

    // pseudo.copy from a physical register into a fresh virtual register.
    public int BuildCopyFromPhysicalRegister(int physReg, IRType type)
    {
        var vreg = function.CreateVirtualRegister(type);
        Insert(PseudoDialect.OpRef(PseudoOp.Copy), [
            new VirtualReg(vreg, IsDefinition: true),
            new PhysicalReg(physReg, IsDefinition: false),
        ]);
        return vreg;
    }

    // pseudo.copy from a physical register into an existing virtual register.
    public void BuildCopyFromPhysicalRegisterInto(int destVreg, int physReg)
    {
        Insert(PseudoDialect.OpRef(PseudoOp.Copy), [
            new VirtualReg(destVreg, IsDefinition: true),
            new PhysicalReg(physReg, IsDefinition: false),
        ]);
    }

    // pseudo.copy from a virtual register into a physical register.
    public void BuildCopyToPhysicalRegister(int physReg, int sourceVreg)
    {
        Insert(PseudoDialect.OpRef(PseudoOp.Copy), [
            new PhysicalReg(physReg, IsDefinition: true),
            new VirtualReg(sourceVreg, IsDefinition: false),
        ]);
    }

    // pseudo.merge into an existing wide virtual register. Used by ABI lowering
    // so the merge def reuses the original parameter vreg, leaving downstream
    // uses unchanged.
    public void BuildMergeInto(int existingVreg, int[] sourceVregs)
    {
        var operands = new MirOperand[1 + sourceVregs.Length];
        operands[0] = new VirtualReg(existingVreg, IsDefinition: true);
        for (var i = 0; i < sourceVregs.Length; i++)
            operands[1 + i] = new VirtualReg(sourceVregs[i], IsDefinition: false);
        Insert(PseudoDialect.OpRef(PseudoOp.Merge), operands);
    }

    // pseudo.unmerge a wide vreg into N freshly-allocated narrow vregs.
    public int[] BuildUnmerge(IRType elementType, int sourceVreg, int count)
    {
        var defs = new int[count];
        var operands = new MirOperand[count + 1];
        for (var i = 0; i < count; i++)
        {
            defs[i] = function.CreateVirtualRegister(elementType);
            operands[i] = new VirtualReg(defs[i], IsDefinition: true);
        }
        operands[count] = new VirtualReg(sourceVreg, IsDefinition: false);
        Insert(PseudoDialect.OpRef(PseudoOp.Unmerge), operands);
        return defs;
    }

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
