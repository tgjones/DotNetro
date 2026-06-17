using Irie.Dialects.Arith;
using Irie.Dialects.Cf;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

// MOS6502 instruction selector for the unified-MIR pipeline. Mirrors the old
// Irie.Target.MOS6502.MOS6502InstructionSelector while talking in OpcodeRef +
// MirOperand. Selection rules:
//
//   - arith.constant 0 : i1   →  mos6502.clc  (def: class Cc vreg)
//   - arith.constant 1 : i1   →  mos6502.sec  (def: class Cc vreg)
//   - arith.addi_with_carry   →  mos6502.adc  (pre-AMS form; classes / tied
//                                              metadata from MOS6502Dialect)
//   - arith.cmpi + cf.cond_br →  mos6502.cmp + mos6502.b<pred> + mos6502.jmp.abs
//                                fused into a 3-op sequence; the i1 cmpi result
//                                never reaches RA. Only triggers when the cmpi
//                                is immediately followed by a cond_br consuming
//                                its def, in the same block, with exactly one
//                                use. Bare cmpi (no cond_br) is unsupported.
//   - cf.br                   →  mos6502.jmp.abs (single BlockTarget)
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
    // Per-function tracker: a key identifying the pointer whose low/high bytes
    // are *currently* materialised in PointerZpLo / PointerZpHi. The scratch
    // pair is a single pair shared by every mem op, so the pointer setup can
    // only be elided when the next access targets the *same* pointer that is
    // already in the pair (e.g. bytes 1..3 of one multi-byte load/store).
    // Switching to a different pointer must re-materialise the pair, otherwise
    // the second access would dereference the first pointer. The key is
    // "@name" for a mem.symbol address and "%<vreg>" for a runtime i16 pointer
    // (an address vreg defined by a pseudo.merge of two byte vregs). Isel
    // visits instructions in program order, so tracking the last-loaded pointer
    // is sufficient. Cleared per function and invalidated at each call (jsr) —
    // a call clobbers the shared pointer pair, so a cached setup cannot be
    // reused across it (see the JsrAbs case in Select).
    private string? _currentPointerKey;

    public override void BeginFunction(MirFunction function)
    {
        _currentPointerKey = null;
    }

    public override bool Select(MirInstruction instruction, MirBuilder builder)
    {
        var opcode = instruction.Opcode;

        if (opcode.Dialect == ArithDialect.Id)
        {
            return (ArithOp)opcode.Code switch
            {
                ArithOp.Constant   => SelectConstant(instruction, builder),
                ArithOp.AddI       => SelectAddI(instruction, builder),
                ArithOp.SubI       => SelectSubI(instruction, builder),
                ArithOp.AddICarry  => SelectAddCarry(instruction, builder),
                ArithOp.SubIBorrow => SelectSubBorrow(instruction, builder),
                ArithOp.CmpI       => SelectCmpI(instruction, builder),
                _ => false,
            };
        }

        if (opcode.Dialect == CfDialect.Id)
        {
            return (CfOp)opcode.Code switch
            {
                CfOp.Br     => SelectBr(instruction, builder),
                // cf.cond_br is consumed by SelectCmpI; reaching it here means
                // it was never fused with a preceding cmpi.
                CfOp.CondBr => throw new NotSupportedException(
                    "MOS6502InstructionSelector: bare cf.cond_br (without a preceding " +
                    "arith.cmpi) is not yet supported. The cmpi+cond_br fusion path " +
                    "handles cond_br only when fused."),
                _ => false,
            };
        }

        if (opcode.Dialect == PseudoDialect.Id)
        {
            return (PseudoOp)opcode.Code switch
            {
                PseudoOp.Copy    => true,
                PseudoOp.Merge   => true,
                PseudoOp.Unmerge => SelectUnmerge(instruction, builder),
                PseudoOp.Return  => SelectReturn(instruction, builder),
                _ => false,
            };
        }

        if (opcode.Dialect == MemDialect.Id)
        {
            return (MemOp)opcode.Code switch
            {
                // mem.symbol passes through; it is removed by SelectMemLoad /
                // SelectMemStore when its last use is consumed.
                MemOp.Symbol      => true,
                MemOp.LoadByteAt  => SelectMemLoadByteAt(instruction, builder),
                MemOp.StoreByteAt => SelectMemStoreByteAt(instruction, builder),
                _ => false,
            };
        }

        // Already-selected target ops pass through. A call (jsr) clobbers the
        // shared zero-page pointer pair the mem-indirect lowering parks addresses
        // in (the runtime helpers use it as scratch), so any cached pointer setup
        // is stale afterwards: invalidate it so the next mem access re-materialises
        // the pair instead of dereferencing a clobbered pointer.
        if (opcode.Dialect == MOS6502Dialect.Id)
        {
            if ((MOS6502Op)opcode.Code == MOS6502Op.JsrAbs)
                _currentPointerKey = null;
            return true;
        }

        return false;
    }

    // arith.constant N : iK → target op carrying the immediate.
    //   i1: 0 → mos6502.clc, 1 → mos6502.sec (flag-class vreg def).
    //   i8: a fresh Anyi8 vreg is defined via `pseudo.copy <imm>`. The pseudo
    //       expander turns that into LDA/LDX/LDY-immediate (or a zp store via
    //       $a) depending on the RA's chosen physreg.
    private static bool SelectConstant(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        var defOp = (VirtualReg)instr.Operands[0];
        var immOp = (Immediate)instr.Operands[1];

        var annotation = function.GetVRegAnnotation(defOp.Id);
        if (annotation is not TypedVReg typed)
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: arith.constant of {annotation} is not yet supported.");

        if (typed.Type == IRType.I1)
        {
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

        if (typed.Type == IRType.I8)
        {
            // %byte : any8 = pseudo.copy <imm>
            // PseudoExpansion turns this into an LDA/LDX/LDY-imm followed by
            // an STA-to-zp if the RA picked a zero-page slot.
            var newDef = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Anyi8,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);

            builder.SetInsertionPointBefore(instr);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(newDef, IsDefinition: true),
                new Immediate(immOp.Value));

            function.ReplaceAllUsesOfRegister(defOp.Id, newDef);
            builder.Remove(instr);
            return true;
        }

        throw new NotSupportedException(
            $"MOS6502InstructionSelector: arith.constant of {typed.Type.DisplayName} is not yet supported.");
    }

    // arith.addi_with_carry → mos6502.adc (pre-AMS). New defs are created in
    // class Ac/Cc; existing typed-vreg uses are reclassified to Ac (a),
    // Imag8 (b), Cc (carry-in).
    //
    // Operand layout: def[0]=result, def[1]=carry_out, use[0]=a, use[1]=b,
    //                 use[2]=carry_in (the carry-in's def — an arith.constant —
    //                 was already selected into a clc/sec earlier in the block).
    private static bool SelectAddCarry(MirInstruction instr, MirBuilder builder)
        => SelectCarryBorrowOp(
            instr, builder, MOS6502Op.Adc,
            resultVreg:  ((VirtualReg)instr.Operands[0]).Id,
            flagOutVreg: ((VirtualReg)instr.Operands[1]).Id,
            aVreg:       ((VirtualReg)instr.Operands[2]).Id,
            bVreg:       ((VirtualReg)instr.Operands[3]).Id,
            flagInVreg:  ((VirtualReg)instr.Operands[4]).Id);

    // arith.subi_with_borrow → mos6502.sbc (pre-AMS). Same operand layout,
    // class assignment, and tied-def shape as add-with-carry — the only
    // difference is the emitted opcode tag.
    private static bool SelectSubBorrow(MirInstruction instr, MirBuilder builder)
        => SelectCarryBorrowOp(
            instr, builder, MOS6502Op.Sbc,
            resultVreg:  ((VirtualReg)instr.Operands[0]).Id,
            flagOutVreg: ((VirtualReg)instr.Operands[1]).Id,
            aVreg:       ((VirtualReg)instr.Operands[2]).Id,
            bVreg:       ((VirtualReg)instr.Operands[3]).Id,
            flagInVreg:  ((VirtualReg)instr.Operands[4]).Id);

    // arith.addi : i8 → clc + mos6502.adc. The bare (carry-less) byte add is
    // legal on this target (wider adds narrow to an addi_with_carry chain in the
    // legalizer; an i8 add never does), so the selector supplies the carry chain
    // head itself: it emits a clc into a fresh carry-class vreg and feeds that as
    // the carry-in, then reuses the addi_with_carry lowering. The carry-out is
    // unused — a fresh dead Cc vreg absorbs it. Mirrors llvm-mos selecting a
    // legal S8 G_ADD directly.
    private static bool SelectAddI(MirInstruction instr, MirBuilder builder)
        => SelectBareAddSub(instr, builder, MOS6502Op.Adc, MOS6502Op.Clc);

    // arith.subi : i8 → sec + mos6502.sbc. Carry-in head is a SEC (6502 "no
    // borrow"), mirroring the subi_with_borrow chain head. See SelectAddI.
    private static bool SelectSubI(MirInstruction instr, MirBuilder builder)
        => SelectBareAddSub(instr, builder, MOS6502Op.Sbc, MOS6502Op.Sec);

    private static bool SelectBareAddSub(
        MirInstruction instr, MirBuilder builder, MOS6502Op aluOp, MOS6502Op carryHeadOp)
    {
        var function = builder.Function;

        // Bare operand layout: def[0]=result, use[0]=a, use[1]=b (no carry).
        var resultVreg = ((VirtualReg)instr.Operands[0]).Id;
        var aVreg      = ((VirtualReg)instr.Operands[1]).Id;
        var bVreg      = ((VirtualReg)instr.Operands[2]).Id;

        // Emit the carry-chain head (clc/sec) into a fresh carry-class vreg, and
        // a fresh dead carry-class vreg to absorb the alu's carry-out. Inserting
        // before `instr` keeps the clc/sec ahead of the adc/sbc the core emits.
        builder.SetInsertionPointBefore(instr);
        var carryInVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc, MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(carryHeadOp),
            new VirtualReg(carryInVreg, IsDefinition: true));
        var deadCarryOut = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc, MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        return SelectCarryBorrowOp(
            instr, builder, aluOp,
            resultVreg, flagOutVreg: deadCarryOut, aVreg, bVreg, flagInVreg: carryInVreg);
    }

    private static bool SelectCarryBorrowOp(
        MirInstruction instr, MirBuilder builder, MOS6502Op targetOp,
        int resultVreg, int flagOutVreg, int aVreg, int bVreg, int flagInVreg)
    {
        var function = builder.Function;

        // Stage B: fold a single-use static-symbol load into the ALU's memory
        // operand, mirroring llvm-mos m_FoldedLdAbs. `A = A op mem` reads the
        // folded byte directly from `@sym`, dropping the separate `lda.abs` +
        // copy. The accumulator (use[0], tied to def[0]) holds the *other*
        // operand; `adc` is commutative so either input may become the memory
        // operand, but `sbc` is not — only the subtrahend (b) may fold.
        var absOp = targetOp == MOS6502Op.Adc ? MOS6502Op.AdcAbs : MOS6502Op.SbcAbs;
        if (TryMatchFoldableAbsLoad(function, bVreg, instr) is { } foldB)
            return EmitFoldedAbsCarryBorrow(
                builder, instr, absOp, resultVreg, flagOutVreg, accVreg: aVreg, flagInVreg, foldB);
        if (targetOp == MOS6502Op.Adc && TryMatchFoldableAbsLoad(function, aVreg, instr) is { } foldA)
            return EmitFoldedAbsCarryBorrow(
                builder, instr, absOp, resultVreg, flagOutVreg, accVreg: bVreg, flagInVreg, foldA);

        // Do NOT pin aVreg/bVreg to a *single-physreg* class. The class-
        // intersection gap (plan §3.1) arises when a single value is pinned `Ac`
        // here and used as a zero-page (`Imag8`) operand elsewhere: Ac ∩ Imag8 =
        // ∅ in RA. Keep both operands broad instead:
        //   - use[0] (a) is *tied* to def[0], so TwoAddressInstructionPass
        //     materialises it as a fresh copy that picks up the `Ac` operand
        //     constraint from AdcInfo/SbcInfo. It reads the tied use's class to
        //     classify that copy, so aVreg must carry *a* class — but the broad
        //     flexible class (`Anyi8`), not `Ac`. That keeps aVreg free to live
        //     in zero page too while still satisfying the pass.
        //   - use[1] (b) keeps the `Imag8` operand constraint from the dialect
        //     info directly; a broad bVreg intersects to zero page in RA, which
        //     is correct. With no `Ac` pin reaching bVreg, Ac ∩ Imag8 can't arise.
        // The carry-in genuinely must live in the carry register and has no
        // multi-role conflict, so it keeps its in-place class.
        ReclassifyTo(function, aVreg,      MOS6502RegisterClass.Anyi8);
        ReclassifyTo(function, flagInVreg, MOS6502RegisterClass.Cc);

        var newResult = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        var newFlag   = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(targetOp),
            new VirtualReg(newResult,  IsDefinition: true),
            new VirtualReg(newFlag,    IsDefinition: true),
            new VirtualReg(aVreg,      IsDefinition: false),
            new VirtualReg(bVreg,      IsDefinition: false),
            new VirtualReg(flagInVreg, IsDefinition: false));

        // Funnel the result out of $a into a flexible Anyi8 vreg (mirrors the
        // EOR path's out-funnel). The adc/sbc architecturally produces its
        // result in $a (def[0] = Ac, tied to use[0]), but a result later used as
        // a zero-page operand would otherwise intersect Ac ∩ Imag8 = ∅. Routing
        // downstream uses through `resOut : any8` keeps the value flexible; the
        // coalescer collapses this copy whenever $a is free for the chain.
        var resOut = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Anyi8,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(resOut,    IsDefinition: true),
            new VirtualReg(newResult, IsDefinition: false));

        function.ReplaceAllUsesOfRegister(resultVreg,  resOut);
        function.ReplaceAllUsesOfRegister(flagOutVreg, newFlag);
        builder.Remove(instr);
        return true;
    }

    // A folded absolute-symbol load: the Symbol the ALU op will read directly,
    // plus the now-dead `lda.abs` and its funnel `pseudo.copy` to erase.
    private readonly record struct AbsLoadFold(Symbol Symbol, MirInstruction Lda, MirInstruction Copy);

    // Match `%v = pseudo.copy %lda` where `%lda = mos6502.lda.abs @sym`, both
    // single-use, with no memory write between the load and `consumer` (which
    // would make folding the load past it unsound). Returns the symbol + the two
    // instructions to erase, or null if the pattern / safety check fails.
    private static AbsLoadFold? TryMatchFoldableAbsLoad(MirFunction function, int vreg, MirInstruction consumer)
    {
        if (function.GetUseCount(vreg) != 1) return null;

        var copy = function.GetDefinition(vreg);
        if (copy is null
            || copy.Opcode.Dialect != PseudoDialect.Id
            || (PseudoOp)copy.Opcode.Code != PseudoOp.Copy
            || copy.Operands.Length != 2
            || copy.Operands[1] is not VirtualReg copySrc)
            return null;

        if (function.GetUseCount(copySrc.Id) != 1) return null;

        var lda = function.GetDefinition(copySrc.Id);
        if (lda is null
            || lda.Opcode != MOS6502Dialect.OpRef(MOS6502Op.LdaAbs)
            || lda.Operands.Length != 2
            || lda.Operands[1] is not Symbol symbol)
            return null;

        // The load can only sink to its single use if nothing writes memory in
        // between. The selector always emits the load and its consumer in the
        // same block; bail otherwise.
        if (lda.Parent is not { } block || consumer.Parent != block) return null;
        var ldaIndex = block.Instructions.IndexOf(lda);
        var consumerIndex = block.Instructions.IndexOf(consumer);
        for (var i = ldaIndex + 1; i < consumerIndex; i++)
            if (MayWriteMemory(block.Instructions[i]))
                return null;

        return new AbsLoadFold(symbol, lda, copy);
    }

    // Emit `%r, %c = mos6502.{adc,sbc}.abs %acc, @sym, %carryIn`, funnel the
    // result out of $a into a flexible Anyi8 vreg (the same out-funnel the plain
    // adc/sbc path uses), and erase the original op plus the now-dead load.
    private static bool EmitFoldedAbsCarryBorrow(
        MirBuilder builder, MirInstruction instr, MOS6502Op absOp,
        int resultVreg, int flagOutVreg, int accVreg, int flagInVreg, AbsLoadFold fold)
    {
        var function = builder.Function;

        // The accumulator (use[0], tied to def[0]) carries the broad Anyi8 class
        // for the same reason the plain path keeps `a` flexible; the carry-in
        // lives in the carry register.
        ReclassifyTo(function, accVreg,    MOS6502RegisterClass.Anyi8);
        ReclassifyTo(function, flagInVreg, MOS6502RegisterClass.Cc);

        var newResult = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        var newFlag   = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(absOp),
            new VirtualReg(newResult,  IsDefinition: true),
            new VirtualReg(newFlag,    IsDefinition: true),
            new VirtualReg(accVreg,    IsDefinition: false),
            fold.Symbol,
            new VirtualReg(flagInVreg, IsDefinition: false));

        var resOut = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Anyi8,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(resOut,    IsDefinition: true),
            new VirtualReg(newResult, IsDefinition: false));

        function.ReplaceAllUsesOfRegister(resultVreg,  resOut);
        function.ReplaceAllUsesOfRegister(flagOutVreg, newFlag);
        builder.Remove(instr);

        // The load now has no remaining uses (both single-use checks passed).
        builder.Remove(fold.Copy);
        builder.Remove(fold.Lda);
        return true;
    }

    // Conservative aliasing test for the absolute-load fold: does this
    // instruction possibly write memory (so a load may not sink past it)?
    // pseudo.copy moves only between registers (incl. RA-assigned zero-page
    // scratch, which cannot alias an absolute global). Any not-yet-lowered
    // generic op is treated as a possible writer. Every mos6502 store / memory
    // read-modify-write / stack push / call / frame access is a writer.
    private static bool MayWriteMemory(MirInstruction instr)
    {
        if (instr.Opcode.Dialect == PseudoDialect.Id)
            return false;
        if (instr.Opcode.Dialect != MOS6502Dialect.Id)
            return true;

        return (MOS6502Op)instr.Opcode.Code is
            MOS6502Op.Sta or MOS6502Op.Stx or MOS6502Op.Sty
            or MOS6502Op.StaZp or MOS6502Op.StaZpX or MOS6502Op.StaAbs or MOS6502Op.StaAbsX
            or MOS6502Op.StaAbsY or MOS6502Op.StaIndX or MOS6502Op.StaIndY
            or MOS6502Op.StxZp or MOS6502Op.StxZpY or MOS6502Op.StxAbs
            or MOS6502Op.StyZp or MOS6502Op.StyZpX or MOS6502Op.StyAbs
            or MOS6502Op.Inc or MOS6502Op.Dec
            or MOS6502Op.IncZp or MOS6502Op.IncZpX or MOS6502Op.IncAbs or MOS6502Op.IncAbsX
            or MOS6502Op.DecZp or MOS6502Op.DecZpX or MOS6502Op.DecAbs or MOS6502Op.DecAbsX
            or MOS6502Op.Asl or MOS6502Op.Lsr or MOS6502Op.Rol or MOS6502Op.Ror
            or MOS6502Op.AslZp or MOS6502Op.AslZpX or MOS6502Op.AslAbs or MOS6502Op.AslAbsX
            or MOS6502Op.LsrZp or MOS6502Op.LsrZpX or MOS6502Op.LsrAbs or MOS6502Op.LsrAbsX
            or MOS6502Op.RolZp or MOS6502Op.RolZpX or MOS6502Op.RolAbs or MOS6502Op.RolAbsX
            or MOS6502Op.RorZp or MOS6502Op.RorZpX or MOS6502Op.RorAbs or MOS6502Op.RorAbsX
            or MOS6502Op.FrameLoadByte or MOS6502Op.FrameStoreByte
            or MOS6502Op.Pha or MOS6502Op.Php or MOS6502Op.JsrAbs or MOS6502Op.Brk;
    }

    // pseudo.return → mos6502.rts. The return physregs are surfaced as
    // implicit uses on the rts, gathered by scanning backward from the
    // pseudo.return for a *contiguous* run of `$reg = pseudo.copy %v` (or
    // `$reg = pseudo.copy $reg`) instructions: AbiLowering.LowerReturn emits
    // exactly that run immediately before pseudo.return. Scanning only the
    // contiguous run avoids picking up unrelated physreg-target copies
    // emitted earlier in the block (e.g. the indirect-Y pointer setup that
    // mem.load/store lowering inserts).
    private static bool SelectReturn(MirInstruction instr, MirBuilder builder)
    {
        var block = instr.Parent!;
        var implicitUses = new List<MirOperand>();

        var index = block.Instructions.IndexOf(instr);
        for (var i = index - 1; i >= 0; i--)
        {
            var prev = block.Instructions[i];
            if (prev.Opcode.Dialect != PseudoDialect.Id) break;
            if ((PseudoOp)prev.Opcode.Code != PseudoOp.Copy) break;
            if (prev.Operands.Length == 0) break;
            if (prev.Operands[0] is not PhysicalReg phys || !phys.IsDefinition) break;
            implicitUses.Insert(0, new PhysicalReg(phys.Id, IsDefinition: false, IsImplicit: true));
        }

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Rts),
            implicitUses.ToArray());

        builder.Remove(instr);
        return true;
    }

    // cf.br T → mos6502.jmp.abs T.
    private static bool SelectBr(MirInstruction instr, MirBuilder builder)
    {
        if (instr.Operands.Length != 1 || instr.Operands[0] is not BlockTarget target)
            throw new InvalidOperationException(
                "MOS6502InstructionSelector: cf.br must carry exactly one BlockTarget operand.");

        builder.SetInsertionPointBefore(instr);
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.JmpAbs), target);
        builder.Remove(instr);
        return true;
    }

    // arith.cmpi <pred> + cf.cond_br %cond, T, F → mos6502.cmp + mos6502.b<pred> T
    // + mos6502.jmp.abs F. Only fires when:
    //   - The cmpi's def has exactly one use.
    //   - The use is a cf.cond_br in the same block, *immediately* after the
    //     cmpi (no instructions between).
    //   - The operand types are i8 (multi-byte cmp deferred — see plan §6 step 15).
    // For now: signed predicates (slt/sgt/sle/sge) emit SBC + synthetic branches
    // by deferring through `mos6502.sbc` — out of scope for step 3; throw.
    // Initial coverage: eq, ne, ult, uge (the straightforward CMP-only predicates).
    private static bool SelectCmpI(MirInstruction cmpi, MirBuilder builder)
    {
        var function = builder.Function;
        var block = cmpi.Parent!;

        // Operand layout: def i1, predicate Immediate, a vreg, b vreg.
        if (cmpi.Operands.Length != 4)
            throw new InvalidOperationException(
                "MOS6502InstructionSelector: arith.cmpi must have 4 operands (def, predicate, a, b).");

        var condVreg = ((VirtualReg)cmpi.Operands[0]).Id;
        var predicate = (ArithCmpPredicate)((Immediate)cmpi.Operands[1]).Value;
        var aVreg = ((VirtualReg)cmpi.Operands[2]).Id;
        // The RHS is normally a vreg, but the legalizer narrows a signed
        // compare-against-zero to `arith.cmpi slt/sge, %high : i8, 0` with an
        // immediate 0 — the high-byte sign test below reads it directly.
        var bOperand = cmpi.Operands[3];

        // The compare width comes from the `a` operand. It is a TypedVReg when the
        // value is still generic (a local / ABI value), or a ClassedVReg when it
        // has already been instruction-selected (e.g. a byte produced by the
        // mem-load lowering, which reclassifies its result to Anyi8 before this
        // cmpi is reached). Every MOS6502 register class is a single byte, so a
        // ClassedVReg operand is always an i8 compare.
        var aByteWidth = function.GetVRegAnnotation(aVreg) switch
        {
            TypedVReg aTyped => aTyped.Type.SizeInBits / 8,
            ClassedVReg => 1,
            var other => throw new NotSupportedException(
                $"MOS6502InstructionSelector: arith.cmpi on operand %{aVreg} with annotation " +
                $"{other?.ToString() ?? "none"} is not supported."),
        };

        // Multi-byte cmpi (i16, i32) widens to a per-byte chain of CMP +
        // conditional branches via the target-private path below. Only
        // unsigned / equality predicates are supported in this first cut;
        // signed predicates use SBC + N⊕V and are deferred.
        if (aByteWidth > 1)
        {
            // Wide compares reaching the selector always carry a vreg RHS (the
            // immediate-0 form is only produced for the narrowed i8 sign test).
            var bVregWide = ((VirtualReg)bOperand).Id;
            return SelectCmpIMultiByte(cmpi, builder, predicate, aVreg, bVregWide, condVreg, aByteWidth);
        }

        // Find the cond_br that consumes this cmpi.
        var cmpiIndex = block.Instructions.IndexOf(cmpi);
        if (cmpiIndex < 0 || cmpiIndex + 1 >= block.Instructions.Count)
            throw new NotSupportedException(
                "MOS6502InstructionSelector: arith.cmpi without a following cf.cond_br is not yet supported.");

        var next = block.Instructions[cmpiIndex + 1];
        if (next.Opcode.Dialect != CfDialect.Id || (CfOp)next.Opcode.Code != CfOp.CondBr)
            throw new NotSupportedException(
                "MOS6502InstructionSelector: arith.cmpi must be immediately followed by cf.cond_br.");

        // cf.cond_br operand layout: cond (vreg), T (BlockTarget), F (BlockTarget).
        if (next.Operands.Length != 3
            || next.Operands[0] is not VirtualReg condUse || condUse.IsDefinition
            || condUse.Id != condVreg
            || next.Operands[1] is not BlockTarget tTarget
            || next.Operands[2] is not BlockTarget fTarget)
            throw new InvalidOperationException(
                "MOS6502InstructionSelector: malformed cf.cond_br operand shape.");

        if (function.GetUseCount(condVreg) != 1)
            throw new NotSupportedException(
                "MOS6502InstructionSelector: arith.cmpi whose result is used outside cf.cond_br " +
                "is not yet supported (i1 result materialisation deferred).");

        builder.SetInsertionPointBefore(cmpi);

        // The legalizer normalizes every compare to the canonical {eq, uge, slt}
        // set the 6502 flags express directly, so only those three reach here:
        //   - eq  → CMP + BEQ;  uge → CMP + BCS.
        //   - slt against an immediate 0 → a sign test: CMP #0 sets N from bit 7,
        //     BMI branches when negative.
        //   - general slt → bias both operands by EOR #$80 (mirrors the multi-byte
        //     signed path), so the unsigned CMP + BCC computes signed less-than.
        // The value funneled into $a (cmpAVreg) and the CMP's RHS (cmpRhs) vary by
        // case; the shared funnel + CMP + branch sequence below emits all three.
        var cmpAVreg = aVreg;
        MirOperand cmpRhs;
        MOS6502Op branchOp;
        switch (predicate)
        {
            case ArithCmpPredicate.Eq:
                branchOp = MOS6502Op.Beq;
                cmpRhs = new VirtualReg(((VirtualReg)bOperand).Id, IsDefinition: false);
                break;
            case ArithCmpPredicate.Uge:
                branchOp = MOS6502Op.Bcs;
                cmpRhs = new VirtualReg(((VirtualReg)bOperand).Id, IsDefinition: false);
                break;
            case ArithCmpPredicate.Slt when bOperand is Immediate { Value: 0 }:
                branchOp = MOS6502Op.Bmi;
                cmpRhs = new Immediate(0);
                break;
            case ArithCmpPredicate.Slt:
                cmpAVreg = EorByteWithImmediate(function, builder, aVreg, 0x80);
                var bFlipped = EorByteWithImmediate(function, builder, ((VirtualReg)bOperand).Id, 0x80);
                branchOp = MOS6502Op.Bcc;
                cmpRhs = new VirtualReg(bFlipped, IsDefinition: false);
                break;
            default:
                throw new NotSupportedException(
                    $"MOS6502InstructionSelector: arith.cmpi predicate '{ArithCmpPredicateNames.ToText(predicate)}' " +
                    "reached the i8 selector; after legalizer normalization only eq, uge, slt are expected.");
        }

        // Funnel the (possibly sign-biased) %a through a fresh `Ac` vreg instead
        // of pinning it in place. cmp's use[0] is *not* tied, so an in-place `Ac`
        // reclassify would be a direct pin — and if the same value is also used as
        // a zero-page operand elsewhere, Ac ∩ Imag8 = ∅ in RA (plan §3.2). Routing
        // the `Ac` requirement through a copy leaves the source broad; the
        // coalescer collapses the copy whenever $a is free. The b operand stays
        // broad as use[1]; the `Imag8` operand constraint from CmpInfo applies to
        // it directly and, with no `Ac` pin reaching it, cannot conflict.
        var aCmpVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(aCmpVreg, IsDefinition: true),
            new VirtualReg(cmpAVreg, IsDefinition: false));

        // mos6502.cmp %a_cmp, <rhs> implicit-def $n, implicit-def $z, implicit-def $c
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Cmp),
            new VirtualReg(aCmpVreg, IsDefinition: false),
            cmpRhs,
            new PhysicalReg(MOS6502Registers.N, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Z, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.C, IsDefinition: true, IsImplicit: true));

        // mos6502.b<pred> T implicit <flag>
        var implicitFlag = branchOp switch
        {
            MOS6502Op.Beq or MOS6502Op.Bne => MOS6502Registers.Z,
            MOS6502Op.Bcc or MOS6502Op.Bcs => MOS6502Registers.C,
            MOS6502Op.Bmi or MOS6502Op.Bpl => MOS6502Registers.N,
            _ => throw new InvalidOperationException($"Unhandled branch op {branchOp}"),
        };

        builder.BuildInstruction(
            MOS6502Dialect.OpRef(branchOp),
            tTarget,
            new PhysicalReg(implicitFlag, IsDefinition: false, IsImplicit: true));

        // mos6502.jmp.abs F
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.JmpAbs), fTarget);

        builder.Remove(next);
        builder.Remove(cmpi);
        return true;
    }

    // Multi-byte cmpi+cond_br: emit a chain of per-byte CMPs with appropriate
    // conditional branches. For predicate p on N-byte operands a, b:
    //
    //   for each byte i from high to low:
    //     CMP a[i], b[i]
    //     <p-specific exit branches that may go to T or F>
    //   ; (after all bytes, the byte-wise check has fallen through every layer)
    //   <p-specific tail action>
    //
    // For eq:  any BNE → F (early exit). If we reach the end, all equal → T.
    // For ult: BCC → T (a < b); BNE → F (a > b in high byte means a >= b).
    //          On reaching the last byte unequal: BCC → T; else F.
    //          On full equality: F.
    // For uge: BCC → F (a < b); BNE → T (a > b ⇒ a >= b).
    //          On reaching the last byte unequal: BCS → T; else F.
    //          On full equality: T.
    //
    // After legalizer normalization only the canonical {eq, uge, slt} predicates
    // reach here; slt runs the ult chain on sign-biased operands (see below).
    private static bool SelectCmpIMultiByte(
        MirInstruction cmpi,
        MirBuilder builder,
        ArithCmpPredicate predicate,
        int aVreg,
        int bVreg,
        int condVreg,
        int byteCount)
    {
        if (predicate is not (ArithCmpPredicate.Eq or ArithCmpPredicate.Uge or ArithCmpPredicate.Slt))
        {
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: multi-byte arith.cmpi predicate '{ArithCmpPredicateNames.ToText(predicate)}' " +
                "reached the selector; after legalizer normalization only eq, uge, slt are expected.");
        }

        // The signed `slt` compare reduces to the unsigned `ult` chain after
        // flipping the sign bit (bit 7 of the most-significant byte) of both
        // operands:  a <_signed b  ==  (a ^ msb_mask) <_unsigned (b ^ msb_mask),
        // where msb_mask has only the whole-value sign bit set. The flip is
        // emitted below, after the bytes are funneled.
        var signedFlip = predicate is ArithCmpPredicate.Slt;
        var chainPredicate = signedFlip ? ArithCmpPredicate.Ult : predicate;

        var function = builder.Function;
        var block = cmpi.Parent!;

        // Locate the following cond_br (same pattern as the i8 path).
        var cmpiIndex = block.Instructions.IndexOf(cmpi);
        if (cmpiIndex < 0 || cmpiIndex + 1 >= block.Instructions.Count)
            throw new NotSupportedException(
                "MOS6502InstructionSelector: arith.cmpi without a following cf.cond_br is not yet supported.");

        var next = block.Instructions[cmpiIndex + 1];
        if (next.Opcode.Dialect != CfDialect.Id || (CfOp)next.Opcode.Code != CfOp.CondBr)
            throw new NotSupportedException(
                "MOS6502InstructionSelector: arith.cmpi must be immediately followed by cf.cond_br.");
        if (next.Operands.Length != 3
            || next.Operands[0] is not VirtualReg condUse || condUse.IsDefinition
            || condUse.Id != condVreg
            || next.Operands[1] is not BlockTarget tTarget
            || next.Operands[2] is not BlockTarget fTarget)
            throw new InvalidOperationException(
                "MOS6502InstructionSelector: malformed cf.cond_br operand shape.");
        if (function.GetUseCount(condVreg) != 1)
            throw new NotSupportedException(
                "MOS6502InstructionSelector: arith.cmpi whose result is used outside cf.cond_br is not yet supported.");

        // Note: the signed compare-against-zero shape (`x <s 0` / `x >=s 0`) is
        // narrowed to a single high-byte sign test in the legalizer
        // (MOS6502LegalizerInfo.LegalizeCmpI), so it never reaches this wide
        // path — only general unsigned/equality chains and signed non-zero
        // compares (via the EOR #$80 sign bias below) do.
        builder.SetInsertionPointBefore(cmpi);

        // Get the byte vregs that make up each operand. If the operand is
        // defined by a pseudo.merge of the right arity, reuse its part vregs
        // directly (sidestepping the wide vreg that has no register class).
        // Otherwise emit a fresh pseudo.unmerge — but then the wide vreg's
        // class is the caller's responsibility (unsupported here).
        var aBytesRaw = GetByteVregsOrUnmerge(function, builder, aVreg, byteCount);
        var bBytesRaw = GetByteVregsOrUnmerge(function, builder, bVreg, byteCount);

        // Each byte vreg fed in by ABI lowering carries a copy hint to its
        // arrival physreg (e.g. byte 0 → $a, byte 1 → $x). The CMP chain
        // needs $a for the funneled comparison value at every step, so
        // letting one of the source-byte vregs stay in $a starves the chain.
        // Funnel each byte through a fresh Anyi8 vreg first to break the
        // copy hint chain — the RA is then free to park the bytes in zero
        // page across the entire chain.
        var aBytes = FunnelThroughAnyi8(function, builder, aBytesRaw);
        var bBytes = FunnelThroughAnyi8(function, builder, bBytesRaw);

        // For signed compares, flip bit 7 of the most-significant byte of each
        // operand so the unsigned chain below computes the signed result.
        if (signedFlip)
        {
            var top = byteCount - 1;
            aBytes[top] = EorByteWithImmediate(function, builder, aBytes[top], 0x80);
            bBytes[top] = EorByteWithImmediate(function, builder, bBytes[top], 0x80);
        }

        // Walk high byte → low byte. For each byte: copy the a-byte into a
        // fresh Ac vreg (since the architectural CMP requires $a), constrain
        // the b-byte to Imag8 (zero page), emit CMP, then emit predicate-
        // specific exit branches. The last byte's branches include the
        // fallthrough JMP that anchors the chain's terminator.
        for (var pos = byteCount - 1; pos >= 0; pos--)
        {
            var byteA = aBytes[pos];
            var byteB = bBytes[pos];

            // %a_cmp : ac = pseudo.copy %byteA — funnels each byte through $a
            // in turn so multiple bytes can be in flight without all needing
            // Ac class (which only has the single $a physreg).
            var aCmpVreg = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Ac,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(aCmpVreg, IsDefinition: true),
                new VirtualReg(byteA,    IsDefinition: false));
            ReclassifyTo(function, byteB, MOS6502RegisterClass.Imag8);

            EmitCmp(builder, aCmpVreg, byteB);

            var isLast = pos == 0;
            EmitMultiByteExits(builder, chainPredicate, isLast, tTarget, fTarget);
        }

        builder.Remove(next);
        builder.Remove(cmpi);
        return true;
    }

    // Emit `result = byte EOR #imm`, returning a fresh Anyi8 vreg holding the
    // result. The EOR must run in $a, so the byte is copied into a fresh Ac
    // vreg, EOR'd in place (def tied to use via EorImmInfo), then copied back
    // out into an Anyi8 vreg so the RA can park it in zero page for the rest of
    // the compare chain (otherwise it would be pinned to $a and starve the
    // per-byte CMP funnel).
    private static int EorByteWithImmediate(MirFunction function, MirBuilder builder, int byteVreg, long imm)
    {
        var aIn = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(aIn,     IsDefinition: true),
            new VirtualReg(byteVreg, IsDefinition: false));

        // %aOut : ac = mos6502.eor.imm %aIn, #imm  (def[0] tied to use[0]).
        var aOut = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.EorImm),
            new VirtualReg(aOut, IsDefinition: true),
            new VirtualReg(aIn,  IsDefinition: false),
            new Immediate(imm));

        var result = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Anyi8,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(result, IsDefinition: true),
            new VirtualReg(aOut,   IsDefinition: false));
        return result;
    }

    private static int[] FunnelThroughAnyi8(MirFunction function, MirBuilder builder, int[] bytes)
    {
        var result = new int[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var fresh = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Anyi8,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(fresh,    IsDefinition: true),
                new VirtualReg(bytes[i], IsDefinition: false));
            result[i] = fresh;
        }
        return result;
    }

    // Look through a wide vreg to find the i8 byte vregs that compose it.
    // If the vreg is defined by an arity-matching pseudo.merge, return its
    // part vregs directly and remove the now-dead merge (if it has only this
    // consumer). Otherwise emit a pseudo.unmerge and return its def vregs;
    // the caller is then responsible for the wide vreg's register class.
    private static int[] GetByteVregsOrUnmerge(MirFunction function, MirBuilder builder, int wideVreg, int byteCount)
    {
        var def = function.GetDefinition(wideVreg);
        if (def is not null
            && def.Opcode.Dialect == PseudoDialect.Id
            && (PseudoOp)def.Opcode.Code == PseudoOp.Merge)
        {
            var parts = new List<int>();
            foreach (var op in def.Operands)
            {
                if (op is VirtualReg v && !v.IsDefinition) parts.Add(v.Id);
            }
            if (parts.Count == byteCount)
            {
                // The merge's part vregs are exactly what we want. Once the
                // wide def loses this consumer (the cmpi we're replacing),
                // the merge becomes trivially dead — but isel won't sweep it
                // up automatically. Schedule removal if this was the merge's
                // only user.
                if (function.GetUseCount(wideVreg) == 1)
                {
                    builder.Remove(def);
                }
                return parts.ToArray();
            }
        }

        return builder.BuildUnmerge(IRType.I8, wideVreg, byteCount);
    }

    private static void EmitCmp(MirBuilder builder, int aVreg, int bVreg)
    {
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Cmp),
            new VirtualReg(aVreg, IsDefinition: false),
            new VirtualReg(bVreg, IsDefinition: false),
            new PhysicalReg(MOS6502Registers.N, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Z, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.C, IsDefinition: true, IsImplicit: true));
    }

    private static void EmitMultiByteExits(
        MirBuilder builder,
        ArithCmpPredicate predicate,
        bool isLast,
        BlockTarget tTarget,
        BlockTarget fTarget)
    {
        switch (predicate)
        {
            case ArithCmpPredicate.Eq:
                // BNE F on every byte; fallthrough at the end → JMP T.
                EmitConditionalBranch(builder, MOS6502Op.Bne, MOS6502Registers.Z, fTarget);
                if (isLast)
                    EmitUnconditional(builder, tTarget);
                break;

            case ArithCmpPredicate.Ult:
                // BCC → T (a < b in this byte); BNE → F (a > b in this byte).
                // On the last byte, no BNE F (BCC alone decides; equal → F).
                EmitConditionalBranch(builder, MOS6502Op.Bcc, MOS6502Registers.C, tTarget);
                if (!isLast)
                    EmitConditionalBranch(builder, MOS6502Op.Bne, MOS6502Registers.Z, fTarget);
                else
                    EmitUnconditional(builder, fTarget);
                break;

            case ArithCmpPredicate.Uge:
                // BCC → F (a < b in this byte); BNE → T (a > b ⇒ a >= b).
                // On the last byte: BCS → T (a >= b); else F.
                if (!isLast)
                {
                    EmitConditionalBranch(builder, MOS6502Op.Bcc, MOS6502Registers.C, fTarget);
                    EmitConditionalBranch(builder, MOS6502Op.Bne, MOS6502Registers.Z, tTarget);
                }
                else
                {
                    EmitConditionalBranch(builder, MOS6502Op.Bcs, MOS6502Registers.C, tTarget);
                    EmitUnconditional(builder, fTarget);
                }
                break;
        }
    }

    private static void EmitConditionalBranch(MirBuilder builder, MOS6502Op branchOp, int flagReg, BlockTarget target)
    {
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(branchOp),
            target,
            new PhysicalReg(flagReg, IsDefinition: false, IsImplicit: true));
    }

    private static void EmitUnconditional(MirBuilder builder, BlockTarget target)
    {
        builder.BuildInstruction(MOS6502Dialect.OpRef(MOS6502Op.JmpAbs), target);
    }

    private static void ReclassifyTo(MirFunction function, int vreg, int classId)
    {
        var name = MOS6502RegisterClass.GetName(classId)!;
        var annotation = function.GetVRegAnnotation(vreg);
        if (annotation is ClassedVReg existing && existing.ClassId == classId) return;
        function.ReclassifyVirtualRegister(vreg, classId, name);
    }

    // Indirect-Y pointer scratch pair: the two bytes of any mem.symbol-derived
    // address get pinned to these adjacent zero-page slots so LDA/STA ($zp),Y
    // can use them without an RA-aware register-pair model.
    //
    // Pinned to RC0/RC1 because those are reserved (excluded from the Imag8
    // allocatable pool in MOS6502RegisterInfo). If we used in-pool slots the
    // RA would freely reuse them as spill destinations between consecutive
    // mem ops on the same global, clobbering the pointer mid-sequence. RC0/RC1
    // are nominally the soft-stack pointer per CC_MOS, but no current pass
    // touches them; when frame slots / spilling land in step 11+, either pick
    // a different scratch pair or move the soft stack pointer.
    private const int PointerZpLo = 12; // MOS6502Registers.RC(0)
    private const int PointerZpHi = 13; // MOS6502Registers.RC(1)

    // mem.load.byte_at %p, <offset> → lda.indy through a zero-page pointer pair.
    // Only handles the case where %p is defined by `mem.symbol @name`; other
    // address-vreg sources (frame slots, heap pointers) come in later steps.
    private bool SelectMemLoadByteAt(MirInstruction instr, MirBuilder builder)
    {
        // Operand layout: def[0]=value vreg (i8), use[0]=address vreg (i16),
        //                 use[1]=Immediate byte offset.
        var function = builder.Function;
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg defReg  || !defReg.IsDefinition
            || instr.Operands[1] is not VirtualReg addrReg || addrReg.IsDefinition
            || instr.Operands[2] is not Immediate offset)
            throw new InvalidOperationException(
                "MOS6502InstructionSelector: mem.load.byte_at must have shape `%def : i8 = mem.load.byte_at %addr, <offset>`.");

        builder.SetInsertionPointBefore(instr);

        // A byte access whose address resolves to a FrameSlot lowers to the
        // addressing-mode-agnostic mos6502.frame.load.byte: the value byte is
        // defined in $a, and the scratch the absolute/indirect-Y sequence uses
        // ($y, $rc0, $rc1) is declared as an implicit clobber so RA reserves it.
        // FrameAccessLoweringPass expands it post-RA per the slot's placement.
        // Non-frame globals (strings, statics — genuine absolute addresses) fall
        // through to the indirect-Y path below. The pointer cache is left
        // untouched: a frame access does not park anything in the shared pair.
        if (ResolveFrameSlotSymbol(function, addrReg.Id) is string loadSym)
        {
            // %v : ac = mos6502.frame.load.byte @sym, #off,
            //          implicit-def $y, implicit-def $rc0, implicit-def $rc1
            var valueVreg = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Ac,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.FrameLoadByte),
                new VirtualReg(valueVreg, IsDefinition: true),
                new Symbol(loadSym),
                new Immediate(offset.Value),
                new PhysicalReg(MOS6502Registers.Y, IsDefinition: true, IsImplicit: true),
                new PhysicalReg(PointerZpLo, IsDefinition: true, IsImplicit: true),
                new PhysicalReg(PointerZpHi, IsDefinition: true, IsImplicit: true));

            // Funnel out of $a into a flexible Anyi8 vreg so the value can be
            // parked in zero page when more accesses are in flight (mirrors the
            // indirect path's Ac→park idiom).
            var frameResultVreg = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Anyi8,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(frameResultVreg, IsDefinition: true),
                new VirtualReg(valueVreg,       IsDefinition: false));

            function.ReplaceAllUsesOfRegister(defReg.Id, frameResultVreg);
            builder.Remove(instr);

            TryRemoveDeadMemSymbol(function, addrReg.Id, builder);
            return true;
        }

        // A byte access whose address resolves to a (non-frame) static symbol —
        // a global, string, or other compile-time-constant address — uses
        // absolute addressing: `lda.abs @sym+off` reads the byte directly, with
        // no zero-page pointer pair and no Y register. The byte offset (0 = low,
        // 1 = high of an i16) becomes the Symbol's offset. Mirrors llvm-mos
        // `G_LOAD_ABS`. Runtime i16 pointers (heap, computed) fall through to the
        // indirect-Y path below.
        if (TryResolveSymbolAddress(function, addrReg.Id) is string absLoadSym)
        {
            // %a : ac = mos6502.lda.abs @sym+off — value lands in $a.
            var absLdaVreg = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Ac,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.LdaAbs),
                new VirtualReg(absLdaVreg, IsDefinition: true),
                new Symbol(absLoadSym, (int)offset.Value));

            // Funnel out of $a into a flexible Anyi8 vreg so the value can be
            // parked when more accesses are in flight (same idiom as the indy path).
            var absResultVreg = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Anyi8,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(absResultVreg, IsDefinition: true),
                new VirtualReg(absLdaVreg,    IsDefinition: false));

            function.ReplaceAllUsesOfRegister(defReg.Id, absResultVreg);
            builder.Remove(instr);

            TryRemoveDeadMemSymbol(function, addrReg.Id, builder);
            return true;
        }

        EmitPointerSetup(builder, addrReg, offset.Value);

        // %a3 : ac = mos6502.lda.indy $rc2, implicit-use $rc3, implicit-use $y
        // Then copy the result out of $a into a flexible Anyi8 vreg so it can
        // be parked in zero page when more loads are in flight. Without the
        // copy, multi-byte loads tie every byte to $a and the RA runs out of
        // physregs in class Ac as soon as two byte loads are live.
        var ldaVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);

        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.LdaIndY),
            new VirtualReg(ldaVreg, IsDefinition: true),
            new PhysicalReg(PointerZpLo, IsDefinition: false),
            new PhysicalReg(PointerZpHi, IsDefinition: false, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));

        var resultVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Anyi8,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(resultVreg, IsDefinition: true),
            new VirtualReg(ldaVreg,    IsDefinition: false));

        function.ReplaceAllUsesOfRegister(defReg.Id, resultVreg);
        builder.Remove(instr);

        TryRemoveDeadMemSymbol(function, addrReg.Id, builder);
        return true;
    }

    // mem.store.byte_at %p, <offset>, %v → sta.indy through a zero-page pointer pair.
    private bool SelectMemStoreByteAt(MirInstruction instr, MirBuilder builder)
    {
        // Operand layout: use[0]=address vreg (i16), use[1]=Immediate offset,
        //                 use[2]=value vreg (i8).
        var function = builder.Function;
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg addrReg || addrReg.IsDefinition
            || instr.Operands[1] is not Immediate offset
            || instr.Operands[2] is not VirtualReg valReg  || valReg.IsDefinition)
            throw new InvalidOperationException(
                "MOS6502InstructionSelector: mem.store.byte_at must have shape `mem.store.byte_at %addr, <offset>, %val`.");

        builder.SetInsertionPointBefore(instr);

        // Park the value in an Anyi8 vreg *before* pointer setup so its live
        // range doesn't trap $a across the pointer-byte LDAs. Without this,
        // a parameter-in-$a value would stay in $a from function entry until
        // the sta.indy, and the lda.imm.symlo / .symhi clobbers $a mid-flight.
        var parkedVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Anyi8,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(parkedVreg, IsDefinition: true),
            new VirtualReg(valReg.Id,  IsDefinition: false));

        // A byte access whose address resolves to a FrameSlot lowers to the
        // addressing-mode-agnostic mos6502.frame.store.byte. FrameAccessLoweringPass
        // expands it post-RA per the slot's placement; non-frame globals fall
        // through to the indirect-Y path below.
        if (ResolveFrameSlotSymbol(function, addrReg.Id) is string storeSym)
        {
            // The value byte lives in $a and is PRESERVED through the expansion.
            // Both placements keep it there: the zero-page branch is a single
            // sta.zp (value already in $a), and the absolute branch builds the
            // $rc0/$rc1 pointer pair via $x (ldx/stx, never lda/sta) so $a is
            // never disturbed before the sta.indy. The op therefore clobbers
            // $x/$y/$rc0/$rc1 but reads $a as a plain (preserved) use.
            var valueVreg = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Ac,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(valueVreg,  IsDefinition: true),
                new VirtualReg(parkedVreg, IsDefinition: false));

            // frame.store.byte @sym, #off, $a,
            //   implicit-def $x, implicit-def $y, implicit-def $rc0, implicit-def $rc1
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.FrameStoreByte),
                new Symbol(storeSym),
                new Immediate(offset.Value),
                new VirtualReg(valueVreg, IsDefinition: false),
                new PhysicalReg(MOS6502Registers.X, IsDefinition: true, IsImplicit: true),
                new PhysicalReg(MOS6502Registers.Y, IsDefinition: true, IsImplicit: true),
                new PhysicalReg(PointerZpLo, IsDefinition: true, IsImplicit: true),
                new PhysicalReg(PointerZpHi, IsDefinition: true, IsImplicit: true));

            builder.Remove(instr);

            TryRemoveDeadMemSymbol(function, addrReg.Id, builder);
            return true;
        }

        // A (non-frame) static symbol store uses absolute addressing: the value
        // byte flows through $a and `sta.abs @sym+off` writes it directly, with
        // no zero-page pointer pair and no Y register. Mirrors llvm-mos
        // `G_STORE_ABS`. Runtime i16 pointers fall through to the indy path.
        if (TryResolveSymbolAddress(function, addrReg.Id) is string absStoreSym)
        {
            // sta.abs needs its source in $a (Ac); copy the parked value there.
            var absSrcVreg = function.CreateVirtualRegisterInClass(
                MOS6502RegisterClass.Ac,
                MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
            builder.BuildInstruction(
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(absSrcVreg,  IsDefinition: true),
                new VirtualReg(parkedVreg,  IsDefinition: false));

            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.StaAbs),
                new VirtualReg(absSrcVreg, IsDefinition: false),
                new Symbol(absStoreSym, (int)offset.Value));

            builder.Remove(instr);

            TryRemoveDeadMemSymbol(function, addrReg.Id, builder);
            return true;
        }

        EmitPointerSetup(builder, addrReg, offset.Value);

        // sta.indy emits `STA ($rc2),Y`. The source byte must be in $a (Ac
        // class). Insert a pseudo.copy to an Ac vreg so the value flows
        // through $a regardless of where the source vreg was allocated.
        var srcVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(srcVreg,    IsDefinition: true),
            new VirtualReg(parkedVreg, IsDefinition: false));

        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.StaIndY),
            new PhysicalReg(PointerZpLo, IsDefinition: false),
            new VirtualReg(srcVreg, IsDefinition: false),
            new PhysicalReg(PointerZpHi, IsDefinition: false, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));

        builder.Remove(instr);

        TryRemoveDeadMemSymbol(function, addrReg.Id, builder);
        return true;
    }

    // pseudo.unmerge of a `mem.symbol @name` def → materialise the two address
    // bytes directly with lda.imm.symlo / lda.imm.symhi. This is the path taken
    // when a symbol's *address* is used as a value (e.g. a string pointer passed
    // to a call) rather than as the base of a mem.load / mem.store. Other
    // unmerge sources pass straight through to the artifact combiner / RA.
    private bool SelectUnmerge(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;

        // Operand layout: def[0..N-1] = byte vregs, use[N] = wide source vreg.
        if (instr.Operands.Length < 2
            || instr.Operands[^1] is not VirtualReg srcReg || srcReg.IsDefinition)
            return true;

        var srcDef = function.GetDefinition(srcReg.Id);
        if (srcDef is null
            || srcDef.Opcode.Dialect != MemDialect.Id
            || (MemOp)srcDef.Opcode.Code != MemOp.Symbol
            || srcDef.Operands.Length < 2
            || srcDef.Operands[1] is not Symbol symbol)
        {
            // Not a symbol address — leave the unmerge for the combiner / RA.
            return true;
        }

        // Exactly two byte defs (i16 pointer). Anything else is unexpected for
        // a symbol address; let it fall through so the gap is visible.
        var byteDefs = new List<VirtualReg>();
        for (var i = 0; i < instr.Operands.Length - 1; i++)
        {
            if (instr.Operands[i] is not VirtualReg d || !d.IsDefinition) return true;
            byteDefs.Add(d);
        }
        if (byteDefs.Count != 2) return true;

        builder.SetInsertionPointBefore(instr);

        // lda.imm.symlo / .symhi are LDA-immediate: they always write $a (Ac).
        // Both address bytes are live at once (they feed the call's arg copies),
        // so each must be parked in a flexible Anyi8 vreg immediately after the
        // LDA — otherwise both byte vregs would want $a simultaneously. Mirrors
        // EmitPointerSetup's Ac→park idiom.
        var loVreg = EmitSymbolByte(function, builder, MOS6502Op.LdaImmSymLo, symbol.Name);
        function.ReplaceAllUsesOfRegister(byteDefs[0].Id, loVreg);

        var hiVreg = EmitSymbolByte(function, builder, MOS6502Op.LdaImmSymHi, symbol.Name);
        function.ReplaceAllUsesOfRegister(byteDefs[1].Id, hiVreg);

        builder.Remove(instr);
        TryRemoveDeadMemSymbol(function, srcReg.Id, builder);
        return true;
    }

    // lda.imm.symX @name → Ac vreg, then pseudo.copy into a flexible Anyi8 vreg
    // whose id is returned. Lets multiple symbol bytes be live without all
    // pinning $a.
    private static int EmitSymbolByte(MirFunction function, MirBuilder builder, MOS6502Op ldaOp, string symbolName)
    {
        var acVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(ldaOp),
            new VirtualReg(acVreg, IsDefinition: true),
            new Symbol(symbolName));

        var anyVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Anyi8,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(anyVreg, IsDefinition: true),
            new VirtualReg(acVreg,  IsDefinition: false));
        return anyVreg;
    }

    // Emits the pointer-setup sequence that puts an address's low/high bytes
    // into PointerZpLo / PointerZpHi and sets Y to the byte offset. The address
    // is either a mem.symbol global or a runtime i16 pointer (a vreg defined by
    // a pseudo.merge of two byte vregs):
    //
    //   ; mem.symbol @name path
    //   %lo : ac = mos6502.lda.imm.symlo @name
    //   $rc0     = pseudo.copy %lo
    //   %hi : ac = mos6502.lda.imm.symhi @name
    //   $rc1     = pseudo.copy %hi
    //
    //   ; runtime-pointer path (address = pseudo.merge %plo, %phi)
    //   $rc0     = pseudo.copy %plo
    //   $rc1     = pseudo.copy %phi
    //
    //   $y       = mos6502.ldy.imm <offset>   (both paths; def constrained to Yc)
    //
    // Run before the indirect-Y load/store at the current insertion point.
    // The pointer-byte setup is skipped only when the scratch pair already
    // holds this exact pointer (see _currentPointerKey) — i.e. consecutive byte
    // accesses to the same global / runtime pointer. A different pointer forces
    // a re-materialisation. The ldy.imm is always re-emitted: even on a cache
    // hit the offset differs per byte of a multi-byte load/store.
    private void EmitPointerSetup(MirBuilder builder, VirtualReg addrReg, long byteOffset)
    {
        var function = builder.Function;

        var symbolName = TryResolveSymbolAddress(function, addrReg.Id);
        var key = symbolName is not null ? "@" + symbolName : "%" + addrReg.Id;
        var needsSetup = _currentPointerKey != key;
        _currentPointerKey = key;

        if (needsSetup)
        {
            if (symbolName is not null)
                EmitSymbolPointerBytes(builder, symbolName);
            else
                EmitRuntimePointerBytes(builder, addrReg.Id);
        }

        // ldy.imm <offset> → fresh Yc vreg, then pin to $y. Re-emitted on
        // every mem.load.byte_at / mem.store.byte_at so the addressing mode
        // finds Y live at the use site (even if cached pointer setup was
        // reused, the offset may differ per byte for a multi-byte load/store).
        var yVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Yc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Yc)!);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.LdyImm),
            new VirtualReg(yVreg, IsDefinition: true),
            new Immediate(byteOffset));
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: true),
            new VirtualReg(yVreg, IsDefinition: false));
    }

    // Materialise a global's low/high address bytes into PointerZpLo / PointerZpHi
    // via lda.imm.symlo / .symhi. Each LDA-immediate writes $a, so it is pinned
    // straight into the scratch pair through a pseudo.copy.
    private static void EmitSymbolPointerBytes(MirBuilder builder, string symbolName)
    {
        var function = builder.Function;

        // lda.imm.symlo @name → fresh Ac vreg, then pin to $rc0.
        var loVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.LdaImmSymLo),
            new VirtualReg(loVreg, IsDefinition: true),
            new Symbol(symbolName));
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(PointerZpLo, IsDefinition: true),
            new VirtualReg(loVreg, IsDefinition: false));

        // lda.imm.symhi @name → fresh Ac vreg, then pin to $rc1.
        var hiVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.LdaImmSymHi),
            new VirtualReg(hiVreg, IsDefinition: true),
            new Symbol(symbolName));
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(PointerZpHi, IsDefinition: true),
            new VirtualReg(hiVreg, IsDefinition: false));
    }

    // Copy a runtime i16 pointer's low/high byte vregs into PointerZpLo /
    // PointerZpHi. The address vreg must be defined by a pseudo.merge of exactly
    // two byte vregs — the post-legalization form of every wide value (an
    // arith.addi pointer arithmetic chain, a loaded i16 pointer, an i16 block /
    // ABI parameter, …). The byte vregs are reclassified to the flexible Anyi8
    // pool so they can live in zero page regardless of where they were defined.
    private static void EmitRuntimePointerBytes(MirBuilder builder, int addrVreg)
    {
        var function = builder.Function;
        var (loVreg, hiVreg) = ResolveRuntimePointerBytesOrThrow(function, addrVreg);

        ReclassifyTo(function, loVreg, MOS6502RegisterClass.Anyi8);
        ReclassifyTo(function, hiVreg, MOS6502RegisterClass.Anyi8);

        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(PointerZpLo, IsDefinition: true),
            new VirtualReg(loVreg, IsDefinition: false));
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new PhysicalReg(PointerZpHi, IsDefinition: true),
            new VirtualReg(hiVreg, IsDefinition: false));
    }

    // The frame-slot symbol backing an address vreg, or null if the address is
    // not a FrameSlot (a non-frame global — string/static absolute address — or
    // a runtime pointer). A frame-slot access is routed to the abstract
    // mos6502.frame.{load,store}.byte op and placed/addressed late
    // (FrameAccessLoweringPass → MOS6502FrameLowering.LowerFrameAccess); a
    // non-frame global keeps the indirect-Y lowering at isel because its address
    // is genuinely absolute, not a frame object.
    private static string? ResolveFrameSlotSymbol(MirFunction function, int addrVreg)
    {
        var symbolName = TryResolveSymbolAddress(function, addrVreg);
        if (symbolName is null) return null;

        foreach (var slot in function.FrameSlots)
            if (slot.SymbolName == symbolName)
                return symbolName;
        return null;
    }

    private static string? TryResolveSymbolAddress(MirFunction function, int addrVreg)
    {
        var def = function.GetDefinition(addrVreg);
        if (def is not null
            && def.Opcode.Dialect == MemDialect.Id
            && (MemOp)def.Opcode.Code == MemOp.Symbol
            && def.Operands.Length >= 2
            && def.Operands[1] is Symbol symbolOperand)
        {
            return symbolOperand.Name;
        }
        return null;
    }

    // The two byte vregs (low, high) of a runtime i16 pointer. Throws if the
    // address vreg is not defined by a pseudo.merge of exactly two byte vregs —
    // other pointer shapes (frame slots, scalar i16 vregs that never went
    // through a merge) are not yet supported, so the gap stays visible.
    private static (int Lo, int Hi) ResolveRuntimePointerBytesOrThrow(MirFunction function, int addrVreg)
    {
        var def = function.GetDefinition(addrVreg);
        if (def is not null
            && def.Opcode.Dialect == PseudoDialect.Id
            && (PseudoOp)def.Opcode.Code == PseudoOp.Merge)
        {
            var bytes = new List<int>();
            foreach (var op in def.Operands)
                if (op is VirtualReg v && !v.IsDefinition)
                    bytes.Add(v.Id);
            if (bytes.Count == 2)
                return (bytes[0], bytes[1]);
        }

        throw new NotSupportedException(
            "MOS6502InstructionSelector: runtime mem.load/store currently only supports " +
            "addresses produced by `mem.symbol @name` or a two-byte pseudo.merge (i16 " +
            "pointer). Other address sources (frame slots, scalar i16 vregs) land later.");
    }

    // After lowering a mem.load/store whose address came from a mem.symbol,
    // remove the mem.symbol itself if no other consumer remains. Keeps the IR
    // clean — RA later assumes every instruction is either target dialect or
    // pseudo.
    private static void TryRemoveDeadMemSymbol(MirFunction function, int addrVreg, MirBuilder builder)
    {
        if (function.GetUseCount(addrVreg) > 0) return;
        var def = function.GetDefinition(addrVreg);
        if (def is not null) builder.Remove(def);
    }
}
