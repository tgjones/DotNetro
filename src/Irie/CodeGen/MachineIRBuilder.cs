using Irie.IR;

namespace Irie.CodeGen;

public sealed class MachineIRBuilder(MachineFunction function)
{
    private MachineBasicBlock? _block;
    private int _insertionIndex;

    public MachineFunction Function => function;

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
    public int BuildCopyFromPhysReg(int physReg, IRType type)
    {
        var vreg = function.CreateVirtualRegister(type);
        Insert(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(vreg, IsDefinition: true),
            new PhysicalRegisterOperand(physReg, IsDefinition: false));
        return vreg;
    }

    // GenericCopy from a virtual register into a physical register.
    public void BuildCopyToPhysReg(int physReg, int sourceVreg)
    {
        Insert(GenericOpcode.GenericCopy,
            new PhysicalRegisterOperand(physReg, IsDefinition: true),
            new VirtualRegisterOperand(sourceVreg, IsDefinition: false));
    }

    // GenericCopy between two virtual registers.
    public void BuildCopyVirtToVirt(int destVreg, int sourceVreg)
    {
        Insert(GenericOpcode.GenericCopy,
            new VirtualRegisterOperand(destVreg, IsDefinition: true),
            new VirtualRegisterOperand(sourceVreg, IsDefinition: false));
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

    // GenericAddCarry with an immediate carry-in (used for the initial carry = 0).
    public (int result, int carryOut) BuildAddCarryImm(IRType type, int a, int b, long carryImm)
    {
        var result   = function.CreateVirtualRegister(type);
        var carryOut = function.CreateVirtualRegister(IRType.I1);
        Insert(GenericOpcode.GenericAddCarry,
            new VirtualRegisterOperand(result,   IsDefinition: true),
            new VirtualRegisterOperand(carryOut, IsDefinition: true),
            new VirtualRegisterOperand(a, IsDefinition: false),
            new VirtualRegisterOperand(b, IsDefinition: false),
            new ImmediateOperand(carryImm));
        return (result, carryOut);
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

    // GenericReturn with no operands (return value already in physical registers via copies).
    public void BuildReturn()
    {
        Insert(GenericOpcode.GenericReturn);
    }

    // Emit an arbitrary target-specific instruction with no defs.
    public void BuildTargetInstr(int opcode)
    {
        Insert(opcode);
    }

    // Emit an arbitrary target-specific instruction with one virtual-register def.
    public int BuildTargetInstrWithDef(int opcode, IRType defType, params MachineOperand[] useOperands)
    {
        var vreg = function.CreateVirtualRegister(defType);
        var operands = new MachineOperand[1 + useOperands.Length];
        operands[0] = new VirtualRegisterOperand(vreg, IsDefinition: true);
        useOperands.CopyTo(operands, 1);
        Insert(opcode, operands);
        return vreg;
    }

    // Remove an instruction from its block and mark it as removed (Parent = null).
    public void Remove(MachineInstruction instruction)
    {
        instruction.Parent?.Instructions.Remove(instruction);
        instruction.Parent = null;
    }

    private void Insert(int opcode, params MachineOperand[] operands)
    {
        var instruction = new MachineInstruction(opcode, operands);
        instruction.Parent = _block!;
        _block!.Instructions.Insert(_insertionIndex, instruction);
        _insertionIndex++;
    }
}
