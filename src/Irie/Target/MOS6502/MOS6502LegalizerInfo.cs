using Irie.Dialects.Arith;
using Irie.Dialects.Cast;
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
                // arith.cmpi's legality is keyed off operand[2] (the `a` value),
                // not the i1 def — see ArithDialect.GetInstructionInfo's
                // TypeOperandIndex: 2. Every integer compare is Custom-legalized
                // (LegalizeCmpI): the predicate is normalized consumer-
                // independently to the canonical {eq, uge, slt} set (inverting via
                // an arith.not), the signed-against-zero shape is narrowed to a
                // single high-byte sign test, and a wide compare is lowered to a
                // lexicographic i8 cmpi + i1 select tree. An i8 compare is left
                // legal for the selector's i8 cmpi+cond_br fusion.
                ArithOp.CmpI       => LegalityAction.Custom,
                ArithOp.Constant   when intType.SizeInBits == 1  => LegalityAction.Legal,
                ArithOp.Constant   when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.Constant   when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                // Materialized (value) selects: i8 left legal for the target's
                // select-lowering pass (which runs after the legalizer); wider
                // narrows element-wise into per-byte i8 selects sharing the i1
                // condition. The i1 selects of a wide-compare lexicographic tree
                // are left legal for the instruction selector to re-fuse.
                ArithOp.Select     when intType.SizeInBits == 1  => LegalityAction.Legal,
                ArithOp.Select     when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.Select     when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                // arith.not (i1) marks a normalized-away inverse predicate; left
                // legal for the selector to strip at the consuming cf.cond_br.
                ArithOp.Not        when intType.SizeInBits == 1  => LegalityAction.Legal,
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

        if (opcode.Dialect == CastDialect.Id)
        {
            return ((CastOp)opcode.Code) switch
            {
                // cast.trunc lowers to a pseudo.extract / pseudo.merge of the
                // source vreg's low bytes via the Custom action below.
                CastOp.Trunc => LegalityAction.Custom,
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
                // mem.fill's TypeOperandIndex points at the byte-pattern (i8) operand;
                // the count is an Immediate that the Custom path unrolls into per-byte
                // mem.store.byte_at.
                MemOp.Fill        when intType.SizeInBits == 8  => LegalityAction.Custom,
                _ => LegalityAction.Unsupported,
            };
        }

        return LegalityAction.Unsupported;
    }

    public override IRType GetNarrowType(OpcodeRef opcode, IRType fromType)
    {
        if (opcode.Dialect == ArithDialect.Id
            && ((ArithOp)opcode.Code == ArithOp.AddI
                || (ArithOp)opcode.Code == ArithOp.SubI
                || (ArithOp)opcode.Code == ArithOp.Constant
                || (ArithOp)opcode.Code == ArithOp.Select))
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
        if (instr.Opcode.Dialect == MemDialect.Id)
        {
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

                case MemOp.Fill:
                    LowerMemFill(instr, builder);
                    return;
            }
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: no Custom legalization for mem opcode {(MemOp)instr.Opcode.Code}");
        }

        if (instr.Opcode.Dialect == CastDialect.Id)
        {
            switch ((CastOp)instr.Opcode.Code)
            {
                case CastOp.Trunc:
                    LowerTrunc(instr, builder);
                    return;
            }
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: no Custom legalization for cast opcode {(CastOp)instr.Opcode.Code}");
        }

        if (instr.Opcode.Dialect == ArithDialect.Id)
        {
            switch ((ArithOp)instr.Opcode.Code)
            {
                case ArithOp.CmpI:
                    LegalizeCmpI(instr, builder);
                    return;
            }
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: no Custom legalization for arith opcode {(ArithOp)instr.Opcode.Code}");
        }

        throw new NotSupportedException(
            $"MOS6502LegalizerInfo: no Custom legalization for {instr.Opcode}");
    }

    // arith.cmpi legalization (mirrors llvm-mos MOSLegalizerInfo::legalizeICmp):
    //
    //   1. Normalize the predicate to the canonical {eq, uge, slt} set the 6502
    //      flags express directly — *consumer-independently*, exactly as llvm-mos
    //      does. Negation (ne/ult/sge → eq/uge/slt) inverts the predicate and
    //      wraps the i1 result in an `arith.not` (the buildNot analogue); operand
    //      swap (ule/ugt/sle/sgt) reverses the predicate and the a/b operands.
    //      The selector strips the `arith.not` at the consuming cf.cond_br by
    //      swapping its true/false targets, so no boolean is ever materialized.
    //   2. Narrow the signed compare against a statically-zero RHS (`x <s 0`) to
    //      a single high-byte sign test — `arith.cmpi slt, lhsHigh : i8, 0` —
    //      since only the most-significant byte's sign bit matters.
    //   3. Lower any remaining wide (i16/i32) compare to a lexicographic tree of
    //      per-byte i8 arith.cmpi combined with i1 arith.select (BuildWideCmpTree).
    //      The tree's i1 root feeds the cond_br; the instruction selector re-fuses
    //      the whole tree into a CMP+branch ladder (it is never materialized).
    //
    // Because normalization no longer depends on the consumer, a compare feeding a
    // materialized arith.select (lowered to a cond_br only later, by the target's
    // select-lowering pass) is narrowed here just the same.
    private static void LegalizeCmpI(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 4
            || instr.Operands[0] is not VirtualReg defReg || !defReg.IsDefinition
            || instr.Operands[1] is not Immediate predImm
            || instr.Operands[2] is not VirtualReg || instr.Operands[3] is not VirtualReg)
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: arith.cmpi must have shape `%def : i1 = arith.cmpi <pred>, %a, %b`.");

        // Step 1: normalize the predicate to {eq, uge, slt}. Negation is recorded
        // and applied once, as an arith.not on the result (consumer-independent);
        // operand swap mutates the cmpi in place.
        var predicate = (ArithCmpPredicate)predImm.Value;
        var negate = false;
        while (predicate is not (ArithCmpPredicate.Eq or ArithCmpPredicate.Uge or ArithCmpPredicate.Slt))
        {
            if (predicate is ArithCmpPredicate.Ne or ArithCmpPredicate.Ult or ArithCmpPredicate.Sge)
            {
                predicate = ArithCmpPredicateOps.Inverse(predicate);
                negate = !negate;
            }
            else // ule, ugt, sle, sgt
            {
                predicate = ArithCmpPredicateOps.Swapped(predicate);
                (instr.Operands[2], instr.Operands[3]) = (instr.Operands[3], instr.Operands[2]);
            }
        }
        instr.Operands[1] = new Immediate((long)predicate);

        // Apply the negation by routing the cmpi's def through a fresh vreg and
        // emitting `%origDef = arith.not %inner` right after the cmpi. Steps 2/3
        // (which may RAUW the cmpi's def or rebuild it into a tree) then flow
        // through the not automatically, so consumers see not(canonicalResult).
        if (negate)
        {
            var inner = function.CreateVirtualRegister(IRType.I1);
            builder.SetInsertionPointAfter(instr);
            builder.BuildInstruction(
                ArithDialect.OpRef(ArithOp.Not),
                new VirtualReg(defReg.Id, IsDefinition: true),
                new VirtualReg(inner,     IsDefinition: false));
            instr.Operands[0] = new VirtualReg(inner, IsDefinition: true);
        }

        // Step 2: signed compare against a constant 0 → high-byte sign test.
        if (predicate == ArithCmpPredicate.Slt
            && instr.Operands[3] is VirtualReg bReg
            && IsConstantZero(function, bReg.Id))
        {
            var aReg = (VirtualReg)instr.Operands[2];
            var aType = ((TypedVReg)function.GetVRegAnnotation(aReg.Id)).Type;
            if (aType.SizeInBits > 8)
            {
                var byteCount = aType.SizeInBits / 8;
                var bytes = builder.BuildUnmerge(IRType.I8, aReg.Id, byteCount);
                instr.Operands[2] = new VirtualReg(bytes[byteCount - 1], IsDefinition: false);
            }

            // Re-key onto an immediate 0. Using an Immediate (rather than a fresh
            // i8 zero constant) keeps the RHS out of the vreg world entirely, so no
            // dead `arith.constant 0` survives to the selector.
            instr.Operands[3] = new Immediate(0);
            return;
        }

        // Step 3: a general wide (i16/i32) compare → a lexicographic tree of
        // per-byte i8 arith.cmpi combined with arith.select (mirrors llvm-mos
        // MOSLegalizerInfo::legalizeICmp's buildSelect recursion). The tree's i1
        // result feeds the cond_br; the instruction selector re-fuses the whole
        // tree back into a CMP+branch ladder (it is never materialized as a
        // boolean). MirSelectLoweringPass leaves these i1 selects alone.
        var lhs = (VirtualReg)instr.Operands[2];
        var lhsType = ((TypedVReg)function.GetVRegAnnotation(lhs.Id)).Type;
        if (lhsType.SizeInBits > 8)
        {
            var rhs = (VirtualReg)instr.Operands[3];
            BuildWideCmpTree(instr, builder, predicate, lhs.Id, rhs.Id, lhsType.SizeInBits / 8);
        }
        // An i8 compare is left as-is for the selector's i8 cmpi+cond_br fusion.
    }

    // Lexicographic per-byte lowering of a wide compare. For canonical predicate
    // P on N-byte operands (byte index 0 = least significant), the most-
    // significant byte is compared with P and the lower bytes with the unsigned
    // "rest" predicate (uge→uge, eq→eq, slt→ult — the signed bias of the top byte
    // is applied later by the selector, exactly as the old multi-byte ladder did):
    //
    //   cmp(P, [0..hi]) = select(cmpi eq, a[hi], b[hi]
    //                            ? cmp(restP, [0..hi-1])
    //                            : cmpi P, a[hi], b[hi])
    //
    // The selector's re-fusion recovers (predicate, per-byte operands) from this
    // shape and emits the ladder; the tree instructions are then erased.
    private static void BuildWideCmpTree(
        MirInstruction cmpi, MirBuilder builder, ArithCmpPredicate predicate,
        int aVreg, int bVreg, int byteCount)
    {
        var function = builder.Function;
        builder.SetInsertionPointBefore(cmpi);

        var aBytes = builder.BuildUnmerge(IRType.I8, aVreg, byteCount);
        var bBytes = builder.BuildUnmerge(IRType.I8, bVreg, byteCount);

        var restPredicate = predicate == ArithCmpPredicate.Slt ? ArithCmpPredicate.Ult : predicate;
        var root = BuildLexicographic(builder, aBytes, bBytes, byteCount - 1, predicate, restPredicate);

        var defId = ((VirtualReg)cmpi.Operands[0]).Id;
        function.ReplaceAllUsesOfRegister(defId, root);
        builder.Remove(cmpi);
    }

    // Build the i1 comparison vreg for bytes [0..hi]. The byte at `hi` is compared
    // with `thisPredicate`; the recursion for lower bytes uses `restPredicate`.
    private static int BuildLexicographic(
        MirBuilder builder, int[] aBytes, int[] bBytes, int hi,
        ArithCmpPredicate thisPredicate, ArithCmpPredicate restPredicate)
    {
        if (hi == 0)
            return builder.BuildCmpI(thisPredicate, aBytes[0], bBytes[0]);

        var cmpHi = builder.BuildCmpI(thisPredicate, aBytes[hi], bBytes[hi]);
        var eqHi  = builder.BuildCmpI(ArithCmpPredicate.Eq, aBytes[hi], bBytes[hi]);
        var rest  = BuildLexicographic(builder, aBytes, bBytes, hi - 1, restPredicate, restPredicate);
        return builder.BuildSelect(IRType.I1, eqHi, rest, cmpHi);
    }

    // True when `vreg` is defined by `arith.constant 0` of any width. Pure
    // inspection — never mutates the IR.
    private static bool IsConstantZero(MirFunction function, int vreg)
    {
        var def = function.GetDefinition(vreg);
        return def is not null
            && def.Opcode.Dialect == ArithDialect.Id
            && (ArithOp)def.Opcode.Code == ArithOp.Constant
            && def.Operands is [_, Immediate { Value: 0 }];
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

    // mem.fill %addr, %byte, <count>  →  unroll into <count> mem.store.byte_at
    // ops at offsets 0..count-1, all writing the same byte vreg. Loop-form for
    // large counts is future work; the active tests use small struct sizes.
    private static void LowerMemFill(MirInstruction instr, MirBuilder builder)
    {
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg addrReg || addrReg.IsDefinition
            || instr.Operands[1] is not VirtualReg byteReg || byteReg.IsDefinition
            || instr.Operands[2] is not Immediate countImm)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: mem.fill must have shape `mem.fill %addr, %byte, <count>`.");
        }

        var count = countImm.Value;
        if (count < 0 || count > 256)
        {
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: mem.fill count {count} is out of range (loop-form not implemented; expected 0..256).");
        }

        for (var i = 0; i < count; i++)
        {
            builder.BuildInstruction(
                MemDialect.OpRef(MemOp.StoreByteAt),
                new VirtualReg(addrReg.Id, IsDefinition: false),
                new Immediate(i),
                new VirtualReg(byteReg.Id, IsDefinition: false));
        }
        builder.Remove(instr);
    }

    // %y : iM = cast.trunc %x : iN  (M < N)  →  pseudo.unmerge + pseudo.merge
    // of the low M/8 byte vregs. The special case M = 8 reduces to a single
    // pseudo.extract (the artifact combiner from step 6 folds it through any
    // upstream pseudo.merge, so a truncate of a multi-byte construct collapses
    // to a direct reference to the desired byte).
    private static void LowerTrunc(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg defReg || !defReg.IsDefinition
            || instr.Operands[1] is not VirtualReg srcReg || srcReg.IsDefinition)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: cast.trunc must have shape `%def : iM = cast.trunc %src : iN`.");
        }

        var defType = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type;
        var srcType = ((TypedVReg)function.GetVRegAnnotation(srcReg.Id)).Type;
        if (defType.SizeInBits >= srcType.SizeInBits)
        {
            throw new InvalidOperationException(
                $"MOS6502LegalizerInfo: cast.trunc requires the target type to be narrower " +
                $"than the source (got {defType.DisplayName} ← {srcType.DisplayName}).");
        }

        var defByteCount = defType.SizeInBits / 8;
        var srcByteCount = srcType.SizeInBits / 8;

        if (defByteCount == 1)
        {
            // Single-byte truncation: surface as a pseudo.extract at offset 0
            // so the artifact combiner can fold it against an upstream merge.
            var byteVreg = builder.BuildExtract(IRType.I8, srcReg.Id, bitOffset: 0);
            function.ReplaceAllUsesOfRegister(defReg.Id, byteVreg);
            builder.Remove(instr);
            return;
        }

        // Wider-than-byte truncation: unmerge the source into its byte vregs
        // and re-merge only the low defByteCount of them into the original
        // def vreg.
        var sourceBytes = builder.BuildUnmerge(IRType.I8, srcReg.Id, srcByteCount);
        var lowBytes = new int[defByteCount];
        Array.Copy(sourceBytes, lowBytes, defByteCount);
        builder.BuildMergeInto(defReg.Id, lowBytes);
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
