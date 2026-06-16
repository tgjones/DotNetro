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
    // addresses the slot, chosen from the slot's StackId (the eliminateFrameIndex
    // analogue). Two placements:
    //   ZeroPage  — a direct lda.zp/sta.zp against the slot's promoted absolute
    //               zero-page address (slot.Offset + byteOffset). No pointer pair,
    //               no $y, no indirect-Y.
    //   default   — the absolute global + indirect-Y path: build the $rc0/$rc1
    //               pointer pair and set $y to the offset, then LDA/STA ($rc0),Y.
    public override void LowerFrameAccess(
        MirFunction function, MirInstruction instr, MirBuilder builder)
    {
        var op = (MOS6502Op)instr.Opcode.Code;
        var (symbol, offset, valueReg) = DecodeFrameAccess(instr, op);
        var slot = ResolveSlot(function, symbol);

        builder.SetInsertionPointBefore(instr);

        if (slot.StackId == MOS6502FrameStackIds.ZeroPage)
            LowerZeroPageAccess(builder, op, slot, offset, valueReg);
        else
            LowerAbsoluteAccess(builder, op, symbol, offset, valueReg);

        builder.Remove(instr);
    }

    // Direct zero-page access. The slot's Offset is its absolute zp base address
    // (stamped by MOS6502FramePlacementPass); the byte offset is added literally.
    //   load:  $a = lda.zp #addr
    //   store: sta.zp #addr        (value already in $a — the op's preserved use)
    private static void LowerZeroPageAccess(
        MirBuilder builder, MOS6502Op op, FrameSlot slot, long offset, int valueReg)
    {
        var address = slot.Offset + offset;
        if (op == MOS6502Op.FrameLoadByte)
        {
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.LdaZp),
                new PhysicalReg(valueReg, IsDefinition: true),
                new Immediate(address));
        }
        else
        {
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.StaZp),
                new Immediate(address),
                new PhysicalReg(valueReg, IsDefinition: false));
        }
    }

    // Absolute global + indirect-Y access. For a store the value byte is in $a
    // and must survive the pointer setup, so the pointer pair is built through $x
    // (ldx/stx, never lda/sta). The load is free to use $a (it is the result).
    private void LowerAbsoluteAccess(
        MirBuilder builder, MOS6502Op op, string symbol, long offset, int valueReg)
    {
        if (op == MOS6502Op.FrameLoadByte)
        {
            // $a = lda.imm.symlo @sym ; $rc0 = sta.zp $a   (and high → $rc1)
            // $y = ldy.imm #off
            // $a = lda.indy $rc0, implicit-use $rc1, implicit-use $y
            EmitSymbolPointerByteViaA(builder, MOS6502Op.LdaImmSymLo, symbol, PointerZpLo);
            EmitSymbolPointerByteViaA(builder, MOS6502Op.LdaImmSymHi, symbol, PointerZpHi);
            EmitLoadY(builder, offset);
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.LdaIndY),
                new PhysicalReg(valueReg, IsDefinition: true),
                new PhysicalReg(PointerZpLo, IsDefinition: false),
                new PhysicalReg(PointerZpHi, IsDefinition: false, IsImplicit: true),
                new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));
            return;
        }

        // Store: the value is already in $a (a preserved use). Build the pointer
        // pair via $x so $a is untouched, then sta.indy with the value still in $a.
        //   $x = ldx.imm.symlo @sym ; $rc0 = stx.zp $x   (and high → $rc1)
        //   $y = ldy.imm #off
        //   sta.indy $rc0, $a, implicit-use $rc1, implicit-use $y
        EmitSymbolPointerByteViaX(builder, MOS6502Op.LdxImmSymLo, symbol, PointerZpLo);
        EmitSymbolPointerByteViaX(builder, MOS6502Op.LdxImmSymHi, symbol, PointerZpHi);
        EmitLoadY(builder, offset);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.StaIndY),
            new PhysicalReg(PointerZpLo, IsDefinition: false),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false),
            new PhysicalReg(PointerZpHi, IsDefinition: false, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));
    }

    private static void EmitLoadY(MirBuilder builder, long offset)
        => builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.LdyImm),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: true),
            new Immediate(offset));

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

    // $a = lda.imm.sym{lo,hi} @sym ; $rcN = sta.zp $a. Used by the load path,
    // whose result lands in $a anyway.
    private static void EmitSymbolPointerByteViaA(MirBuilder builder, MOS6502Op ldaOp, string symbol, int zpReg)
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

    // $x = ldx.imm.sym{lo,hi} @sym ; $rcN = stx.zp $x. Used by the store path so
    // the value byte already in $a is not disturbed while the pointer is built.
    private static void EmitSymbolPointerByteViaX(MirBuilder builder, MOS6502Op ldxOp, string symbol, int zpReg)
    {
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(ldxOp),
            new PhysicalReg(MOS6502Registers.X, IsDefinition: true),
            new Symbol(symbol));
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.StxZp),
            new PhysicalReg(zpReg, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.X, IsDefinition: false));
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
