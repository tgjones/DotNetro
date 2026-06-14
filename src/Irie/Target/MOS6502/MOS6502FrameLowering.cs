using Irie.Mir;

namespace Irie.Target.MOS6502;

// MOS6502 frame lowering: saves/restores callee-saved zero-page registers
// (RC20..RC31) via the 6502 hardware stack (pha/pla). Mirrors llvm-mos's
// MOSFrameLowering::spillCalleeSavedRegisters / restoreCalleeSavedRegisters,
// which emit `copy $a <- RCn ; PH $a` (= `LDA $nn ; PHA`) because the base 6502
// stack register class is the accumulator.
//
// The wrinkle the base 6502 forces: the only register that can be pushed is the
// accumulator, so the save loop must borrow `$a`. But `$a` holds arg0's low byte
// on entry (a live-in) and the return value's byte0 at the `rts`. `$y` is never
// a CC argument or return register under CC_MOS, so it is provably dead at both
// points; we shuttle the live `$a` through `$y` (`tay` … `tya`) around the spill
// loop. This is a pure post-RA pass — no scratch scavenging, no `$a`-evacuation
// into a zp slot (which is what `llc --target=mos -Os` does instead).
public sealed class MOS6502FrameLowering : Irie.Target.FrameLowering
{
    public override bool IsReturnInstruction(MirInstruction instr)
        => instr.Opcode.Dialect == MOS6502Dialect.Id
           && (MOS6502Op)instr.Opcode.Code == MOS6502Op.Rts;

    public override void EmitCalleeSavedSpills(
        MirBlock entryBlock, IReadOnlyList<int> saved, MirBuilder builder)
    {
        // Under CC_MOS, $y is never an argument register, so it is dead at entry.
        // If a future convention change made it live-in, the tay/tya shuttle
        // below would silently clobber it — fail loudly instead.
        if (entryBlock.LiveIns.Contains(MOS6502Registers.Y))
            throw new InvalidOperationException(
                "MOS6502FrameLowering: $y must not be live-in to the entry block " +
                "(it is borrowed to preserve $a across the callee-saved spill loop).");

        if (entryBlock.Instructions.Count > 0)
            builder.SetInsertionPointBefore(entryBlock.Instructions[0]);
        else
            builder.SetInsertionPointAtEnd(entryBlock);

        // Preserve a live $a (arg0 byte) through $y while $a is borrowed for the
        // lda/pha loop.
        var preserveA = entryBlock.LiveIns.Contains(MOS6502Registers.A);
        if (preserveA)
            EmitTransfer(builder, MOS6502Op.Tay, MOS6502Registers.Y, MOS6502Registers.A);

        // saved is ascending; push in ascending order (popped in descending).
        foreach (var reg in saved)
        {
            // $a = lda.zp $reg
            builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.LdaZp),
                new PhysicalReg(MOS6502Registers.A, IsDefinition: true),
                new PhysicalReg(reg, IsDefinition: false));
            // pha (implied — pushes $a)
            builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Pha));
        }

        if (preserveA)
            EmitTransfer(builder, MOS6502Op.Tya, MOS6502Registers.A, MOS6502Registers.Y);
    }

    public override void EmitCalleeSavedRestores(
        MirInstruction returnInstr, IReadOnlyList<int> saved, MirBuilder builder)
    {
        // $y is never a return register under CC_MOS, so it is dead at every rts.
        if (ReturnUses(returnInstr, MOS6502Registers.Y))
            throw new InvalidOperationException(
                "MOS6502FrameLowering: $y must not be a use on the return instruction " +
                "(it is borrowed to preserve $a across the callee-saved restore loop).");

        builder.SetInsertionPointBefore(returnInstr);

        // Preserve a live $a (return-value byte0, carried as an implicit use on
        // the rts) through $y while $a is borrowed for the pla/sta loop.
        var preserveA = ReturnUses(returnInstr, MOS6502Registers.A);
        if (preserveA)
            EmitTransfer(builder, MOS6502Op.Tay, MOS6502Registers.Y, MOS6502Registers.A);

        // Restore in descending order — LIFO, the reverse of the spill order.
        for (var i = saved.Count - 1; i >= 0; i--)
        {
            // pla (implied — pulls into $a)
            builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.Pla));
            // $reg = sta.zp $a
            builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.StaZp),
                new PhysicalReg(saved[i], IsDefinition: true),
                new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
        }

        if (preserveA)
            EmitTransfer(builder, MOS6502Op.Tya, MOS6502Registers.A, MOS6502Registers.Y);
    }

    private static void EmitTransfer(MirBuilder builder, MOS6502Op op, int dst, int src)
        => builder.BuildInstruction(MOS6502Dialect.OpRef(op),
            new PhysicalReg(dst, IsDefinition: true),
            new PhysicalReg(src, IsDefinition: false));

    // True if `physReg` appears as a use operand on the return instruction
    // (e.g. an implicit-use return-value byte left there by SelectReturn).
    private static bool ReturnUses(MirInstruction returnInstr, int physReg)
    {
        foreach (var op in returnInstr.Operands)
            if (op is PhysicalReg { IsDefinition: false } p && p.Id == physReg)
                return true;
        return false;
    }
}
