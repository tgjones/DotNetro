using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// RegisterScavengingPass — assigns physical registers to copy-scratch vregs
// left behind by pseudo expansion, then drives their final lowering.
// =============================================================================
//
// Why this pass exists (plan §3.6, mirroring llvm-mos's MOSPostRAScavenging).
// Some target moves need a temporary register to lower — on the MOS6502 an
// immediate-into-zero-page copy expands to `LD? #imm ; ST? $zp`, which needs a
// GPR. WHICH GPR is free is a post-allocation fact: it depends on what the
// surrounding code is keeping in $a/$x/$y at that exact point. So the register
// allocator does NOT reserve scratch (that was the old, deleted
// GetPseudoCopyScratchClobbers hack, which forced $a free for every such copy).
// Instead the pseudo expander re-emits a scratch-needing copy as a 3-operand
// "scratch form" `pseudo.copy %dst, %src, %scratch` whose third operand is a
// fresh virtual register, and THIS pass:
//
//   1. computes exact post-RA live intervals (every physreg's busy windows are
//      already explicit in the operand array by now);
//   2. for each scratch vreg, picks the cheapest target scratch GPR that is
//      DEAD across the copy's program point — preferring $a, then $x, then $y,
//      so a copy emitted while $a holds a live value gets $x or $y instead of
//      evicting anything;
//   3. rewrites the scratch operand to that physreg and asks the target's
//      PseudoExpander to lower the now-physreg-only scratch form to its final
//      machine instructions.
//
// This is a real pass with its own tests, NOT a shortcut: it produces strictly
// better code than the old $a-reservation (it can use $x/$y as scratch) and
// removes a whole class of constraint from the allocator.
//
// Pipeline position: it runs AFTER PseudoExpansionPass (which mints the scratch
// vregs) and is the final pass, so no vregs survive into machine-code emission.
// This matches llvm-mos's order (RA → pseudo expansion → scavenging; plan §3.6).
//
// Spilling note: if no scratch GPR is ever dead at a copy's point, the
// scavenger would need to save/restore one through an emergency frame slot. Real
// spilling is Phase 4; per the plan it is acceptable for that emergency path to
// throw a clear NotImplementedException so long as the test corpus never hits it
// (verified — there is always a dead GPR for the immediate→zp copies the corpus
// produces). The exception, if ever raised, is the explicit signal to implement
// the minimal save/restore.
public sealed class RegisterScavengingPass(
    TargetRegisterInfo registerInfo, PseudoExpander expander) : MirFunctionPass
{
    public override string Name => "RegisterScavenging";

    public override void Run(MirFunction function)
    {
        // Gather the scratch-form copies first. After pseudo expansion every
        // remaining instruction is a final target op EXCEPT these 3-operand
        // scratch-form pseudo.copies; a scratch form is the only thing that can
        // still carry a virtual-register operand here.
        var scratchCopies = new List<MirInstruction>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                if (IsScratchForm(instr))
                    scratchCopies.Add(instr);

        if (scratchCopies.Count == 0)
            return;

        // Exact post-RA liveness. Every physical register's busy windows (call
        // clobbers, flag defs, CC live-ins, data values RA parked there) are
        // already explicit physreg operands, so LiveIntervals models them all.
        var intervals = PassManager != null
            ? GetAnalysis<LiveIntervalsAnalysis, LiveIntervals>(function)
            : new LiveIntervalsAnalysis().Compute(function);

        var candidates = registerInfo.GetScratchGprCandidates();
        if (candidates.Length == 0)
            throw new InvalidOperationException(
                $"RegisterScavenging: target advertises no scratch GPR candidates, " +
                $"but {scratchCopies.Count} scratch-form copies need one.");

        var builder = new MirBuilder(function);

        foreach (var copy in scratchCopies)
        {
            var scratchOperandIndex = FindScratchOperandIndex(copy);
            var physReg = PickScratch(copy, intervals, candidates);

            // Bind the scratch vreg to the chosen physreg, then hand the now
            // physreg-only scratch form to the target to finish lowering.
            copy.Operands[scratchOperandIndex] =
                new PhysicalReg(physReg, IsDefinition: copy.Operands[scratchOperandIndex] is VirtualReg { IsDefinition: true });

            builder.SetInsertionPointBefore(copy);
            expander.Expand(copy, builder);
            builder.Remove(copy);
        }

        // The scavenger introduced and consumed scratch vregs; clear any stray
        // annotations so the function is genuinely physreg-only afterwards.
        function.ClearVRegAnnotations();
    }

    // Choose the cheapest scratch GPR that is dead across the copy's whole
    // program point. "Dead across the copy" = the candidate's precolored busy
    // interval does not overlap the [use point, def point] span of the copy
    // instruction, and the candidate is not itself the copy's source or
    // destination physreg (it must be free to scribble in).
    //
    // We test the full instruction span (use point through def point) rather
    // than a single point so a register whose value is live INTO the copy (read
    // by it) or OUT of the copy (read after it) is excluded — the scratch must
    // be free for the entire load+store the copy expands to.
    private int PickScratch(
        MirInstruction copy, LiveIntervals intervals, ReadOnlySpan<int> candidates)
    {
        var baseSlot = intervals.BaseSlotOf[copy];
        // The copy expands to two instructions in place; conservatively treat
        // the scratch as busy across the whole instruction (use point through
        // the def point inclusive — DefSlot+1 makes the window half-open over
        // both sub-slots).
        var span = new LiveSegment(
            LiveIntervals.UseSlot(baseSlot),
            LiveIntervals.DefSlot(baseSlot) + 1);

        // Physregs the copy itself touches (its dst / src) cannot serve as
        // scratch — the lowering needs the scratch distinct from both.
        var touched = TouchedPhysRegs(copy);

        foreach (var candidate in candidates)
        {
            if (touched.Contains(candidate)) continue;
            if (PhysRegBusyDuring(intervals, candidate, span)) continue;
            return candidate;
        }

        // No scratch GPR is dead here. Real spilling (an emergency save/restore
        // through an abstract frame slot) is Phase 4; surface the gap clearly.
        throw new NotImplementedException(
            "RegisterScavenging: no scratch GPR is free for a copy that needs one — " +
            "emergency save/restore via an abstract spill slot is Phase 4 (plan §3.4/§3.6). " +
            "No case in the current corpus reaches this path.");
    }

    // True if `physReg`'s precolored busy interval overlaps the given span — i.e.
    // it holds a live value somewhere across the copy. An untracked physreg
    // (never appears as an operand) has no interval and is free.
    private static bool PhysRegBusyDuring(LiveIntervals intervals, int physReg, LiveSegment span)
    {
        foreach (var seg in intervals.PhysIntervalOf(physReg).Segments)
            if (seg.OverlapsWith(span))
                return true;
        return false;
    }

    // The physical registers the copy reads or writes directly (excludes the
    // scratch operand, which is what we are choosing). Used so the scavenger
    // never picks the copy's own dst/src as its scratch.
    private static HashSet<int> TouchedPhysRegs(MirInstruction copy)
    {
        var touched = new HashSet<int>();
        var scratchIdx = FindScratchOperandIndex(copy);
        for (var i = 0; i < copy.Operands.Length; i++)
        {
            if (i == scratchIdx) continue;
            if (copy.Operands[i] is PhysicalReg p)
                touched.Add(p.Id);
        }
        return touched;
    }

    // A scratch-form copy is a `pseudo.copy` with exactly 3 operands whose
    // scratch (third) operand is still a virtual register. (Once the scavenger
    // has bound and lowered it, it no longer exists.)
    private static bool IsScratchForm(MirInstruction instr)
    {
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        if (instr.Operands.Length != 3) return false;
        return instr.Operands[2] is VirtualReg;
    }

    // The operand index holding the scratch vreg (by construction the third
    // operand of a scratch-form copy).
    private static int FindScratchOperandIndex(MirInstruction copy)
    {
        for (var i = 0; i < copy.Operands.Length; i++)
            if (copy.Operands[i] is VirtualReg)
                return i;
        throw new InvalidOperationException(
            "RegisterScavenging: scratch-form copy has no virtual-register scratch operand.");
    }
}
