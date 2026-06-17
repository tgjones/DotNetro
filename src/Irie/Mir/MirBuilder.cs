using Irie.Dialects.Arith;
using Irie.Dialects.Call;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;

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

    public void SetInsertionPointAfter(MirInstruction instruction)
    {
        _block = instruction.Parent!;
        _insertionIndex = _block.Instructions.IndexOf(instruction) + 1;
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

    // arith.constant: materialize an immediate of the given type into a fresh vreg.
    // Used by the legalizer to supply an explicit zero carry-in to the head of an
    // addi_with_carry chain so every link has a uniform 3-use shape.
    public int BuildConstant(IRType type, long value)
    {
        var vreg = function.CreateVirtualRegister(type);
        Insert(ArithDialect.OpRef(ArithOp.Constant), [
            new VirtualReg(vreg, IsDefinition: true),
            new Immediate(value),
        ]);
        return vreg;
    }

    // arith.addi_with_carry: 2 defs (result, carry-out), 3 uses (a, b, carry-in).
    public (int result, int carryOut) BuildAddCarry(IRType type, int a, int b, int carryInVreg)
    {
        var result   = function.CreateVirtualRegister(type);
        var carryOut = function.CreateVirtualRegister(IRType.I1);
        Insert(ArithDialect.OpRef(ArithOp.AddICarry), [
            new VirtualReg(result,      IsDefinition: true),
            new VirtualReg(carryOut,    IsDefinition: true),
            new VirtualReg(a,           IsDefinition: false),
            new VirtualReg(b,           IsDefinition: false),
            new VirtualReg(carryInVreg, IsDefinition: false),
        ]);
        return (result, carryOut);
    }

    // arith.subi_with_borrow: 2 defs (result, borrow-out), 3 uses (a, b, borrow-in).
    // borrow_in/out follow 6502 C-flag polarity (1 = no borrow).
    public (int result, int borrowOut) BuildSubBorrow(IRType type, int a, int b, int borrowInVreg)
    {
        var result    = function.CreateVirtualRegister(type);
        var borrowOut = function.CreateVirtualRegister(IRType.I1);
        Insert(ArithDialect.OpRef(ArithOp.SubIBorrow), [
            new VirtualReg(result,       IsDefinition: true),
            new VirtualReg(borrowOut,    IsDefinition: true),
            new VirtualReg(a,            IsDefinition: false),
            new VirtualReg(b,            IsDefinition: false),
            new VirtualReg(borrowInVreg, IsDefinition: false),
        ]);
        return (result, borrowOut);
    }

    // arith.cmpi <pred>, %a, %b: 1 def (i1 result), 3 uses (predicate Immediate, a, b).
    // The predicate is encoded as the first use Immediate; the writer/parser
    // render it symbolically via ArithDialect.TryFormat/ParseImmediateUse.
    public int BuildCmpI(ArithCmpPredicate predicate, int aVreg, int bVreg)
    {
        var result = function.CreateVirtualRegister(IRType.I1);
        Insert(ArithDialect.OpRef(ArithOp.CmpI), [
            new VirtualReg(result, IsDefinition: true),
            new Immediate((long)predicate),
            new VirtualReg(aVreg,  IsDefinition: false),
            new VirtualReg(bVreg,  IsDefinition: false),
        ]);
        return result;
    }

    // arith.select %cond, %a, %b: 1 def (result, type `valueType`), 3 uses
    // (cond i1, a, b). %r is %a when %cond is true, else %b.
    public int BuildSelect(IRType valueType, int condVreg, int aVreg, int bVreg)
    {
        var result = function.CreateVirtualRegister(valueType);
        Insert(ArithDialect.OpRef(ArithOp.Select), [
            new VirtualReg(result,  IsDefinition: true),
            new VirtualReg(condVreg, IsDefinition: false),
            new VirtualReg(aVreg,    IsDefinition: false),
            new VirtualReg(bVreg,    IsDefinition: false),
        ]);
        return result;
    }

    // call.func @callee, %arg0, %arg1, ... → %r0, %r1, ...
    // Allocates fresh def vregs of the given returnTypes. Caller passes
    // existing arg vregs.
    public int[] BuildCall(string calleeName, IRType[] returnTypes, params int[] argVregs)
    {
        var defs = new int[returnTypes.Length];
        var operands = new MirOperand[returnTypes.Length + 1 + argVregs.Length];
        for (var i = 0; i < returnTypes.Length; i++)
        {
            defs[i] = function.CreateVirtualRegister(returnTypes[i]);
            operands[i] = new VirtualReg(defs[i], IsDefinition: true);
        }
        operands[returnTypes.Length] = new Symbol(calleeName);
        for (var i = 0; i < argVregs.Length; i++)
            operands[returnTypes.Length + 1 + i] = new VirtualReg(argVregs[i], IsDefinition: false);
        Insert(CallDialect.OpRef(CallOp.Func), operands);
        return defs;
    }

    // call.indirect %target, %arg0, %arg1, ... → %r0, %r1, ...
    // Same shape as BuildCall but the callee is an i16 target-pointer vreg
    // instead of a Symbol.
    public int[] BuildCallIndirect(int targetPtrVreg, IRType[] returnTypes, params int[] argVregs)
    {
        var defs = new int[returnTypes.Length];
        var operands = new MirOperand[returnTypes.Length + 1 + argVregs.Length];
        for (var i = 0; i < returnTypes.Length; i++)
        {
            defs[i] = function.CreateVirtualRegister(returnTypes[i]);
            operands[i] = new VirtualReg(defs[i], IsDefinition: true);
        }
        operands[returnTypes.Length] = new VirtualReg(targetPtrVreg, IsDefinition: false);
        for (var i = 0; i < argVregs.Length; i++)
            operands[returnTypes.Length + 1 + i] = new VirtualReg(argVregs[i], IsDefinition: false);
        Insert(CallDialect.OpRef(CallOp.Indirect), operands);
        return defs;
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

    // pseudo.extract: %byte = pseudo.extract %wide, <bit_offset>
    // Carves out a sub-range of `sourceVreg` starting at `bitOffset` bits.
    // The result vreg's type (sub-range width) is determined by `resultType`.
    public int BuildExtract(IRType resultType, int sourceVreg, long bitOffset)
    {
        var result = function.CreateVirtualRegister(resultType);
        Insert(PseudoDialect.OpRef(PseudoOp.Extract), [
            new VirtualReg(result,     IsDefinition: true),
            new VirtualReg(sourceVreg, IsDefinition: false),
            new Immediate(bitOffset),
        ]);
        return result;
    }

    // pseudo.insert: %new = pseudo.insert %wide, %sub, <bit_offset>
    public int BuildInsert(IRType resultType, int wideVreg, int subVreg, long bitOffset)
    {
        var result = function.CreateVirtualRegister(resultType);
        Insert(PseudoDialect.OpRef(PseudoOp.Insert), [
            new VirtualReg(result,   IsDefinition: true),
            new VirtualReg(wideVreg, IsDefinition: false),
            new VirtualReg(subVreg,  IsDefinition: false),
            new Immediate(bitOffset),
        ]);
        return result;
    }

    // mem.symbol @name → %p : i16
    // Returns the i16 pointer vreg holding the address of the named global.
    public int BuildMemSymbol(string symbolName)
    {
        var result = function.CreateVirtualRegister(IRType.Pointer);
        Insert(MemDialect.OpRef(MemOp.Symbol), [
            new VirtualReg(result, IsDefinition: true),
            new Symbol(symbolName),
        ]);
        return result;
    }

    // mem.frame_addr <slot_index> → %p : i16
    // Surface the address of one of the function's reserved frame slots.
    // The slot itself must already be registered on `function.FrameSlots`.
    public int BuildFrameAddr(int slotIndex)
    {
        var result = function.CreateVirtualRegister(IRType.Pointer);
        Insert(MemDialect.OpRef(MemOp.FrameAddr), [
            new VirtualReg(result, IsDefinition: true),
            new Immediate(slotIndex),
        ]);
        return result;
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
