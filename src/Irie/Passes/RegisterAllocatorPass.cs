using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// Unified-MIR register allocator. Absorbs the responsibilities of the old
// CodeGen RegisterCoalescer + RegisterAllocator + VirtualRegisterRewriter
// trio into one pass.
//
// Input:  post-isel, post-PhiElim, post-TwoAddress MIR. Every vreg has a
//         ClassedVReg annotation; no block parameters; tied uses already
//         materialized as pseudo.copy that re-defines the def vreg.
// Output: physreg-only MIR. Every VirtualReg operand has been replaced with
//         a PhysicalReg. Block live-ins are recomputed against the final
//         assignment. The vreg annotation table is cleared.
//
// Pipeline:
//   1. Widen live-in vregs (entry block pseudo.copy-from-physreg defs) to
//      the target's flexible 8-bit class so multiple live-ins fit even when
//      every downstream use otherwise pins them to a single-physreg class.
//   2. Insert constraint-fixup copies wherever an operand's vreg class does
//      not match the class the dialect's instruction info requires.
//   3. Insert result-preservation copies after tied-operand defs whose
//      result is live beyond the instruction.
//   4. Compute liveness on the now-final shape.
//   5. Linear-scan physreg allocation with copy hints. A single
//      `physregActive` dictionary tracks which physregs are currently in
//      use; expiry uses `endpoint ≤ currentStart` so tied def/use that
//      meet at the same slot can share a physreg.
//   6. Rewrite VirtualReg operands to PhysicalReg.
//   7. Recompute block live-ins from the final assignment.
//
// Spilling is not implemented; a NotImplementedException is thrown if no
// physical register is available in a class.
public sealed class RegisterAllocatorPass(TargetRegisterInfo registerInfo) : MirFunctionPass
{
    public override string Name => "RegisterAllocator";

    public override void Run(MirFunction function)
    {
        var flexibleI8 = registerInfo.FlexibleI8ClassId;
        if (flexibleI8 != 0)
        {
            WidenLiveinsToFlexibleClass(function, flexibleI8);
            WidenUnconstrainedToFlexibleClass(function, flexibleI8);
            WidenLeftoverTypedToFlexibleClass(function, flexibleI8);
        }

        InsertConstraintFixupCopies(function);
        InsertResultPreservationCopies(function, flexibleI8);

        var liveness = PassManager != null
            ? GetAnalysis<LivenessAnalysis, Liveness>(function)
            : new LivenessAnalysis().Compute(function);

        var assignment = LinearScanAllocate(function, liveness);
        ApplyAssignment(function, assignment);
        function.ClearVRegAnnotations();
        RecomputeBlockLiveIns(function, liveness, assignment);
    }

