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

    // --- Frame-access lowering (the eliminateFrameIndex analogue) ------------
    //
    // The indirect-Y scratch the absolute path uses: the $rc0/$rc1 zero-page
    // pointer pair (matching MOS6502InstructionSelector's PointerZpLo/Hi) plus
    // $y for the indexed offset. These are the physregs the abstract frame op
    // declares clobbered, so they are free here without scavenging.
    private static readonly int PointerZpLo = MOS6502Registers.RC(0); // physreg 12 → $00
    private static readonly int PointerZpHi = MOS6502Registers.RC(1); // physreg 13 → $01

    public override bool IsFrameAccess(MirInstruction instr)
        => instr.Opcode.Dialect == MOS6502Dialect.Id
           && (MOS6502Op)instr.Opcode.Code is MOS6502Op.FrameLoadByte or MOS6502Op.FrameStoreByte;

    // Expand one mos6502.frame.{load,store}.byte into the concrete sequence that
    // addresses the slot. Stage 2: every slot has StackId == DefaultStackId, so
    // the only branch implemented is the absolute global + indirect-Y path,
    // mirroring exactly what MOS6502InstructionSelector emitted for absolute
    // frame globals before this rework. Stage 3 adds the FrameStackIds.ZeroPage
    // branch (a direct lda.zp/sta.zp against the slot's promoted zp address).
    public override void LowerFrameAccess(
        MirFunction function, MirInstruction instr, MirBuilder builder)
    {
        var op = (MOS6502Op)instr.Opcode.Code;
        var (symbol, offset, valueReg) = DecodeFrameAccess(instr, op);
        var slot = ResolveSlot(function, symbol);

        if (slot.StackId != FrameSlot.DefaultStackId)
            throw new NotSupportedException(
                $"MOS6502FrameLowering: frame slot '{symbol}' has non-default StackId " +
                $"{slot.StackId}, but zero-page placement is not implemented until Stage 3.");

        builder.SetInsertionPointBefore(instr);

        // Materialise the slot's absolute address low/high bytes into the
        // $rc0/$rc1 pointer pair, then set $y to the access offset.
        //   $a = lda.imm.symlo @sym ; $rc0 = sta.zp $a
        //   $a = lda.imm.symhi @sym ; $rc1 = sta.zp $a
        //   $y = ldy.imm #off
        EmitSymbolPointerByte(builder, MOS6502Op.LdaImmSymLo, symbol, PointerZpLo);
        EmitSymbolPointerByte(builder, MOS6502Op.LdaImmSymHi, symbol, PointerZpHi);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.LdyImm),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: true),
            new Immediate(offset));

        if (op == MOS6502Op.FrameLoadByte)
        {
            // $a = lda.indy $rc0, implicit-use $rc1, implicit-use $y.
            // The value def is $a (Ac), so the result lands directly where the
            // op modelled it — no extra move.
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.LdaIndY),
                new PhysicalReg(valueReg, IsDefinition: true),
                new PhysicalReg(PointerZpLo, IsDefinition: false),
                new PhysicalReg(PointerZpHi, IsDefinition: false, IsImplicit: true),
                new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));
        }
        else
        {
            // The value byte must be in $a for sta.indy; the store op modelled
            // it in a flexible class (and $a as a clobber), so move it into $a
            // now — a no-op if RA already parked it there.
            if (valueReg != MOS6502Registers.A)
                EmitMoveIntoA(builder, valueReg);

            // sta.indy $rc0, $a, implicit-use $rc1, implicit-use $y.
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.StaIndY),
                new PhysicalReg(PointerZpLo, IsDefinition: false),
                new PhysicalReg(MOS6502Registers.A, IsDefinition: false),
                new PhysicalReg(PointerZpHi, IsDefinition: false, IsImplicit: true),
                new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));
        }

        builder.Remove(instr);
    }

    // Decode a frame access into (slot symbol, byte offset, value physreg).
    //   load:  %v = frame.load.byte @sym, #off    → value=def[0]
    //   store: frame.store.byte @sym, #off, %v     → value=use after the offset
    // Implicit clobber operands ($a/$y/$rc0/$rc1) follow and are ignored here.
    private static (string Symbol, long Offset, int ValueReg) DecodeFrameAccess(
        MirInstruction instr, MOS6502Op op)
    {
        var ops = instr.Operands;
        if (op == MOS6502Op.FrameLoadByte)
        {
            if (ops[0] is not PhysicalReg value || !value.IsDefinition
                || ops[1] is not Symbol sym || ops[2] is not Immediate off)
                throw new InvalidOperationException(
                    "MOS6502FrameLowering: mos6502.frame.load.byte must have shape " +
                    "`$v = frame.load.byte @sym, #off`.");
            return (sym.Name, off.Value, value.Id);
        }

        if (ops[0] is not Symbol ssym || ops[1] is not Immediate soff
            || ops[2] is not PhysicalReg sval || sval.IsDefinition)
            throw new InvalidOperationException(
                "MOS6502FrameLowering: mos6502.frame.store.byte must have shape " +
                "`frame.store.byte @sym, #off, $v`.");
        return (ssym.Name, soff.Value, sval.Id);
    }

    private static FrameSlot ResolveSlot(MirFunction function, string symbol)
    {
        foreach (var slot in function.FrameSlots)
            if (slot.SymbolName == symbol)
                return slot;
        throw new InvalidOperationException(
            $"MOS6502FrameLowering: function @{function.Name} has no frame slot named '{symbol}'.");
    }

    // $a = lda.imm.sym{lo,hi} @sym ; $rcN = sta.zp $a.
    private static void EmitSymbolPointerByte(MirBuilder builder, MOS6502Op ldaOp, string symbol, int zpReg)
    {
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(ldaOp),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: true),
            new Symbol(symbol));
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.StaZp),
            new PhysicalReg(zpReg, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));
    }

    // Move the byte in `src` into $a. $x/$y use the transfer ops; a zero-page
    // register loads via lda.zp. ($a is handled by the caller's no-op guard.)
    private static void EmitMoveIntoA(MirBuilder builder, int src)
    {
        switch (src)
        {
            case MOS6502Registers.X:
                EmitTransfer(builder, MOS6502Op.Txa, MOS6502Registers.A, MOS6502Registers.X);
                break;
            case MOS6502Registers.Y:
                EmitTransfer(builder, MOS6502Op.Tya, MOS6502Registers.A, MOS6502Registers.Y);
                break;
            default:
                builder.BuildInstruction(
                    MOS6502Dialect.OpRef(MOS6502Op.LdaZp),
                    new PhysicalReg(MOS6502Registers.A, IsDefinition: true),
                    new PhysicalReg(src, IsDefinition: false));
                break;
        }
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
