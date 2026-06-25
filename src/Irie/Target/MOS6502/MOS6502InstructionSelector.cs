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

    // Target register info, used by the adc/sbc emission paths to constrain each
    // selected op's register operands to their declared operand class (the
    // RegisterClassConstraining / llvm constrainSelectedInstRegOperands port).
    private readonly Irie.Target.TargetRegisterInfo _registerInfo;

    public MOS6502InstructionSelector(Irie.Target.TargetRegisterInfo registerInfo)
    {
        _registerInfo = registerInfo;
    }

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
                // arith.cmpi / arith.select / arith.not are all consumed wholesale
                // by the cf.cond_br re-fusion below (SelectCondBr looks each up by
                // def), so skip them here. A compare always reaches a cond_br
                // transitively: directly, through the legalizer's normalization
                // `not`, or as a leaf of a wide-compare lexicographic select tree.
                ArithOp.CmpI       => true,
                ArithOp.Select     => true,
                ArithOp.Not        => true,
                _ => false,
            };
        }

        if (opcode.Dialect == CfDialect.Id)
        {
            return (CfOp)opcode.Code switch
            {
                CfOp.Br     => SelectBr(instruction, builder),
                CfOp.CondBr => SelectCondBr(instruction, builder),
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
    private bool SelectAddCarry(MirInstruction instr, MirBuilder builder)
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
    private bool SelectSubBorrow(MirInstruction instr, MirBuilder builder)
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
    private bool SelectAddI(MirInstruction instr, MirBuilder builder)
        => SelectBareAddSub(instr, builder, MOS6502Op.Adc, MOS6502Op.Clc);

    // arith.subi : i8 → sec + mos6502.sbc. Carry-in head is a SEC (6502 "no
    // borrow"), mirroring the subi_with_borrow chain head. See SelectAddI.
    private bool SelectSubI(MirInstruction instr, MirBuilder builder)
        => SelectBareAddSub(instr, builder, MOS6502Op.Sbc, MOS6502Op.Sec);

    private bool SelectBareAddSub(
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

    private bool SelectCarryBorrowOp(
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

        // Stage B': fold a constant operand into the ALU's immediate addressing
        // mode, mirroring the cmp fold (ResolveI8CmpRhs) and llvm-mos's imm-operand
        // patterns. adc is commutative so either input may become the immediate;
        // sbc is not, so only the subtrahend (b) may fold.
        if (TryGetConstant(function, bVreg) is { } bConst)
            return EmitFoldedImmCarryBorrow(
                builder, instr, targetOp, resultVreg, flagOutVreg,
                accVreg: aVreg, flagInVreg, immValue: bConst, foldedConstVreg: bVreg);
        if (targetOp == MOS6502Op.Adc && TryGetConstant(function, aVreg) is { } aConst)
            return EmitFoldedImmCarryBorrow(
                builder, instr, targetOp, resultVreg, flagOutVreg,
                accVreg: bVreg, flagInVreg, immValue: aConst, foldedConstVreg: aVreg);

        var newResult = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        var newFlag   = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        builder.SetInsertionPointBefore(instr);
        var adc = builder.BuildInstruction(
            MOS6502Dialect.OpRef(targetOp),
            new VirtualReg(newResult,  IsDefinition: true),
            new VirtualReg(newFlag,    IsDefinition: true),
            new VirtualReg(aVreg,      IsDefinition: false),
            new VirtualReg(bVreg,      IsDefinition: false),
            new VirtualReg(flagInVreg, IsDefinition: false));

        // Constrain each operand to its declared class (AdcInfo/SbcInfo: use[0]=Ac
        // tied to def[0], use[1]=Imag8, carry=Cc), narrowing the operand vregs in
        // place or splitting with a copy on conflict — the llvm-mos
        // constrainSelectedInstRegOperands behaviour. This replaces the old
        // defensive `Anyi8` pin on the accumulator: instead of keeping `a` broad
        // to dodge the Ac ∩ Imag8 = ∅ RA hazard, we narrow it to `Ac` and let the
        // utility split the rare case where the same value is also a zp operand.
        RegisterClassConstraining.ConstrainSelectedInstRegOperands(
            adc, function, _registerInfo, builder);

        // The adc/sbc result lands in $a (def[0] = Ac, tied to use[0]). If it
        // outlives the instruction the generic relocation pass
        // (RegisterAllocatorPass.InsertRelocationCopiesForConstrainedDefs) moves
        // it off $a into a flexible vreg — one pass for every tied-def result,
        // replacing the per-opcode out-funnel that used to live here.
        function.ReplaceAllUsesOfRegister(resultVreg,  newResult);
        function.ReplaceAllUsesOfRegister(flagOutVreg, newFlag);
        builder.Remove(instr);
        return true;
    }

    // A folded absolute-symbol load: the Symbol the ALU op will read directly,
    // plus the now-dead `lda.abs` and (when the operand reaches the ALU op via a
    // relocation copy rather than directly) the funnel `pseudo.copy` to erase.
    private readonly record struct AbsLoadFold(Symbol Symbol, MirInstruction Lda, MirInstruction? Copy);

    // Match an `lda.abs @sym` feeding `vreg`, accepting EITHER shape
    // (mirroring llvm-mos m_FoldedLdAbs, which walks to the G_LOAD_ABS def with
    // no copy in the pattern):
    //   - direct:      `%v = mos6502.lda.abs @sym`
    //   - via funnel:  `%v = pseudo.copy %lda` where `%lda = mos6502.lda.abs @sym`
    // Requires the relevant defs to be single-use and no memory write between the
    // load and `consumer` (which would make folding the load past it unsound).
    // Returns the symbol + the instruction(s) to erase, or null if the pattern /
    // safety check fails.
    private static AbsLoadFold? TryMatchFoldableAbsLoad(MirFunction function, int vreg, MirInstruction consumer)
    {
        if (function.GetUseCount(vreg) != 1) return null;

        var def = function.GetDefinition(vreg);
        if (def is null) return null;

        MirInstruction? copy = null;
        var lda = def;

        // Funnel shape: walk through a single-use relocation copy to its source.
        if (def.Opcode.Dialect == PseudoDialect.Id
            && (PseudoOp)def.Opcode.Code == PseudoOp.Copy
            && def.Operands.Length == 2
            && def.Operands[1] is VirtualReg copySrc)
        {
            if (function.GetUseCount(copySrc.Id) != 1) return null;
            var srcDef = function.GetDefinition(copySrc.Id);
            if (srcDef is null) return null;
            copy = def;
            lda = srcDef;
        }

        if (lda.Opcode != MOS6502Dialect.OpRef(MOS6502Op.LdaAbs)
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

    // Emit `%r, %c = mos6502.{adc,sbc}.abs %acc, @sym, %carryIn` and erase the
    // original op plus the now-dead load. The result is left in its Ac vreg; the
    // generic relocation pass moves it off $a if it outlives the op.
    private bool EmitFoldedAbsCarryBorrow(
        MirBuilder builder, MirInstruction instr, MOS6502Op absOp,
        int resultVreg, int flagOutVreg, int accVreg, int flagInVreg, AbsLoadFold fold)
    {
        var function = builder.Function;

        var newResult = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        var newFlag   = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        builder.SetInsertionPointBefore(instr);
        var adc = builder.BuildInstruction(
            MOS6502Dialect.OpRef(absOp),
            new VirtualReg(newResult,  IsDefinition: true),
            new VirtualReg(newFlag,    IsDefinition: true),
            new VirtualReg(accVreg,    IsDefinition: false),
            fold.Symbol,
            new VirtualReg(flagInVreg, IsDefinition: false));

        // Constrain operands to AdcInfo/SbcInfo classes (acc=Ac tied to def, the
        // folded symbol is operand[3]=Imag8 but a Symbol so it is skipped, carry=Cc).
        RegisterClassConstraining.ConstrainSelectedInstRegOperands(
            adc, function, _registerInfo, builder);

        function.ReplaceAllUsesOfRegister(resultVreg,  newResult);
        function.ReplaceAllUsesOfRegister(flagOutVreg, newFlag);
        builder.Remove(instr);

        // The load now has no remaining uses (the single-use checks passed).
        if (fold.Copy is { } foldCopy) builder.Remove(foldCopy);
        builder.Remove(fold.Lda);
        return true;
    }

    // Emit `%r, %c = mos6502.{adc,sbc} %acc, #imm, %carryIn` and erase the
    // original op plus the now-dead constant def. The result is left in its Ac
    // vreg; the generic relocation pass moves it off $a if it outlives the op.
    // AMS refines the bare adc/sbc to .imm once operand[3] is an Immediate.
    private bool EmitFoldedImmCarryBorrow(
        MirBuilder builder, MirInstruction instr, MOS6502Op aluOp,
        int resultVreg, int flagOutVreg, int accVreg, int flagInVreg,
        long immValue, int foldedConstVreg)
    {
        var function = builder.Function;

        var newResult = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Ac,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Ac)!);
        var newFlag   = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Cc,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Cc)!);

        builder.SetInsertionPointBefore(instr);
        var adc = builder.BuildInstruction(
            MOS6502Dialect.OpRef(aluOp),                 // stays pre-AMS adc/sbc; AMS → .imm
            new VirtualReg(newResult,  IsDefinition: true),
            new VirtualReg(newFlag,    IsDefinition: true),
            new VirtualReg(accVreg,    IsDefinition: false),
            new Immediate(immValue),                     // operand[3] → AMS picks AdcImm/SbcImm
            new VirtualReg(flagInVreg, IsDefinition: false));

        // Constrain operands to AdcInfo/SbcInfo classes (acc=Ac tied to def,
        // operand[3] is an Immediate so skipped, carry=Cc).
        RegisterClassConstraining.ConstrainSelectedInstRegOperands(
            adc, function, _registerInfo, builder);

        function.ReplaceAllUsesOfRegister(resultVreg,  newResult);
        function.ReplaceAllUsesOfRegister(flagOutVreg, newFlag);
        builder.Remove(instr);

        // Dead-constant cleanup — mirror the cmp path. The constant may feed
        // several ops; only erase when its last use is gone.
        if (function.GetUseCount(foldedConstVreg) == 0
            && function.GetDefinition(foldedConstVreg) is { Parent: not null } def)
            builder.Remove(def);

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
            or MOS6502Op.StAbs
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

    // Re-fuse a cf.cond_br with the compare feeding it into a CMP+branch sequence
    // (the llvm-mos selectBrCondImm analogue). The boolean is never materialized.
    // The condition is, by construction after legalization + select-lowering:
    //   * an arith.cmpi (i8)                       → CMP + conditional branch;
    //   * a wide-compare lexicographic select tree → the multi-byte CMP ladder;
    //   * either of the above wrapped in arith.not → same, with T/F swapped.
    private bool SelectCondBr(MirInstruction condBr, MirBuilder builder)
    {
        var function = builder.Function;

        if (condBr.Operands.Length != 3
            || condBr.Operands[0] is not VirtualReg cond || cond.IsDefinition
            || condBr.Operands[1] is not BlockTarget t0
            || condBr.Operands[2] is not BlockTarget f0)
            throw new InvalidOperationException(
                "MOS6502InstructionSelector: malformed cf.cond_br operand shape.");

        var toRemove = new List<MirInstruction>();
        var condId = cond.Id;
        var tTarget = t0;
        var fTarget = f0;

        // Strip arith.not wrappers (the legalizer's normalized-away inverse
        // predicate), swapping the branch's true/false targets for each.
        while (true)
        {
            var def = function.GetDefinition(condId);
            if (def is null
                || def.Opcode.Dialect != ArithDialect.Id
                || (ArithOp)def.Opcode.Code != ArithOp.Not)
                break;
            toRemove.Add(def);
            condId = ((VirtualReg)def.Operands[1]).Id;
            (tTarget, fTarget) = (fTarget, tTarget);
        }

        var rootDef = function.GetDefinition(condId)
            ?? throw new InvalidOperationException(
                $"MOS6502InstructionSelector: cf.cond_br condition %{condId} has no definition.");

        builder.SetInsertionPointBefore(condBr);

        // Vregs whose def is a folded constant operand: after the compare folds
        // them into immediate CMPs, their materialization (`pseudo.copy <imm>`,
        // already selected from the original arith.constant) becomes dead. They
        // are erased below once their last cmpi use is gone — but only if the use
        // count actually drops to zero, since one constant can feed several
        // compares and the slt path does not fold.
        var foldedConstantVregs = new List<int>();

        if (rootDef.Opcode.Dialect == ArithDialect.Id && (ArithOp)rootDef.Opcode.Code == ArithOp.Select)
        {
            var (predicate, bytes) = RecoverCmpTree(function, condId, toRemove);
            var aBytes = bytes.Select(p => p.a).ToArray();
            var bBytes = bytes.Select(p => p.b).ToArray();
            EmitMultiByteCmpLadder(function, builder, predicate, aBytes, bBytes, tTarget, fTarget);
            if (predicate is not ArithCmpPredicate.Slt)
                foldedConstantVregs.AddRange(bBytes);
        }
        else if (rootDef.Opcode.Dialect == ArithDialect.Id && (ArithOp)rootDef.Opcode.Code == ArithOp.CmpI)
        {
            EmitI8CmpBranch(function, builder, rootDef, tTarget, fTarget);
            if (rootDef.Operands[3] is VirtualReg bReg)
                foldedConstantVregs.Add(bReg.Id);
            toRemove.Add(rootDef);
        }
        else
        {
            throw new NotSupportedException(
                "MOS6502InstructionSelector: cf.cond_br whose condition is not a compare or a " +
                "wide-compare select tree is not supported (i1 register test + branch is a " +
                "separate follow-up).");
        }

        builder.Remove(condBr);
        foreach (var ti in toRemove)
            if (ti.Parent != null)
                builder.Remove(ti);

        // Erase the now-dead constant materializations (only those with no
        // remaining uses; a constant shared by an unfolded compare survives).
        foreach (var v in foldedConstantVregs.Distinct())
        {
            if (function.GetUseCount(v) != 0) continue;
            if (TryGetConstant(function, v) is null) continue;
            var def = function.GetDefinition(v);
            if (def is not null && def.Parent is not null)
                builder.Remove(def);
        }
        return true;
    }

    // Emit `CMP + conditional branch T + jmp.abs F` for an i8 arith.cmpi. After
    // legalizer normalization only the canonical {eq, uge, slt} predicates reach
    // here:
    //   - eq  → CMP + BEQ;  uge → CMP + BCS.
    //   - slt against an immediate 0 → a sign test: CMP #0 sets N from bit 7,
    //     BMI branches when negative.
    //   - general slt → bias both operands by EOR #$80 (mirrors the multi-byte
    //     signed path), so the unsigned CMP + BCC computes signed less-than.
    // Emits at the builder's current insertion point; does not remove the cmpi
    // (the caller does).
    private void EmitI8CmpBranch(
        MirFunction function, MirBuilder builder, MirInstruction cmpi,
        BlockTarget tTarget, BlockTarget fTarget)
    {
        var predicate = (ArithCmpPredicate)((Immediate)cmpi.Operands[1]).Value;
        var aVreg = ((VirtualReg)cmpi.Operands[2]).Id;
        // The RHS is normally a vreg, but a signed compare-against-zero carries an
        // immediate 0 (the high-byte sign test, narrowed in the legalizer).
        var bOperand = cmpi.Operands[3];

        var cmpAVreg = aVreg;
        MirOperand cmpRhs;
        MOS6502Op branchOp;
        switch (predicate)
        {
            case ArithCmpPredicate.Eq:
                branchOp = MOS6502Op.Beq;
                cmpRhs = ResolveI8CmpRhs(function, bOperand);
                break;
            case ArithCmpPredicate.Uge:
                branchOp = MOS6502Op.Bcs;
                cmpRhs = ResolveI8CmpRhs(function, bOperand);
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

        // mos6502.cmp %a, <rhs> implicit-def $n, $z, $c. Build with the raw
        // (possibly sign-biased) %a operand and constrain each register operand to
        // CmpInfo's classes (use[0]=Ac, use[1]=Imag8): the utility narrows the vreg
        // in place or splits with a copy only on a class conflict, matching llvm's
        // constrainSelectedInstRegOperands. This replaces the old always-on `Ac`
        // funnel copy — a dead-after compare operand now needs no copy at all.
        EmitCmp(function, builder, cmpAVreg, cmpRhs);

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
    }

    // The compare rhs for an eq / uge i8 compare. A constant b-operand is folded
    // into an Immediate (the AddressingModeSelector refines cmp → cmp.imm) so the
    // value is never materialized into a register; otherwise the register byte is
    // passed through as use[1], where the Imag8 operand constraint from CmpInfo
    // applies directly (it is left broad, like the rest of the cmp lowering).
    private static MirOperand ResolveI8CmpRhs(MirFunction function, MirOperand bOperand)
    {
        if (bOperand is Immediate imm)
            return new Immediate(imm.Value);
        var bVreg = ((VirtualReg)bOperand).Id;
        if (TryGetConstant(function, bVreg) is { } constByte)
            return new Immediate(constByte);
        return new VirtualReg(bVreg, IsDefinition: false);
    }

    // Walk a wide-compare lexicographic tree rooted at `vreg`, returning the
    // overall canonical predicate and the per-byte operand vregs (index 0 = least
    // significant). Collects every tree instruction (selects, leaf cmpis, and the
    // arith.nots described below) into `treeInstrs` for the caller to erase.
    // Mirrors the shape built by MOS6502LegalizerInfo.BuildLexicographic:
    //   leaf:   %r = arith.cmpi <pred>, %a, %b
    //   select: %r = arith.select %eqHi, %rest, %cmpHi
    //           with %eqHi = cmpi eq, %aHi, %bHi  and  %cmpHi = cmpi <pred>, %aHi, %bHi
    //
    // The "rest" / lower-byte compares are built with the unsigned `ult`
    // predicate, which the legalizer's consumer-independent normalization then
    // rewrites to `uge` wrapped in an arith.not (exactly as llvm-mos does). The
    // ladder is driven entirely by the *overall* predicate (recovered from the
    // outermost high-byte compare) and the per-byte operands, so those per-byte
    // predicates and the nots wrapping them are irrelevant here — StripNots looks
    // through them, and the lower-level predicates are discarded.
    private static (ArithCmpPredicate predicate, List<(int a, int b)> bytes)
        RecoverCmpTree(MirFunction function, int vreg, List<MirInstruction> treeInstrs)
    {
        vreg = StripNots(function, vreg, treeInstrs);
        var def = function.GetDefinition(vreg)
            ?? throw new InvalidOperationException(
                $"MOS6502InstructionSelector: wide-compare tree vreg %{vreg} has no definition.");
        treeInstrs.Add(def);

        if (def.Opcode.Dialect == ArithDialect.Id && (ArithOp)def.Opcode.Code == ArithOp.CmpI)
        {
            // Leaf: the least-significant byte's compare.
            var leafPred = (ArithCmpPredicate)((Immediate)def.Operands[1]).Value;
            var a = ((VirtualReg)def.Operands[2]).Id;
            var b = ((VirtualReg)def.Operands[3]).Id;
            return (leafPred, [(a, b)]);
        }

        if (def.Opcode.Dialect == ArithDialect.Id && (ArithOp)def.Opcode.Code == ArithOp.Select)
        {
            // operands: [0]=def, [1]=cond(eqHi), [2]=true(rest), [3]=false(cmpHi)
            var restVreg = ((VirtualReg)def.Operands[2]).Id;
            var cmpHiVreg = StripNots(function, ((VirtualReg)def.Operands[3]).Id, treeInstrs);
            var eqHiVreg = StripNots(function, ((VirtualReg)def.Operands[1]).Id, treeInstrs);

            var cmpHiDef = function.GetDefinition(cmpHiVreg)!;
            var eqHiDef = function.GetDefinition(eqHiVreg)!;
            treeInstrs.Add(cmpHiDef);
            treeInstrs.Add(eqHiDef);

            // The overall predicate is this (outermost) level's high-byte compare;
            // lower levels use the unsigned "rest" predicate, which the ladder
            // derives itself, so the recursive predicate is discarded.
            var predicate = (ArithCmpPredicate)((Immediate)cmpHiDef.Operands[1]).Value;
            var aHi = ((VirtualReg)cmpHiDef.Operands[2]).Id;
            var bHi = ((VirtualReg)cmpHiDef.Operands[3]).Id;

            var (_, restBytes) = RecoverCmpTree(function, restVreg, treeInstrs);
            restBytes.Add((aHi, bHi));   // append the more-significant byte
            return (predicate, restBytes);
        }

        throw new NotSupportedException(
            $"MOS6502InstructionSelector: unexpected opcode in wide-compare tree at %{vreg}.");
    }

    // Follow a chain of `arith.not` definitions, collecting them into
    // `treeInstrs`, and return the underlying (non-not) vreg.
    private static int StripNots(MirFunction function, int vreg, List<MirInstruction> treeInstrs)
    {
        while (true)
        {
            var def = function.GetDefinition(vreg);
            if (def is null
                || def.Opcode.Dialect != ArithDialect.Id
                || (ArithOp)def.Opcode.Code != ArithOp.Not)
                return vreg;
            treeInstrs.Add(def);
            vreg = ((VirtualReg)def.Operands[1]).Id;
        }
    }

    // Emit the per-byte CMP+branch ladder for an N-byte compare from the byte
    // operand vregs (index 0 = least significant). For canonical predicate p:
    //   for each byte i from high to low:
    //     CMP a[i], b[i]
    //     <p-specific exit branches to T / F>
    // For eq:  any BNE → F; full equality → T.
    // For ult: BCC → T; BNE → F (last byte: BCC → T else fall to F).
    // For uge: BCC → F; BNE → T (last byte: BCS → T else F).
    // A signed `slt` runs the ult ladder after flipping bit 7 of the most-
    // significant byte of both operands (a <s b == (a^msb) <u (b^msb)).
    private void EmitMultiByteCmpLadder(
        MirFunction function,
        MirBuilder builder,
        ArithCmpPredicate predicate,
        int[] aBytesRaw,
        int[] bBytesRaw,
        BlockTarget tTarget,
        BlockTarget fTarget)
    {
        if (predicate is not (ArithCmpPredicate.Eq or ArithCmpPredicate.Uge or ArithCmpPredicate.Slt))
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: multi-byte compare predicate '{ArithCmpPredicateNames.ToText(predicate)}' " +
                "reached the ladder; after legalizer normalization only eq, uge, slt are expected.");

        var signedFlip = predicate is ArithCmpPredicate.Slt;
        var chainPredicate = signedFlip ? ArithCmpPredicate.Ult : predicate;
        var byteCount = aBytesRaw.Length;

        // Operands are used *broadly*, not funnelled. `mos6502.cmp` is
        // non-destructive, so a byte that is only compared needs no preserving
        // copy — and a defensive funnel would actively hurt: when the same value
        // is also live past the compare (e.g. an operand the arms later subtract),
        // the funnel vreg and the original interfere, forcing two zero-page slots
        // plus the copy. Each per-byte cmp is built with the raw operands and run
        // through RegisterClassConstraining (in EmitCmp), which narrows use[0] to
        // `Ac` and use[1] to `Imag8` in place — splitting with a copy only on a
        // genuine class conflict, the llvm constrainSelectedInstRegOperands
        // behaviour. Only the signed-flip top byte takes a guaranteed copy, and
        // that is the *necessary* preserving copy emitted by EorByteWithImmediate
        // (#$80 EOR is destructive).
        var aBytes = (int[])aBytesRaw.Clone();

        // Resolve each `b` byte to a compare rhs. A constant byte (the zero of a
        // narrowed compare-against-zero) is folded into an Immediate so no zero
        // gets materialized into zero page; any other byte is passed through as a
        // register operand. The signed-flip (slt) path has no constants — the
        // legalizer's #$80 EOR requires real registers — so every byte is a vreg.
        var bRhs = new MirOperand[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            if (!signedFlip && TryGetConstant(function, bBytesRaw[i]) is { } constByte)
                bRhs[i] = new Immediate(constByte);
            else
                bRhs[i] = new VirtualReg(bBytesRaw[i], IsDefinition: false);
        }

        if (signedFlip)
        {
            // a <s b  ==  (a ^ msb) <u (b ^ msb): flip bit 7 of the most-
            // significant byte of both operands, then run the unsigned ladder.
            // EorByteWithImmediate copies its input into $a before EOR'ing, so the
            // raw top byte survives for any later use in the arms.
            var top = byteCount - 1;
            aBytes[top] = EorByteWithImmediate(function, builder, aBytesRaw[top], 0x80);
            bRhs[top] = new VirtualReg(
                EorByteWithImmediate(function, builder, bBytesRaw[top], 0x80), IsDefinition: false);
        }

        for (var pos = byteCount - 1; pos >= 0; pos--)
        {
            EmitCmp(function, builder, aBytes[pos], bRhs[pos]);

            var isLast = pos == 0;
            EmitMultiByteExits(builder, chainPredicate, isLast, tTarget, fTarget);
        }
    }

    // Emit `result = byte EOR #imm`, returning the eor.imm result vreg ($a-class).
    // The EOR must run in $a, so the byte is copied into a fresh Ac vreg and
    // EOR'd in place (def tied to use via EorImmInfo). The result is left in its
    // Ac vreg; if it outlives the op the generic relocation pass moves it off $a
    // into a flexible vreg so the RA can park it in zero page for the rest of the
    // compare chain (the former per-opcode out-funnel is gone).
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

        return aOut;
    }

    // mos6502.cmp %a, <rhs> implicit-def $n, $z, $c. The rhs is a register
    // operand (zero-page byte) or an Immediate (constant operand folded directly
    // — the AddressingModeSelector refines the opcode to cmp.imm). Mirrors
    // llvm-mos's CmpImm fold against a constant operand.
    //
    // Built with the raw `a` / rhs operands and then run through
    // RegisterClassConstraining (the llvm constrainSelectedInstRegOperands port):
    // it narrows use[0] toward `Ac` and use[1] toward `Imag8` (CmpInfo's classes)
    // in place, inserting a `pseudo.copy` split only when the vreg is already
    // committed to a disjoint class. No defensive funnel copy is forced — a
    // dead-after operand stays where it is. The constrain utility moves the
    // builder's insertion point to before the cmp (where it inserts any split
    // copy), so reposition it after the cmp for the caller's following branches.
    private void EmitCmp(MirFunction function, MirBuilder builder, int aVreg, MirOperand rhs)
    {
        var cmp = builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Cmp),
            new VirtualReg(aVreg, IsDefinition: false),
            rhs,
            new PhysicalReg(MOS6502Registers.N, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Z, IsDefinition: true, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.C, IsDefinition: true, IsImplicit: true));

        RegisterClassConstraining.ConstrainSelectedInstRegOperands(
            cmp, function, _registerInfo, builder);

        builder.SetInsertionPointAfter(cmp);
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

    // If `vreg` is defined by a compile-time constant, return its value.
    // After legalization a `0` operand of a narrowed compare is an
    // `arith.constant`; by the time SelectCondBr runs, isel has already visited
    // it (it precedes the cond_br in program order) and rewritten it to the i8
    // constant form `%v : any8 = pseudo.copy <imm>`. Match both so the compare
    // selector can fold the value into an immediate CMP and drop the constant
    // materialization (the now-dead def is erased by the caller's toRemove list).
    private static long? TryGetConstant(MirFunction function, int vreg)
    {
        var def = function.GetDefinition(vreg);
        if (def is null) return null;

        if (def.Opcode.Dialect == ArithDialect.Id
            && (ArithOp)def.Opcode.Code == ArithOp.Constant
            && def.Operands.Length == 2
            && def.Operands[1] is Immediate arithImm)
            return arithImm.Value;

        if (def.Opcode.Dialect == PseudoDialect.Id
            && (PseudoOp)def.Opcode.Code == PseudoOp.Copy
            && def.Operands.Length == 2
            && def.Operands[1] is Immediate copyImm)
            return copyImm.Value;

        return null;
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
                new VirtualReg(valReg.Id,  IsDefinition: false));

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
        // byte is written directly by `sta.abs @sym+off`, with no zero-page
        // pointer pair and no Y register. Mirrors llvm-mos `G_STORE_ABS`. Runtime
        // i16 pointers fall through to the indy path.
        if (TryResolveSymbolAddress(function, addrReg.Id) is string absStoreSym)
        {
            // st.abs is the generic (register-agnostic) absolute store: it reads
            // the value operand DIRECTLY (no copy to a pinned $a), its Axy operand
            // class letting the byte stay in whichever of $a/$x/$y its producer
            // left it, and MOS6502AddressingModeSelectorPass refines it to the
            // concrete `sta.abs` / `stx.abs` / `sty.abs` to match. Referencing the
            // value in place — rather than copying it into a fresh store-only vreg
            // — is what makes this pay off: a store-only copy would interfere with
            // the value's other live uses (e.g. global-rw stores AND returns the
            // sum), forcing the store onto a different register and back through
            // $a. Reading the shared value directly stores from $y/$x with no
            // transfer, turning global-rw's `TYA;STA;TXA;STA` into `STY;STX`. No
            // parking is needed: the absolute store has no pointer-byte LDAs to
            // clobber $a (unlike the indirect-Y path below).
            builder.BuildInstruction(
                MOS6502Dialect.OpRef(MOS6502Op.StAbs),
                new VirtualReg(valReg.Id, IsDefinition: false),
                new Symbol(absStoreSym, (int)offset.Value));

            builder.Remove(instr);

            TryRemoveDeadMemSymbol(function, addrReg.Id, builder);
            return true;
        }

        // Park the value in an Anyi8 vreg *before* pointer setup so its live
        // range doesn't trap $a across the pointer-byte LDAs. Without this,
        // a parameter-in-$a value would stay in $a from function entry until
        // the sta.indy, and the lda.imm.symlo / .symhi clobbers $a mid-flight.
        // (The frame and absolute paths above need no parking — they have no
        // pointer-byte LDAs — so they read the value vreg directly.)
        var parkedVreg = function.CreateVirtualRegisterInClass(
            MOS6502RegisterClass.Anyi8,
            MOS6502RegisterClass.GetName(MOS6502RegisterClass.Anyi8)!);
        builder.BuildInstruction(
            PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(parkedVreg, IsDefinition: true),
            new VirtualReg(valReg.Id,  IsDefinition: false));

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
