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
//   4. Compute physreg-aware live intervals (LiveIntervals) on the now-final
//      shape. This single analysis gives EXACT interference — including the
//      busy windows of every physical register (call clobbers, flag defs, CC
//      live-ins, relocation reads). It subsumes the four ad-hoc reconstructions
//      the old pass needed (ComputeClobberSlots / ComputePhysRegReservations /
//      IsClobberFree / IsReservationFree), all of which were re-deriving physreg
//      liveness the analysis now tracks natively (plan §3.1).
//   5. Linear-scan physreg allocation with copy hints. A single
//      `physregActive` dictionary tracks which physregs are currently held by a
//      not-yet-expired vreg; expiry uses `endpoint ≤ currentStart` so a tied
//      def/use that meet at the same slot can share a physreg. A candidate
//      physreg is rejected when it is already held by a live vreg, OR when the
//      vreg's interval overlaps that physreg's precolored busy interval
//      (LiveIntervals.Overlaps).
//   6. Rewrite VirtualReg operands to PhysicalReg.
//   7. Recompute block live-ins from the final assignment + the vreg intervals.
//
// Spilling is not implemented; a NotImplementedException is thrown if no
// physical register is available in a class.
//
// NOTE (Phase 2 of the register-allocator redesign — see
// notes/register-allocator-redesign-plan.md). This pass was re-cored onto the
// physreg-aware LiveIntervals analysis, deleting ~250 lines of ad-hoc physreg-
// liveness reconstruction. Coalescing is still NOT done here (Phase 3): the
// widen pre-passes, InsertConstraintFixupCopies, InsertResultPreservationCopies,
// and the single-direction copy hint all STAY for now so output is ~unchanged.
// The copy-SCRATCH clobber concept was removed entirely: a copy that needs a
// scratch register is no longer a register-allocation concern — the post-RA
// RegisterScavengingPass picks copy scratch with exact post-RA liveness
// (plan §3.6).
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

        // Physreg-aware live intervals (with holes) are the single source of
        // interference truth. LiveIntervals.Overlaps(vreg, physReg) answers
        // "is this physreg busy anywhere the vreg is live?" — which is what the
        // deleted IsClobberFree / IsReservationFree used to answer by hand.
        var intervals = PassManager != null
            ? GetAnalysis<LiveIntervalsAnalysis, LiveIntervals>(function)
            : new LiveIntervalsAnalysis().Compute(function);

        var assignment = LinearScanAllocate(function, intervals);
        ApplyAssignment(function, assignment);
        function.ClearVRegAnnotations();
        RecomputeBlockLiveIns(function, intervals, assignment);
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

    // Step 5 — linear scan with copy hints over the physreg-aware LiveIntervals.
    // Returns vreg → physreg.
    //
    // Interference comes in two forms, both answered by LiveIntervals:
    //   (a) vreg ↔ vreg — two vregs assigned the same physreg must not be live
    //       at the same time. This is the classic linear-scan active set:
    //       physregActive[P] = the vreg currently parked in P. A holder expires
    //       once its interval ends on or before the current vreg's start (≤, so
    //       a tied def/use that meet at one slot can share a physreg).
    //   (b) vreg ↔ physreg — a vreg cannot live on a physreg that is busy (with
    //       a precolored value) anywhere the vreg is live. LiveIntervals tracks
    //       every physreg's busy windows directly — call clobbers
    //       (mos6502.jsr.abs implicit-defs), flag defs (mos6502.cmp's $n/$v/$z),
    //       CC live-ins, and the [def, relocation-read] window of a result
    //       physreg read back by a `pseudo.copy %v = $P`. So a single
    //       `intervals.Overlaps(vreg, P)` replaces BOTH the old
    //       IsClobberFree (implicit-def clobbers) and IsReservationFree
    //       (relocation reservations). The half-open sub-slot numbering makes
    //       "the relocation copy reads $P exactly where its consumer is born"
    //       a non-overlap automatically — no special consumer-exception needed.
    private Dictionary<int, int> LinearScanAllocate(MirFunction function, LiveIntervals intervals)
    {
        // Order vregs by interval start, then id, for a deterministic scan.
        var ordered = function.VirtualRegisterIds
            .Where(v => !intervals.IntervalOf(v).IsEmpty)
            .Select(v => (vreg: v, interval: intervals.IntervalOf(v)))
            .OrderBy(x => x.interval.Start)
            .ThenBy(x => x.vreg)
            .ToList();

        // Per single-physreg class P-only-register, the vregs constrained to it.
        // Used by the copy-hint starvation guard below.
        var singlePhysClassDemand = ComputeSinglePhysClassDemand(function);

        var assignment = new Dictionary<int, int>();
        var physregActive = new Dictionary<int, int>();

        foreach (var (vreg, interval) in ordered)
        {
            var start = interval.Start;

            // Expire physregs whose holder's interval ended on or before this
            // start. (≤, not <, so a value that dies exactly where the next is
            // born can reuse the register — the tied def/use sharing case.)
            foreach (var (physReg, activeVreg) in physregActive.ToList())
                if (intervals.IntervalOf(activeVreg).End <= start)
                    physregActive.Remove(physReg);

            if (function.GetVRegAnnotation(vreg) is not ClassedVReg classed)
                throw new InvalidOperationException(
                    $"RegisterAllocator: vreg %{vreg} has no register class.");

            var allocatable = registerInfo.GetAllocatableRegisters(classed.ClassId);
            if (allocatable.Length == 0)
                throw new InvalidOperationException(
                    $"RegisterAllocator: no allocatable registers in class {classed.Name}.");

            // Dead copy from a physreg: `%v = pseudo.copy $src` where %v is never
            // used. This is the canonical shape of an unused calling-convention
            // argument byte — AbiLowering emits a livein copy + pseudo.merge for
            // every parameter byte, and when the callee ignores the parameter the
            // merge (and hence these copies) are dead. They survive to RA because
            // nothing earlier DCE's them.
            //
            // Such a copy MUST NOT be given a fresh register: it still *writes*
            // its destination, so parking it on, say, $zp4 clobbers whatever live
            // value occupies $zp4 at that point (a real miscompile observed in the
            // runtime WriteLineInt32, whose i32 parameter is unused). Assign the
            // copy its OWN source register instead: that turns it into an identity
            // `$src = pseudo.copy $src`, which CopyEliminationPass then deletes —
            // emitting no instruction and clobbering nothing. This is exactly what
            // the old vreg-only allocator did incidentally via its copy hint; here
            // we make it explicit and independent of interference, because a dead
            // identity copy is always safe regardless of what else is live.
            // (Coalescing — Phase 3 — will subsume this as the trivial case of a
            // copy whose two ends can share a register.)
            if (TryGetDeadCopySourcePhysReg(function, vreg, allocatable) is int deadSrc)
            {
                // Deliberately NOT recorded in physregActive: the copy is an
                // identity that will be deleted, so it reserves nothing.
                assignment[vreg] = deadSrc;
                continue;
            }

            var hint = TryGetCopyHintPhysReg(function, vreg, allocatable);

            // Starvation guard for the copy hint (see HintStarvesConstrainedClass).
            // Honour the hint only when steering this (flexible) vreg onto the
            // hinted physreg would not leave a class-constrained vreg with
            // nowhere to go.
            if (hint.HasValue
                && HintStarvesConstrainedClass(vreg, hint.Value, classed.ClassId, singlePhysClassDemand, intervals))
                hint = null;

            // P is available for this vreg iff no live vreg already holds it AND
            // the vreg's interval does not overlap P's precolored busy interval.
            bool Available(int candidate) =>
                !physregActive.ContainsKey(candidate)
                && !intervals.Overlaps(vreg, candidate);

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

    // For each single-physreg register class (a class with exactly one
    // allocatable physreg, e.g. `ac`→$a, `xc`→$x), the list of vregs constrained
    // to it, keyed by that physreg. These vregs have *no choice* of register —
    // they MUST live on that one physreg — so they are the values most easily
    // starved. Built once per function for the hint starvation guard.
    private Dictionary<int, List<int>> ComputeSinglePhysClassDemand(MirFunction function)
    {
        var demand = new Dictionary<int, List<int>>();
        foreach (var vreg in function.VirtualRegisterIds)
        {
            if (function.GetVRegAnnotation(vreg) is not ClassedVReg classed) continue;
            var regs = registerInfo.GetAllocatableRegisters(classed.ClassId);
            if (regs.Length != 1) continue; // not a single-physreg class
            var p = regs[0];
            if (!demand.TryGetValue(p, out var list))
                demand[p] = list = [];
            list.Add(vreg);
        }
        return demand;
    }

    // Starvation guard for the single-direction copy hint.
    //
    // The hint says "%v was copied from $P, so prefer giving %v the register $P
    // to collapse the copy." That is a pure win when $P is a register %v could
    // freely use anyway. But when %v is a *flexible* value (it could live in the
    // abundant zero-page pool) and $P is a *scarce single-physreg* register that
    // another value is FORCED onto, honouring the hint parks the flexible value
    // on the scarce register for its whole (often long) live range and leaves
    // the forced value with nowhere to go — RA then aborts for lack of a free
    // physreg. We have no live-range splitting (Phase 6) to recover, so the only
    // safe move is to decline the hint and let %v take the zp pool instead.
    //
    // Concretely: decline the hint to $P iff (a) the vreg's own class offers
    // more than just $P (it is flexible, so declining costs only the copy, not
    // correctness), and (b) some *other* vreg that is constrained to the
    // single-physreg class of $P interferes with this vreg's interval — i.e.
    // would be starved if we took $P across it.
    //
    // This is exact-interval reasoning, not a heuristic: it fires precisely when
    // taking the hinted register would make a class-constrained neighbour
    // uncolourable. It reproduces the effect the now-deleted imm→zp $a-scratch
    // clobber used to have incidentally (it kept long-lived flexible vregs off
    // $a because their ranges crossed the const-materialisation clobbers), but
    // does so from real interference instead of a scratch-clobber side effect.
    // (Conservative coalescing — Phase 3 — will subsume this with a principled
    // Briggs/George test; for Phase 2 this targeted guard keeps output stable.)
    private bool HintStarvesConstrainedClass(
        int vreg, int hintPhysReg, int vregClassId,
        Dictionary<int, List<int>> singlePhysClassDemand,
        LiveIntervals intervals)
    {
        // (a) Is the vreg flexible (its class offers a register other than the
        // hinted one)? If the vreg is itself constrained to $P, the hint is the
        // only option and must stand.
        var ownRegs = registerInfo.GetAllocatableRegisters(vregClassId);
        if (ownRegs.Length <= 1) return false;

        // (b) Does any vreg forced onto $P's single-physreg class interfere with
        // this vreg's interval? If so, taking $P here would starve it.
        if (!singlePhysClassDemand.TryGetValue(hintPhysReg, out var forced)) return false;
        foreach (var other in forced)
        {
            if (other == vreg) continue;
            if (intervals.Interfere(vreg, other)) return true;
        }
        return false;
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

    // If `vreg` is the dead destination of a `pseudo.copy %vreg = $src` (two
    // operands, physreg source, and `%vreg` has no remaining uses), return the
    // source physreg so the caller can assign it and make the copy an identity.
    // Returns null otherwise. The source must lie in `allocatable` so the
    // identity assignment is legal for the vreg's class (always true in practice
    // — a livein copy's source is the CC register, which is in the flexible
    // class the widen pass gave the destination).
    private static int? TryGetDeadCopySourcePhysReg(
        MirFunction function, int vreg, ReadOnlySpan<int> allocatable)
    {
        // A used vreg is not a dead copy; bail before the (more expensive)
        // def-site search.
        if (function.GetUseCount(vreg) > 0) return null;

        var def = function.GetDefinition(vreg);
        if (def == null) return null;
        if (def.Opcode.Dialect != PseudoDialect.Id) return null;
        if ((PseudoOp)def.Opcode.Code != PseudoOp.Copy) return null;
        if (def.Operands.Length != 2) return null;

        if (def.Operands[0] is not VirtualReg { IsDefinition: true } d || d.Id != vreg) return null;
        if (def.Operands[1] is not PhysicalReg { IsDefinition: false } src) return null;
        if (!Contains(allocatable, src.Id)) return null;

        return src.Id;
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
    // for every other block, replace whatever was there with the physregs whose
    // vregs are live on entry to the block, per the final assignment.
    //
    // "Live on entry to block B" is read straight off the intervals: a vreg is
    // live-in iff its interval covers B's first use point. (LiveIntervals
    // anchors a value live across the B-1→B boundary so its segment reaches B's
    // entry point — see LiveIntervalsAnalysis step 3.) This replaces the old
    // per-block LiveIn vreg-set the coarse analysis exposed.
    private static void RecomputeBlockLiveIns(
        MirFunction function, LiveIntervals intervals, IReadOnlyDictionary<int, int> assignment)
    {
        for (var i = 0; i < function.Blocks.Count; i++)
        {
            var block = function.Blocks[i];
            if (i == 0) continue;
            if (block.Instructions.Count == 0) { block.LiveIns.Clear(); continue; }

            var entryPoint = LiveIntervals.UseSlot(intervals.BaseSlotOf[block.Instructions[0]]);

            block.LiveIns.Clear();
            // Deterministic order (by vreg id) so the printed live-in list is
            // stable across runs — the characterization lit tests depend on it.
            foreach (var vreg in assignment.Keys.OrderBy(v => v))
                if (intervals.IntervalOf(vreg).Covers(entryPoint)
                    && assignment.TryGetValue(vreg, out var physReg)
                    && !block.LiveIns.Contains(physReg))
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
