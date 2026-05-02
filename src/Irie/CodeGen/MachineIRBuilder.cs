using Irie.IR;

namespace Irie.CodeGen;

// Notified about instruction insertions/erasures performed via MachineIRBuilder.
// Used by the legalizer to keep its worklists in sync as combines mutate the IR.
public interface IMachineFunctionObserver
{
    void OnInstructionCreated(MachineInstruction instruction);
    void OnInstructionErased(MachineInstruction instruction);
}

public sealed class MachineIRBuilder(MachineFunction function)
{
    private MachineBasicBlock? _block;
    private int _insertionIndex;
    private IMachineFunctionObserver? _observer;

    public MachineFunction Function => function;

    public void SetObserver(IMachineFunctionObserver? observer) => _observer = observer;

    public void SetInsertionPointAtEnd(MachineBasicBlock block)
    {
        _block = block;
        _insertionIndex = block.Instructions.Count;
    }

    public void SetInsertionPointBefore(MachineInstruction instruction)
    {
        _block = instruction.Parent!;
        _insertionIndex = _block.Instructions.IndexOf(instruction);
    }

    // Mark a physical register as live-in on the current block (calling convention metadata).
    public void AddLiveIn(int physReg) => _block!.LiveIns.Add(physReg);

    // GenericCopy from a physical register into a new virtual register.
    public int BuildCopyFromPhysicalRegister(int physReg, IRType type)
    {
        var vreg = function.CreateVirtualRegister(type);
        Insert(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(vreg, IsDefinition: true),
            new PhysicalRegisterOperand(physReg, IsDefinition: false));
        return vreg;
    }

    // GenericCopy from a virtual register into a physical register.
    public void BuildCopyToPhysicalRegister(int physReg, int sourceVreg)
    {
        Insert(GenericOpcode.GenericCopy,
            new PhysicalRegisterOperand(physReg, IsDefinition: true),
            new VirtualRegisterOperand(sourceVreg, IsDefinition: false));
    }

    // GenericCopy between two virtual registers.
    public void BuildCopyVirtualToVirtual(int destVreg, int sourceVreg)
    {
        Insert(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(destVreg, IsDefinition: true),
            new VirtualRegisterOperand(sourceVreg, IsDefinition: false));
    }

    // GenericConstant: materialize an immediate of the given type into a fresh vreg.
    // Used by the legalizer to provide an explicit zero carry-in to the head of an
    // AddCarry chain (mirrors llvm-mos's `Builder.buildConstant(S1, 0)` in
    // MOSLegalizerInfo::legalizeAddSubO).
    public int BuildConstant(IRType type, long value)
    {
        var vreg = function.CreateVirtualRegister(type);
        Insert(GenericOpcode.GenericConstant,
            new VirtualRegisterOperand(vreg, IsDefinition: true),
            new ImmediateOperand(value));
        return vreg;
    }

    // GenericMerge: N narrow values → 1 new wide virtual register.
    public int BuildMerge(IRType resultType, int[] sourceVregs)
    {
        var resultVreg = function.CreateVirtualRegister(resultType);
        BuildMergeInto(resultVreg, sourceVregs);
        return resultVreg;
    }

    // GenericMerge: N narrow values → an existing wide virtual register.
    // Used when the legalizer replaces a wide instruction and must reuse the original def vreg.
    public void BuildMergeInto(int existingVreg, int[] sourceVregs)
    {
        var operands = new MachineOperand[1 + sourceVregs.Length];
        operands[0] = new VirtualRegisterOperand(existingVreg, IsDefinition: true);
        for (var i = 0; i < sourceVregs.Length; i++)
            operands[1 + i] = new VirtualRegisterOperand(sourceVregs[i], IsDefinition: false);
        Insert(GenericOpcode.GenericMerge, operands);
    }

    // GenericUnmerge: 1 wide value → N new narrow virtual registers (LSB first).
    public int[] BuildUnmerge(IRType elementType, int sourceVreg, int count)
    {
        var defs = new int[count];
        var operands = new MachineOperand[count + 1];
        for (var i = 0; i < count; i++)
        {
            defs[i] = function.CreateVirtualRegister(elementType);
            operands[i] = new VirtualRegisterOperand(defs[i], IsDefinition: true);
        }
        operands[count] = new VirtualRegisterOperand(sourceVreg, IsDefinition: false);
        Insert(GenericOpcode.GenericUnmerge, operands);
        return defs;
    }

