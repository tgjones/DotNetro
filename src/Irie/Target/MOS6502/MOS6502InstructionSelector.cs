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
    // Per-function cache: symbols whose pointer bytes have already been
    // materialised into PointerZpLo / PointerZpHi in this function. The
    // pseudo.copy `$rc2 = ...` / `$rc3 = ...` defs hold the bytes alive
    // across the function (until a call clobbers them — step 8's lit tests
    // are leaf functions, so calls aren't an issue yet). Future steps will
    // need a more careful invalidation strategy once calls + mem ops mix.
    private readonly HashSet<string> _pointerSetupEmitted = [];

    public override void BeginFunction(MirFunction function)
    {
        _pointerSetupEmitted.Clear();
    }

    public override bool Select(MirInstruction instruction, MirBuilder builder)
    {
        var opcode = instruction.Opcode;

        if (opcode.Dialect == ArithDialect.Id)
        {
            return (ArithOp)opcode.Code switch
            {
                ArithOp.Constant   => SelectConstant(instruction, builder),
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
                PseudoOp.Unmerge => true,
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

        // Already-selected target ops pass through.
        if (opcode.Dialect == MOS6502Dialect.Id) return true;

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
    private static bool SelectAddCarry(MirInstruction instr, MirBuilder builder)
        => SelectCarryBorrowOp(instr, builder, MOS6502Op.Adc);

    // arith.subi_with_borrow → mos6502.sbc (pre-AMS). Same operand layout,
    // class assignment, and tied-def shape as add-with-carry — the only
    // difference is the emitted opcode tag.
    private static bool SelectSubBorrow(MirInstruction instr, MirBuilder builder)
        => SelectCarryBorrowOp(instr, builder, MOS6502Op.Sbc);

    private static bool SelectCarryBorrowOp(MirInstruction instr, MirBuilder builder, MOS6502Op targetOp)
    {
        var function = builder.Function;

        // Operand layout: def[0]=result, def[1]=carry/borrow_out,
        //                 use[0]=a, use[1]=b, use[2]=carry/borrow_in
        var resultVreg     = ((VirtualReg)instr.Operands[0]).Id;
        var flagOutVreg    = ((VirtualReg)instr.Operands[1]).Id;
        var aVreg          = ((VirtualReg)instr.Operands[2]).Id;
        var bVreg          = ((VirtualReg)instr.Operands[3]).Id;
        var flagInVreg     = ((VirtualReg)instr.Operands[4]).Id;

        ReclassifyTo(function, aVreg,      MOS6502RegisterClass.Ac);
        ReclassifyTo(function, bVreg,      MOS6502RegisterClass.Imag8);
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

        function.ReplaceAllUsesOfRegister(resultVreg,  newResult);
        function.ReplaceAllUsesOfRegister(flagOutVreg, newFlag);
        builder.Remove(instr);
        return true;
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
        var bVreg = ((VirtualReg)cmpi.Operands[3]).Id;

        if (function.GetVRegAnnotation(aVreg) is not TypedVReg aTyped)
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: arith.cmpi on non-typed operand %{aVreg} is not supported.");

        // Multi-byte cmpi (i16, i32) widens to a per-byte chain of CMP +
        // conditional branches via the target-private path below. Only
        // unsigned / equality predicates are supported in this first cut;
        // signed predicates use SBC + N⊕V and are deferred.
        if (aTyped.Type.SizeInBits > 8)
        {
            return SelectCmpIMultiByte(cmpi, builder, predicate, aVreg, bVreg, condVreg, aTyped.Type.SizeInBits / 8);
        }

        if (aTyped.Type != IRType.I8)
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: arith.cmpi on type {aTyped.Type.DisplayName} is not supported.");

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

        var branchOp = predicate switch
        {
            ArithCmpPredicate.Eq  => MOS6502Op.Beq,
            ArithCmpPredicate.Ne  => MOS6502Op.Bne,
            ArithCmpPredicate.Ult => MOS6502Op.Bcc,
            ArithCmpPredicate.Uge => MOS6502Op.Bcs,
            _ => throw new NotSupportedException(
                $"MOS6502InstructionSelector: arith.cmpi predicate '{ArithCmpPredicateNames.ToText(predicate)}' " +
                "is not yet implemented for the cmp+cond_br fusion path. " +
                "Coverage so far: eq, ne, ult, uge. Signed and ugt/ule predicates land later."),
        };

        ReclassifyTo(function, aVreg, MOS6502RegisterClass.Ac);
        ReclassifyTo(function, bVreg, MOS6502RegisterClass.Imag8);

        builder.SetInsertionPointBefore(cmpi);

        // mos6502.cmp %a, %b implicit-def $n, implicit-def $z, implicit-def $c
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.Cmp),
            new VirtualReg(aVreg, IsDefinition: false),
            new VirtualReg(bVreg, IsDefinition: false),
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
    // For ne:  any BNE → T (early exit). If we reach the end, all equal → F.
    // For ult: BCC → T (a < b); BNE → F (a > b in high byte means a >= b).
    //          On reaching the last byte unequal: BCC → T; else F.
    //          On full equality: F.
    // For uge: BCC → F (a < b); BNE → T (a > b ⇒ a >= b).
    //          On reaching the last byte unequal: BCS → T; else F.
    //          On full equality: T.
    //
    // Only equality / unsigned-ordering predicates are supported. Signed
    // predicates need SBC + N⊕V and are deferred (plan §6 step 15 / 3b).
    private static bool SelectCmpIMultiByte(
        MirInstruction cmpi,
        MirBuilder builder,
        ArithCmpPredicate predicate,
        int aVreg,
        int bVreg,
        int condVreg,
        int byteCount)
    {
        if (predicate is not (ArithCmpPredicate.Eq or ArithCmpPredicate.Ne
            or ArithCmpPredicate.Ult or ArithCmpPredicate.Uge))
        {
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: multi-byte arith.cmpi predicate '{ArithCmpPredicateNames.ToText(predicate)}' " +
                "is not yet supported. Coverage so far: eq, ne, ult, uge. Signed predicates land later.");
        }

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
            EmitMultiByteExits(builder, predicate, isLast, tTarget, fTarget);
        }

        builder.Remove(next);
        builder.Remove(cmpi);
        return true;
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

            case ArithCmpPredicate.Ne:
                // BNE T on every byte; fallthrough at the end → JMP F.
                EmitConditionalBranch(builder, MOS6502Op.Bne, MOS6502Registers.Z, tTarget);
                if (isLast)
                    EmitUnconditional(builder, fTarget);
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

        var calleeSymbol = ResolveSymbolAddressOrThrow(function, addrReg.Id, "mem.load.byte_at");

        builder.SetInsertionPointBefore(instr);
        EmitPointerSetup(builder, calleeSymbol, offset.Value);

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

        var calleeSymbol = ResolveSymbolAddressOrThrow(function, addrReg.Id, "mem.store.byte_at");

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

        EmitPointerSetup(builder, calleeSymbol, offset.Value);

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

    // Emits the pointer-setup sequence that puts @name's low/high bytes into
    // PointerZpLo / PointerZpHi and zeroes Y:
    //
    //   %lo : ac = mos6502.lda.imm.symlo @name
    //   $rc2     = pseudo.copy %lo
    //   %hi : ac = mos6502.lda.imm.symhi @name
    //   $rc3     = pseudo.copy %hi
    //   $y       = mos6502.ldy.imm 0       (def constrained to Yc)
    //
    // Run before the indirect-Y load/store at the current insertion point.
    // Skipped if this function has already materialized the same symbol's
    // pointer into the scratch pair (see _pointerSetupEmitted) — the bytes
    // remain valid for subsequent uses in the same function (until a call
    // clobbers them; step-8 lit tests are leaf functions).
    private void EmitPointerSetup(MirBuilder builder, string symbolName, long byteOffset)
    {
        var function = builder.Function;
        var firstUse = _pointerSetupEmitted.Add(symbolName);

        if (firstUse)
        {
            // lda.imm.symlo @name → fresh Ac vreg, then pin to $rc2.
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

            // lda.imm.symhi @name → fresh Ac vreg, then pin to $rc3.
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

    // Find the symbol name backing an address vreg. Throws if the vreg is not
    // defined by a `mem.symbol @name` instruction — other address sources are
    // not yet supported (frame slots / runtime pointers come in step 11+).
    private static string ResolveSymbolAddressOrThrow(MirFunction function, int addrVreg, string opLabel)
    {
        var def = function.GetDefinition(addrVreg);
        if (def is null
            || def.Opcode.Dialect != MemDialect.Id
            || (MemOp)def.Opcode.Code != MemOp.Symbol
            || def.Operands.Length < 2
            || def.Operands[1] is not Symbol symbolOperand)
        {
            throw new NotSupportedException(
                $"MOS6502InstructionSelector: {opLabel} currently only supports addresses produced by " +
                "`mem.symbol @name`. Non-symbolic i16 addresses (frame slots, heap pointers) land later.");
        }
        return symbolOperand.Name;
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
