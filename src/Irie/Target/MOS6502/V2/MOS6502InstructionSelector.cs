using Irie.Dialects.Arith;
using Irie.Dialects.Pseudo;
using Irie.IR;
using Irie.Mir;

namespace Irie.Target.MOS6502.V2;

// MOS6502 instruction selector for the unified-MIR pipeline. Mirrors the old
// Irie.Target.MOS6502.MOS6502InstructionSelector while talking in OpcodeRef +
// MirOperand. Selection rules:
//
//   - arith.constant 0 : i1   →  mos6502.clc  (def: class Cc vreg)
//   - arith.constant 1 : i1   →  mos6502.sec  (def: class Cc vreg)
//   - arith.addi_with_carry   →  mos6502.adc  (pre-AMS form; classes / tied
//                                              metadata from MOS6502Dialect)
//   - pseudo.return           →  mos6502.rts  with implicit-uses of every
//                                              physreg defined by a preceding
//                                              pseudo.copy in the same block
//                                              (i.e. the return physregs the
//                                              call lowering populated)
//   - pseudo.copy / merge / unmerge   pass through (RA and later passes
//                                                   handle them)
//   - already-selected mos6502.* ops  pass through
//
// Classes are applied by:
//   - creating new defs via CreateVirtualRegisterInClass, and
//   - reclassifying existing typed vreg uses via ReclassifyVirtualRegister.
public sealed class MOS6502InstructionSelector : Irie.Target.InstructionSelector
{
    public override bool Select(MirInstruction instruction, MirBuilder builder)
    {
        var opcode = instruction.Opcode;

        if (opcode.Dialect == ArithDialect.Id)
        {
            return (ArithOp)opcode.Code switch
            {
                ArithOp.Constant  => SelectConstant(instruction, builder),
                ArithOp.AddICarry => SelectAddCarry(instruction, builder),
                _ => false,
            };
        }

        if (opcode.Dialect == PseudoDialect.Id)
        {
            return (PseudoOp)opcode.Code switch
            {
                PseudoOp.Copy    => true,
                PseudoOp.Merge   => true,
                PseudoOp.Unmerge => true,
                PseudoOp.Return  => SelectReturn(instruction, builder),
                _ => false,
            };
        }

        // Already-selected target ops pass through.
        if (opcode.Dialect == MOS6502Dialect.Id) return true;

        return false;
    }

    // arith.constant 0 : i1 → mos6502.clc with a fresh class-Cc vreg def.
    // arith.constant 1 : i1 → mos6502.sec, ditto.
    // The original constant vreg is RAUW'd to the new clc/sec def so the
    // downstream addi_with_carry chain head sees a properly-classed carry-in.
    private static bool SelectConstant(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        var defOp = (VirtualReg)instr.Operands[0];
        var immOp = (Immediate)instr.Operands[1];

        var annotation = function.GetVRegAnnotation(defOp.Id);
        if (annotation is not TypedVReg typed || typed.Type != IRType.I1)
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: arith.constant of {annotation} is not yet supported.");

        var targetOp = immOp.Value switch
        {
            0 => MOS6502Op.Clc,
            1 => MOS6502Op.Sec,
            _ => throw new NotSupportedException(
                $"MOS6502InstructionSelector: arith.constant {immOp.Value} : i1 is not yet supported."),
        };

        var newDef = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(targetOp),
            new VirtualReg(newDef, IsDefinition: true));

        function.ReplaceAllUsesOfRegister(defOp.Id, newDef);
        builder.Remove(instr);
        return true;
    }

    // arith.addi_with_carry → mos6502.adc (pre-AMS). New defs are created in
    // class Ac/Cc; existing typed-vreg uses are reclassified to Ac (a),
    // Imag8 (b), Cc (carry-in).
    private static bool SelectAddCarry(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;

        // Operand layout: def[0]=result, def[1]=carry_out, use[0]=a, use[1]=b, use[2]=carry_in
        var resultVreg   = ((VirtualReg)instr.Operands[0]).Id;
        var carryOutVreg = ((VirtualReg)instr.Operands[1]).Id;
        var aVreg        = ((VirtualReg)instr.Operands[2]).Id;
        var bVreg        = ((VirtualReg)instr.Operands[3]).Id;
        var carryInVreg  = ((VirtualReg)instr.Operands[4]).Id;

        ReclassifyTo(function, aVreg,       MOS6502RegisterClass.Ac);
        ReclassifyTo(function, bVreg,       MOS6502RegisterClass.Imag8);
        ReclassifyTo(function, carryInVreg, MOS6502RegisterClass.Cc);

        var newResult = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        var newCarry  = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Adc),
            new VirtualReg(newResult,   IsDefinition: true),
            new VirtualReg(newCarry,    IsDefinition: true),
            new VirtualReg(aVreg,       IsDefinition: false),
            new VirtualReg(bVreg,       IsDefinition: false),
            new VirtualReg(carryInVreg, IsDefinition: false));

        function.ReplaceAllUsesOfRegister(resultVreg,   newResult);
        function.ReplaceAllUsesOfRegister(carryOutVreg, newCarry);
        builder.Remove(instr);
        return true;
    }

    // pseudo.return → mos6502.rts. The return physregs are surfaced as
    // implicit uses on the rts, gathered by scanning the preceding
    // `$reg = pseudo.copy %v` instructions in the same block (the ABI
    // lowering emitted those for each return byte just before the
    // pseudo.return).
    private static bool SelectReturn(MirInstruction instr, MirBuilder builder)
    {
        var block = instr.Parent!;
        var implicitUses = new List<MirOperand>();

        foreach (var prev in block.Instructions)
        {
            if (ReferenceEquals(prev, instr)) break;
            if (prev.Opcode.Dialect != PseudoDialect.Id) continue;
            if ((PseudoOp)prev.Opcode.Code != PseudoOp.Copy) continue;
            if (prev.Operands.Length == 0) continue;

            if (prev.Operands[0] is PhysicalReg phys && phys.IsDefinition)
                implicitUses.Add(new PhysicalReg(phys.Id, IsDefinition: false, IsImplicit: true));
        }

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Rts),
            implicitUses.ToArray());

        builder.Remove(instr);
        return true;
    }

    private static void ReclassifyTo(MirFunction function, int vreg, int classId)
    {
        var name = MOS6502RegisterClass.GetName(classId)!;
        var annotation = function.GetVRegAnnotation(vreg);
        if (annotation is ClassedVReg existing && existing.ClassId == classId) return;
        function.ReclassifyVirtualRegister(vreg, classId, name);
    }
}