    // GenericAddCarry with a virtual-register carry-in.
    public (int result, int carryOut) BuildAddCarry(IRType type, int a, int b, int carryInVreg)
    {
        var result   = function.CreateVirtualRegister(type);
        var carryOut = function.CreateVirtualRegister(IRType.I1);
        Insert(GenericOpcode.GenericAddCarry,
            new VirtualRegisterOperand(result,      IsDefinition: true),
            new VirtualRegisterOperand(carryOut,    IsDefinition: true),
            new VirtualRegisterOperand(a,           IsDefinition: false),
            new VirtualRegisterOperand(b,           IsDefinition: false),
            new VirtualRegisterOperand(carryInVreg, IsDefinition: false));
        return (result, carryOut);
    }

    // Emit an arbitrary target-specific instruction with no defs.
    // If operandClasses is non-null, applies per-operand register class constraints
    // (mirrors LLVM's constrainSelectedInstRegOperands).
    public void BuildTargetInstruction(int opcode, MachineOperand[] operands, int[]? operandClasses = null)
    {
        var instr = Insert(opcode, operands);
        ApplyOperandClasses(instr, operandClasses);
    }

    // Emit an arbitrary target-specific instruction with one virtual-register def.
    public int BuildTargetInstructionWithDefinition(
        int opcode, IRType defType, MachineOperand[] useOperands, int[]? operandClasses = null)
    {
        var vreg = function.CreateVirtualRegister(defType);
        var operands = new MachineOperand[1 + useOperands.Length];
        operands[0] = new VirtualRegisterOperand(vreg, IsDefinition: true);
        useOperands.CopyTo(operands, 1);
        var instr = Insert(opcode, operands);
        ApplyOperandClasses(instr, operandClasses);
        return vreg;
    }

    // Emit an arbitrary target-specific instruction with multiple virtual-register defs.
    public int[] BuildTargetInstructionWithDefinitions(
        int opcode, IRType[] defTypes, MachineOperand[] useOperands, int[]? operandClasses = null)
    {
        var vregs = new int[defTypes.Length];
        var operands = new MachineOperand[defTypes.Length + useOperands.Length];
        for (var i = 0; i < defTypes.Length; i++)
        {
            vregs[i] = function.CreateVirtualRegister(defTypes[i]);
            operands[i] = new VirtualRegisterOperand(vregs[i], IsDefinition: true);
        }
        useOperands.CopyTo(operands, defTypes.Length);
        var instr = Insert(opcode, operands);
        ApplyOperandClasses(instr, operandClasses);
        return vregs;
    }

    // Mirrors LLVM's constrainSelectedInstRegOperands: for each operand position
    // with a non-zero class entry, constrain the underlying vreg to that class.
    // Positions referring to immediates / blocks / phys regs / external symbols
    // are silently skipped (the class entry is expected to be None for those).
    private void ApplyOperandClasses(MachineInstruction instr, int[]? operandClasses)
    {
        if (operandClasses == null) return;
        var operands = instr.Operands;
        var limit = Math.Min(operands.Length, operandClasses.Length);
        for (var i = 0; i < limit; i++)
        {
            var classId = operandClasses[i];
            if (classId == 0) continue;
            if (operands[i] is VirtualRegisterOperand vreg)
                function.SetVirtualRegisterClass(vreg.VirtualRegister, classId);
        }
    }

    // Remove an instruction from its block and mark it as removed (Parent = null).
    public void Remove(MachineInstruction instruction)
    {
        instruction.Parent?.Instructions.Remove(instruction);
        instruction.Parent = null;
        _observer?.OnInstructionErased(instruction);
    }

    private MachineInstruction Insert(int opcode, params MachineOperand[] operands)
    {
        var instruction = new MachineInstruction(opcode, operands);
        instruction.Parent = _block!;
        _block!.Instructions.Insert(_insertionIndex, instruction);
        _insertionIndex++;
        _observer?.OnInstructionCreated(instruction);
        return instruction;
    }
}
