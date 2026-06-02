using Irie.Dialects.Arith;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

// MOS6502 legalizer for the unified-MIR pipeline. Mirrors the old
// Irie.Target.MOS6502.MOS6502LegalizerInfo while talking in OpcodeRef and the
// new dialect opcodes (arith.addi, arith.addi_with_carry, arith.constant,
// pseudo.merge, pseudo.unmerge, pseudo.copy).
public sealed class MOS6502LegalizerInfo : Irie.Target.LegalizerInfo
{
    public override LegalityAction GetAction(OpcodeRef opcode, IRType type)
    {
        if (type is not IntegerType intType)
            return LegalityAction.Unsupported;

        if (opcode.Dialect == ArithDialect.Id)
        {
            return ((ArithOp)opcode.Code) switch
            {
                ArithOp.AddI       when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.AddI       when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                ArithOp.SubI       when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.SubI       when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                ArithOp.AddICarry  => LegalityAction.Legal,
                ArithOp.SubIBorrow => LegalityAction.Legal,
                // arith.cmpi's first def is i1 (the boolean result), so the
                // type query here is always i1. The narrowing decision is
                // driven by the operand types — see GetCmpINarrowType.
                ArithOp.CmpI       => LegalityAction.Legal,
                ArithOp.Constant   when intType.SizeInBits == 1  => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == PseudoDialect.Id)
        {
            return ((PseudoOp)opcode.Code) switch
            {
                PseudoOp.Copy    => LegalityAction.Legal,
                PseudoOp.Merge   => LegalityAction.Legal,
                PseudoOp.Unmerge => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == MemDialect.Id)
        {
            // mem.symbol produces an i16 pointer that the isel pattern-matches
            // through to the per-byte lda.imm.symlo/symhi ops.
            //
            // mem.load.i8 / mem.store.i8: rewritten to mem.load.byte_at /
            // mem.store.byte_at with offset 0 by Custom legalization.
            //
            // mem.load.i16/i32, mem.store.i16/i32: narrowed to a chain of
            // mem.load.byte_at / mem.store.byte_at at consecutive offsets,
            // wrapped in pseudo.unmerge / pseudo.merge to splice into / out
            // of the wider vreg. Same Custom path.
            //
            // mem.load.byte_at / mem.store.byte_at: the post-legalization
            // form the isel knows how to lower to lda.indy / sta.indy.
            return ((MemOp)opcode.Code) switch
            {
                MemOp.Symbol      when intType.SizeInBits == 16 => LegalityAction.Legal,
                MemOp.LoadI8      when intType.SizeInBits == 8  => LegalityAction.Custom,
                MemOp.LoadI16     when intType.SizeInBits == 16 => LegalityAction.Custom,
                MemOp.LoadI32     when intType.SizeInBits == 32 => LegalityAction.Custom,
                MemOp.StoreI8     when intType.SizeInBits == 8  => LegalityAction.Custom,
                MemOp.StoreI16    when intType.SizeInBits == 16 => LegalityAction.Custom,
                MemOp.StoreI32    when intType.SizeInBits == 32 => LegalityAction.Custom,
                MemOp.LoadByteAt  when intType.SizeInBits == 8  => LegalityAction.Legal,
                MemOp.StoreByteAt when intType.SizeInBits == 8  => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        return LegalityAction.Unsupported;
    }

    public override IRType GetNarrowType(OpcodeRef opcode, IRType fromType)
    {
        if (opcode.Dialect == ArithDialect.Id
            && ((ArithOp)opcode.Code == ArithOp.AddI || (ArithOp)opcode.Code == ArithOp.SubI))
            return IRType.I8;

        throw new NotSupportedException(
            $"MOS6502LegalizerInfo: no narrow type defined for opcode {opcode}");
    }

    // Custom legalization for the mem dialect: rewrites typed mem.load / mem.store
    // ops into per-byte mem.load.byte_at / mem.store.byte_at chains. The byte
    // form carries an Immediate offset that the isel feeds into the indirect-Y
    // addressing mode's Y register, so consecutive bytes of a wide load/store
    // read or write from address+0, address+1, …
    public override void LegalizeCustom(MirInstruction instr, MirBuilder builder)
    {
        if (instr.Opcode.Dialect != MemDialect.Id)
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: no Custom legalization for {instr.Opcode}");

        switch ((MemOp)instr.Opcode.Code)
        {
            case MemOp.LoadI8:
            case MemOp.LoadI16:
            case MemOp.LoadI32:
                LowerWideLoad(instr, builder);
                return;

            case MemOp.StoreI8:
            case MemOp.StoreI16:
            case MemOp.StoreI32:
                LowerWideStore(instr, builder);
                return;

            default:
                throw new NotSupportedException(
                    $"MOS6502LegalizerInfo: no Custom legalization for mem opcode {(MemOp)instr.Opcode.Code}");
        }
    }

    // %v : iN = mem.load.iN %p  →  per-byte mem.load.byte_at chain + pseudo.merge.
    // The merge re-uses the original wide vreg as its def so downstream uses are
    // untouched.
    private static void LowerWideLoad(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg defReg || !defReg.IsDefinition
            || instr.Operands[1] is not VirtualReg addrReg || addrReg.IsDefinition)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: mem.load.iN must have shape `%def : iN = mem.load.iN %addr`.");
        }

        var defType = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type;
        var byteCount = defType.SizeInBits / 8;

        var byteVregs = new int[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            byteVregs[i] = function.CreateVirtualRegister(IRType.I8);
            builder.BuildInstruction(
                MemDialect.OpRef(MemOp.LoadByteAt),
                new VirtualReg(byteVregs[i], IsDefinition: true),
                new VirtualReg(addrReg.Id,   IsDefinition: false),
                new Immediate(i));
        }

        if (byteCount == 1)
        {
            // Single byte: just RAUW the def. The byte vreg replaces the
            // wide def vreg (both i8 here — the wide form was already i8).
            function.ReplaceAllUsesOfRegister(defReg.Id, byteVregs[0]);
        }
        else
        {
            // Reuse the original wide vreg as the merge's def so downstream
            // consumers keep their operands intact.
            builder.BuildMergeInto(defReg.Id, byteVregs);
        }

        builder.Remove(instr);
    }

    // mem.store.iN %p, %v  →  pseudo.unmerge %v + per-byte mem.store.byte_at chain.
    // For a single-byte store the unmerge is skipped (the value vreg is fed
    // straight into mem.store.byte_at at offset 0).
    private static void LowerWideStore(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg addrReg || addrReg.IsDefinition
            || instr.Operands[1] is not VirtualReg valReg  || valReg.IsDefinition)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: mem.store.iN must have shape `mem.store.iN %addr, %val`.");
        }

        var valType = ((TypedVReg)function.GetVRegAnnotation(valReg.Id)).Type;
        var byteCount = valType.SizeInBits / 8;

        int[] byteVregs;
        if (byteCount == 1)
        {
            byteVregs = [valReg.Id];
        }
        else
        {
            byteVregs = builder.BuildUnmerge(IRType.I8, valReg.Id, byteCount);
        }

        for (var i = 0; i < byteCount; i++)
        {
            builder.BuildInstruction(
                MemDialect.OpRef(MemOp.StoreByteAt),
                new VirtualReg(addrReg.Id,   IsDefinition: false),
                new Immediate(i),
                new VirtualReg(byteVregs[i], IsDefinition: false));
        }

        builder.Remove(instr);
    }
}