    // Step 1 — widen each entry-block livein pseudo.copy result vreg's class
    // to the flexible 8-bit class. Without this every `Ac`-class livein would
    // need to share $A. Also covers vregs that ISel never reclassified (e.g.
    // a parameter that flows only through pseudo.copy to a call site, with no
    // arith op in between): the entry-block-livein vreg starts as TypedVReg
    // and needs *some* class assignment for RA to work.
    private void WidenLiveinsToFlexibleClass(MirFunction function, int flexibleI8)
    {
        if (function.Blocks.Count == 0) return;
        var entry = function.Blocks[0];
        var flexibleName = ClassNameOrFallback(flexibleI8);

        foreach (var instr in entry.Instructions)
        {
            if (instr.Opcode.Dialect != PseudoDialect.Id) continue;
            if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) continue;
            if (instr.Operands.Length != 2) continue;

            if (instr.Operands[0] is not VirtualReg { IsDefinition: true } def) continue;
            if (instr.Operands[1] is not PhysicalReg { IsDefinition: false }) continue;

            function.ReclassifyVirtualRegister(def.Id, flexibleI8, flexibleName);
        }
    }

    // Step 1b — widen every vreg whose class is a single-physreg-class subset
    // of the flexible 8-bit class but which is touched *only* by pseudo.copy
    // instructions. Such a vreg has no hard physreg requirement of its own:
    // every real (target-dialect) operand that constrains a value to a
    // specific physreg reaches it through a pseudo.copy, so the value itself
    // is free to live anywhere 8-bit.
    //
    // ISel reclassifies the *source* of an `arith.addi_with_carry` to `Ac`
    // (because that source becomes the ADC's tied use after two-address
    // expansion). For a constant-defined or call-crossing addend byte, the
    // value itself is only ever read through a `pseudo.copy` into the actual
    // ADC accumulator vreg — it has no hard `Ac` requirement of its own. Left
    // in `Ac`, four such bytes pile onto the single `$a` and RA runs dry.
    // Widening them to `any8` parks them in the abundant RC* pool, mirroring
    // exactly what live-in addend bytes already get from step 1.
    //
    // The copy-only test is deliberately conservative: a vreg that appears as
    // an operand of any non-copy instruction (e.g. the per-link `mos6502.adc`
    // accumulator, or a value the selector deliberately materialised in `$a`
    // via `mos6502.lda.imm.symlo`) keeps its selected class, because target
    // ops carry implicit physreg constraints that are not all declared in
    // their DialectInstructionInfo. Only Ac/Xc — single-physreg classes wholly
    // contained in the flexible class — are widened; Imag8 (zp) is left alone.
    private void WidenUnconstrainedToFlexibleClass(MirFunction function, int flexibleI8)
    {
        var flexibleRegs = registerInfo.GetAllocatableRegisters(flexibleI8);

        // A vreg is "copy-only" until proven otherwise: any appearance as an
        // operand of a non-pseudo.copy instruction disqualifies it.
        var touchedByNonCopy = new HashSet<int>();
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (IsCopy(instr)) continue;
                foreach (var op in instr.Operands)
                    if (op is VirtualReg v)
                        touchedByNonCopy.Add(v.Id);
            }
        }

        var flexibleName = ClassNameOrFallback(flexibleI8);
        foreach (var vreg in function.VirtualRegisterIds.ToList())
        {
            if (touchedByNonCopy.Contains(vreg)) continue;
            if (!function.TryGetVRegAnnotation(vreg, out var annotation)
                || annotation is not ClassedVReg classed) continue;
            if (classed.ClassId == flexibleI8) continue;

            // Only widen classes whose physregs are wholly contained in the
            // flexible class, and that are strictly narrower (a single physreg).
            var narrow = registerInfo.GetAllocatableRegisters(classed.ClassId);
            if (narrow.Length != 1) continue;
            if (!Contains(flexibleRegs, narrow[0])) continue;

            function.ReclassifyVirtualRegister(vreg, flexibleI8, flexibleName);
        }
    }

    // Step 1c — any vreg still carrying a TypedVReg annotation (width ≤ 8) at
    // RA time is, by construction, an 8-bit value the instruction selector
    // never pinned to a specific class: it flows only through pseudo.copy
    // plumbing. The canonical source is block-parameter narrowing — the i8
    // block parameters the legalizer introduces survive PhiElimination as
    // typed pseudo.copy defs in non-entry blocks. Give them the flexible class
    // so RA can park them in the abundant RC* pool. A still-typed wider vreg
    // would indicate an isel/legalizer gap, so it is rejected loudly.
    private void WidenLeftoverTypedToFlexibleClass(MirFunction function, int flexibleI8)
    {
        // Only consider vregs that some instruction actually references; an
        // orphaned typed annotation (e.g. a dead merge result the legalizer
        // already DCE'd, such as an unused parameter) never reaches RA.
        var referenced = new HashSet<int>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    CollectVregRefs(op, referenced);

        var flexibleName = ClassNameOrFallback(flexibleI8);
        foreach (var vreg in referenced)
        {
            if (!function.TryGetVRegAnnotation(vreg, out var annotation)) continue;
            if (annotation is not TypedVReg typed) continue;

            if (typed.Type.SizeInBits > 8)
                throw new InvalidOperationException(
                    $"RegisterAllocator: vreg %{vreg} still has wide type " +
                    $"{typed.Type.DisplayName} at RA — legalization/isel did not narrow it.");

            function.ReclassifyVirtualRegister(vreg, flexibleI8, flexibleName);
        }
    }

    private static void CollectVregRefs(MirOperand op, HashSet<int> into)
    {
        switch (op)
        {
            case VirtualReg v:
                into.Add(v.Id);
                break;
            case BlockTarget bt:
                foreach (var arg in bt.Args)
                    CollectVregRefs(arg, into);
                break;
        }
    }

    private static bool IsCopy(MirInstruction instr) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy;

    // Step 2 — for every operand whose dialect-declared class does not match
    // the operand's vreg class, insert a pseudo.copy into a fresh vreg in the
    // required class and rewrite the operand.
    private void InsertConstraintFixupCopies(MirFunction function)
    {
        foreach (var block in function.Blocks)
        {
            var i = 0;
            while (i < block.Instructions.Count)
            {
                var instr = block.Instructions[i];
                var info = DialectRegistry.ById(instr.Opcode.Dialect).GetInstructionInfo(instr.Opcode.Code);
                var operandClasses = info.OperandClasses;
                if (operandClasses == null)
                {
                    i++;
                    continue;
                }

                var operands = instr.Operands;
                var inserted = 0;

                for (var opIdx = 0; opIdx < operands.Length && opIdx < operandClasses.Length; opIdx++)
                {
                    var requiredClass = operandClasses[opIdx];
                    if (requiredClass == 0) continue;

                    if (operands[opIdx] is not VirtualReg vop) continue;

                    if (function.GetVRegAnnotation(vop.Id) is not ClassedVReg classed) continue;
                    if (classed.ClassId == requiredClass) continue;

                    var requiredName = ClassNameOrFallback(requiredClass);
                    var newVreg = function.CreateVirtualRegisterInClass(requiredClass, requiredName);

                    block.InsertInstruction(
                        i + inserted,
                        PseudoDialect.OpRef(PseudoOp.Copy),
                        new VirtualReg(newVreg, IsDefinition: true),
                        new VirtualReg(vop.Id, IsDefinition: false));
                    inserted++;

                    operands[opIdx] = new VirtualReg(newVreg, vop.IsDefinition);
                }

                i += 1 + inserted;
            }
        }
    }

    // Step 3 — after every tied-operand def whose vreg is read again later,
    // insert a pseudo.copy to a fresh vreg so the original tied-def physreg
    // can be clobbered by the next tied-def instruction in the chain. When a
    // flexible 8-bit class is available, the fresh vreg uses it so RA can
    // park the value in any free 8-bit physreg.
    //
    // Every later use of the original vreg (within the current block, after
    // the inserted copy) is rewritten to the fresh vreg. Without this, four
    // tied-def Ac-class vregs in an i32 add chain would all need to be live
    // through to the final return copies — which is impossible with a single
    // $A. Multi-block functions are not yet handled correctly for this case;
    // see the TODO below.
    private void InsertResultPreservationCopies(MirFunction function, int flexibleI8)
    {
        foreach (var block in function.Blocks)
        {
            var i = 0;
            while (i < block.Instructions.Count)
            {
                var instr = block.Instructions[i];
                var info = DialectRegistry.ById(instr.Opcode.Dialect).GetInstructionInfo(instr.Opcode.Code);
                var tiedOperands = info.TiedOperands;
                if (tiedOperands == null)
                {
                    i++;
                    continue;
                }

                var operands = instr.Operands;
                var inserted = 0;

                for (var defIdx = 0; defIdx < operands.Length; defIdx++)
                {
                    if (operands[defIdx] is not VirtualReg defOp || !defOp.IsDefinition)
                        continue;

                    if (!IsTiedDef(tiedOperands, defIdx)) continue;
                    if (!IsVregLiveAfter(block, i + inserted, defOp.Id)) continue;

                    int tempClass;
                    string tempName;
                    if (flexibleI8 != 0)
                    {
                        tempClass = flexibleI8;
                        tempName = ClassNameOrFallback(flexibleI8);
                    }
                    else if (function.GetVRegAnnotation(defOp.Id) is ClassedVReg classed)
                    {
                        tempClass = classed.ClassId;
                        tempName = classed.Name;
                    }
                    else
                    {
                        continue;
                    }

                    var tempVreg = function.CreateVirtualRegisterInClass(tempClass, tempName);
                    block.InsertInstruction(
                        i + 1 + inserted,
                        PseudoDialect.OpRef(PseudoOp.Copy),
                        new VirtualReg(tempVreg, IsDefinition: true),
                        new VirtualReg(defOp.Id, IsDefinition: false));
                    inserted++;

                    // TODO: redirect uses that follow in other blocks too once
                    // multi-block test coverage exists.
                    RedirectLaterUsesInBlock(block, i + 1 + inserted, defOp.Id, tempVreg);
                }

                i += 1 + inserted;
            }
        }
    }

    private static void RedirectLaterUsesInBlock(MirBlock block, int startIdx, int origVreg, int newVreg)
    {
        for (var j = startIdx; j < block.Instructions.Count; j++)
        {
            var instr = block.Instructions[j];
            var operands = instr.Operands;
            for (var k = 0; k < operands.Length; k++)
                if (operands[k] is VirtualReg v && !v.IsDefinition && v.Id == origVreg)
                    operands[k] = new VirtualReg(newVreg, IsDefinition: false);
        }
    }

    // Step 5 — linear scan with copy hints and clobber-aware physreg selection.
    // Returns vreg → physreg.
    //
    // Clobber awareness: instructions whose operand array includes implicit-def
    // PhysicalReg operands (e.g. mos6502.jsr.abs implicit-def $a, …) destroy
    // the named physreg's value. A vreg V can be placed on physreg P only if
    // no clobber of P falls strictly between V's def slot and V's last-use
    // slot. Without this check, a value parked in P during a call site would
    // silently get overwritten by the callee.
    private Dictionary<int, int> LinearScanAllocate(MirFunction function, Liveness liveness)
    {
        var intervals = liveness.RangeOf
            .Select(kv => (vreg: kv.Key, range: kv.Value))
            .OrderBy(x => x.range.Start)
            .ThenBy(x => x.vreg)
            .ToList();

        var clobberSlots = ComputeClobberSlots(function, liveness);
        var physRegReservations = ComputePhysRegReservations(function, liveness);

        var assignment = new Dictionary<int, int>();
        var physregActive = new Dictionary<int, int>();

        foreach (var (vreg, range) in intervals)
        {
            // Expire physregs whose holder's interval ended on or before this start.
            foreach (var (physReg, activeVreg) in physregActive.ToList())
                if (liveness.RangeOf[activeVreg].End <= range.Start)
                    physregActive.Remove(physReg);

            if (function.GetVRegAnnotation(vreg) is not ClassedVReg classed)
                throw new InvalidOperationException(
                    $"RegisterAllocator: vreg %{vreg} has no register class.");

            var allocatable = registerInfo.GetAllocatableRegisters(classed.ClassId);
            if (allocatable.Length == 0)
                throw new InvalidOperationException(
                    $"RegisterAllocator: no allocatable registers in class {classed.Name}.");

            var hint = TryGetCopyHintPhysReg(function, vreg, allocatable);

            bool Available(int candidate) =>
                !physregActive.ContainsKey(candidate)
                && IsClobberFree(candidate, range, clobberSlots)
                && IsReservationFree(candidate, vreg, range, physRegReservations);

            int? chosen = null;
            if (hint.HasValue && Available(hint.Value))
                chosen = hint.Value;

            if (chosen == null)
            {
                foreach (var candidate in allocatable)
                {
                    if (Available(candidate))
                    {
                        chosen = candidate;
                        break;
                    }
                }
            }

            if (chosen == null)
                throw new NotImplementedException(
                    $"RegisterAllocator: no free physical register in class {classed.Name} " +
                    $"for vreg %{vreg} (spilling not implemented, or the live range crosses a " +
                    "physreg-clobbering instruction such as mos6502.jsr.abs).");

            assignment[vreg] = chosen.Value;
            physregActive[chosen.Value] = vreg;
        }

        return assignment;
    }

    // For each physreg, the set of instruction slots where it is implicitly
    // defined (clobbered) by some non-copy instruction. pseudo.copy explicit
    // physreg defs are NOT clobbers — RA places them on purpose.
    //
    // A pseudo.copy can still clobber a physreg the target uses as hidden
    // scratch when it lowers the copy (e.g. MOS6502 routes an immediate-into-zp
    // copy through $A). Those clobbers are queried from the target via
    // GetPseudoCopyScratchClobbers and recorded at the copy's slot, so RA won't
    // keep an unrelated live value in that scratch register across the copy.
    private Dictionary<int, List<int>> ComputeClobberSlots(MirFunction function, Liveness liveness)
    {
        var clobbers = new Dictionary<int, List<int>>();

        void Record(int physReg, int slot)
        {
            if (!clobbers.TryGetValue(physReg, out var slots))
                clobbers[physReg] = slots = new List<int>();
            slots.Add(slot);
        }

        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (!liveness.SlotOf.TryGetValue(instr, out var slot)) continue;

                foreach (var op in instr.Operands)
                {
                    if (op is PhysicalReg { IsDefinition: true, IsImplicit: true } p)
                        Record(p.Id, slot);
                }

                if (IsCopy(instr) && instr.Operands.Length == 2
                    && instr.Operands[0] is VirtualReg { IsDefinition: true } dstVreg
                    && function.GetVRegAnnotation(dstVreg.Id) is ClassedVReg dstClass)
                {
                    var src = instr.Operands[1];
                    var srcClass = ResolveOperandClass(function, src);
                    foreach (var scratch in registerInfo.GetPseudoCopyScratchClobbers(
                                 src, srcClass, dstClass.ClassId))
                        Record(scratch, slot);
                }
            }
        }
        return clobbers;
    }

    // Live ranges of physical registers that hold a value across instructions
    // without that value being represented by a vreg. The motivating case: a
    // call (mos6502.jsr.abs) implicit-defs its result physregs ($a, $x, $zp2,
    // $zp3 for an i32), and the caller reads each back with a `pseudo.copy
    // %save = $P` to relocate it. Between the call and that copy the physreg is
    // live, yet liveness (vreg-only) never sees it. Without reserving it, RA
    // may park an unrelated vreg's eviction on $zp2 *before* the copy reads
    // $zp2, destroying the result — the same kind of physreg-liveness gap as
    // the $A-scratch clobber, but on the *source* side of a copy.
    //
    // For each physreg P we walk the function in slot order, tracking the slot
    // of the most recent instruction that defines P (explicit or implicit). A
    // `pseudo.copy %v = $P` reading P closes a reservation interval
    // [defSlot, readSlot] owned by the consumer vreg %v. A redefinition of P
    // resets the tracked def slot. The consumer is recorded so it is not
    // excluded from using P itself (it legitimately reads P at the interval's
    // end).
    private Dictionary<int, List<(int Start, int End, int Consumer)>> ComputePhysRegReservations(
        MirFunction function, Liveness liveness)
    {
        var reservations = new Dictionary<int, List<(int, int, int)>>();
        // Most recent def slot per physreg; -1 = no live value to protect.
        var lastDefSlot = new Dictionary<int, int>();

        void Reserve(int physReg, int start, int end, int consumer)
        {
            if (!reservations.TryGetValue(physReg, out var list))
                reservations[physReg] = list = new List<(int, int, int)>();
            list.Add((start, end, consumer));
        }

        foreach (var block in function.Blocks)
        {
            // Physregs live on entry to the block (call-convention params for the
            // entry block, cross-block live values elsewhere) are defined "before"
            // the block, so seed their def slot at the block's first instruction.
            // A relocation copy reading such a live-in must keep the physreg
            // reserved up to that read, exactly as for an instruction-produced def.
            var firstSlot = block.Instructions.Count > 0
                && liveness.SlotOf.TryGetValue(block.Instructions[0], out var fs)
                ? fs
                : -1;
            if (firstSlot >= 0)
                foreach (var liveIn in block.LiveIns)
                    lastDefSlot[liveIn] = firstSlot;

            foreach (var instr in block.Instructions)
            {
                if (!liveness.SlotOf.TryGetValue(instr, out var slot)) continue;

                // A `pseudo.copy %v = $P` reads physreg P: close P's reservation.
                if (IsCopy(instr) && instr.Operands.Length == 2
                    && instr.Operands[0] is VirtualReg { IsDefinition: true } dst
                    && instr.Operands[1] is PhysicalReg { IsDefinition: false } srcReg
                    && lastDefSlot.TryGetValue(srcReg.Id, out var defSlot)
                    && defSlot >= 0)
                {
                    Reserve(srcReg.Id, defSlot, slot, dst.Id);
                }

                // Record physreg defs (explicit or implicit) as the new live point.
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg { IsDefinition: true } d)
                        lastDefSlot[d.Id] = slot;
            }
        }
        return reservations;
    }

    // Vreg V (range Vr) may use physreg P unless a reservation interval of P —
    // owned by a different consumer — overlaps Vr. Overlap is the half-open
    // test `Vr.Start < resEnd && Vr.End > resStart`: a vreg born exactly at the
    // reservation's end (the consumer reading P) does not conflict, and a vreg
    // dying exactly at the reservation's start does not conflict either.
    private static bool IsReservationFree(
        int physReg, int vreg, LiveRange range,
        Dictionary<int, List<(int Start, int End, int Consumer)>> reservations)
    {
        if (!reservations.TryGetValue(physReg, out var list)) return true;
        foreach (var (start, end, consumer) in list)
        {
            if (vreg == consumer) continue;
            if (range.Start < end && range.End > start) return false;
        }
        return true;
    }

    // Register class of a copy operand as known during clobber analysis: a vreg
    // carries its ClassedVReg annotation; a physreg's class is derived from the
    // target's class membership; an Immediate (or anything else) has no class.
    private int ResolveOperandClass(MirFunction function, MirOperand operand)
    {
        switch (operand)
        {
            case VirtualReg v
                when function.GetVRegAnnotation(v.Id) is ClassedVReg classed:
                return classed.ClassId;
            case PhysicalReg p:
                return registerInfo.ClassOfPhysicalRegister(p.Id);
            default:
                return 0;
        }
    }

    // V at P safely iff no clobber slot K of P satisfies Vs < K < Ve.
    // A clobber at V's def slot (K == Vs) is fine (V is "born" at or after
    // the clobber). A clobber at V's last-use slot (K == Ve) is fine too
    // because the use reads V's value before the clobber takes effect within
    // the same instruction.
    private static bool IsClobberFree(int physReg, LiveRange range, Dictionary<int, List<int>> clobberSlots)
    {
        if (!clobberSlots.TryGetValue(physReg, out var slots)) return true;
        foreach (var k in slots)
            if (k > range.Start && k < range.End)
                return false;
        return true;
    }

    // Hint rule — only the livein form: `%v = pseudo.copy $P` hints `%v` to
    // `$P`, letting CopyEliminationPass collapse the resulting identity copy
    // after RA. The symmetric "vreg = use of `$P = pseudo.copy vreg`" hint
    // and the vreg-to-vreg propagation rules are intentionally omitted —
    // they tend to drag scarce single-physreg-class registers (like $A) into
    // long-lived flexible vregs and starve later one-physreg-class demand.
    // Without proper live-range splitting, suppressing those hint forms is
    // the simplest correctness fix; coalescing of the result-copy chains is
    // left as future work.
    private static int? TryGetCopyHintPhysReg(
        MirFunction function,
        int vreg,
        ReadOnlySpan<int> allocatable)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode.Dialect != PseudoDialect.Id) continue;
                if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) continue;
                if (instr.Operands.Length != 2) continue;

                if (instr.Operands[0] is VirtualReg vDef && vDef.IsDefinition && vDef.Id == vreg
                    && instr.Operands[1] is PhysicalReg pUse && Contains(allocatable, pUse.Id))
                    return pUse.Id;
            }
        }
        return null;
    }

    // Step 6 — rewrite every VirtualReg operand to PhysicalReg.
    private static void ApplyAssignment(MirFunction function, IReadOnlyDictionary<int, int> assignment)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                {
                    if (operands[i] is not VirtualReg v) continue;
                    if (!assignment.TryGetValue(v.Id, out var physReg))
                        throw new InvalidOperationException(
                            $"RegisterAllocator: vreg %{v.Id} has no physical register assignment.");
                    operands[i] = new PhysicalReg(physReg, v.IsDefinition);
                }
            }
        }
    }

    // Step 7 — block live-ins = physregs that are live on entry. The entry
    // block's call-convention live-ins are already populated by AbiLowering;
    // for every other block, replace whatever was there with the physregs
    // whose vregs are in `liveIn[block]` per the assignment computed above.
    private static void RecomputeBlockLiveIns(
        MirFunction function, Liveness liveness, IReadOnlyDictionary<int, int> assignment)
    {
        for (var i = 0; i < function.Blocks.Count; i++)
        {
            var block = function.Blocks[i];
            if (i == 0) continue;

            block.LiveIns.Clear();
            if (!liveness.LiveIn.TryGetValue(block, out var liveVregs)) continue;
            foreach (var vreg in liveVregs)
                if (assignment.TryGetValue(vreg, out var physReg) && !block.LiveIns.Contains(physReg))
                    block.LiveIns.Add(physReg);
        }
    }

    private static bool IsTiedDef(int[] tiedOperands, int defIdx)
    {
        for (var useIdx = 0; useIdx < tiedOperands.Length; useIdx++)
            if (tiedOperands[useIdx] == defIdx) return true;
        return false;
    }

    private static bool IsVregLiveAfter(MirBlock block, int instrIdx, int vreg)
    {
        for (var i = instrIdx + 1; i < block.Instructions.Count; i++)
            foreach (var op in block.Instructions[i].Operands)
                if (op is VirtualReg v && v.Id == vreg)
                    return !v.IsDefinition;
        return false;
    }

    private static bool Contains(ReadOnlySpan<int> values, int target)
    {
        foreach (var v in values)
            if (v == target) return true;
        return false;
    }

    private string ClassNameOrFallback(int classId) =>
        registerInfo.GetRegisterClassName(classId) ?? $"class{classId}";
}
