using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// RegisterCoalescerPass — standalone copy coalescer, mirroring llvm-mos's
// RegisterCoalescer (llvm/lib/CodeGen/RegisterCoalescer.cpp).
// =============================================================================
//
// Runs BEFORE register allocation, on post-isel / post-PhiElim / post-TwoAddress
// MIR (the llvm slot: PHIElimination → TwoAddress → LiveIntervals →
// RegisterCoalescer → … → allocator → VirtRegRewriter). It DELETES a
// `pseudo.copy` by merging the live ranges of its two ends whenever they do not
// interfere — so the allocator sees one value where there were two, and never
// has to spend a register on the copy.
//
// -----------------------------------------------------------------------------
// What it does, and deliberately does not do (faithful to the reference)
// -----------------------------------------------------------------------------
//   * vreg↔vreg full copies → ELIMINATE NOW. Join the two vregs (rename the
//     victim's defs AND uses to the survivor) and delete the copy. This is the
//     `%2 = COPY %0` join the reference performs after TwoAddress.
//
//   * vreg↔physreg copies → LEFT AS HINTS. llvm's canJoinPhys
//     (RegisterCoalescer.cpp:1999) only merges a vreg into a RESERVED physreg;
//     our allocatable $a/$x/$y/$zpN are not reserved, and our only reserved
//     physregs (RC0/RC1 soft stack pointer) never appear in a pseudo.copy. So
//     EVERY copy touching a PhysicalReg is skipped here and survives as an
//     allocation hint — folded later by the allocator's hint assignment plus
//     CopyEliminationPass (our handleIdentityCopy / machine-cp analogue).
//
//   * AGGRESSIVE, not conservative. Like llvm (and unlike GraphColouringAllocator's
//     Briggs/George coalescer), we join on NON-INTERFERENCE alone — no degree
//     test. Over-aggressive merges are recovered downstream by the greedy
//     allocator's split/evict; correctness is never at risk, because the
//     interference test already proves the two values are never simultaneously
//     live (so they can share one register).
//
// -----------------------------------------------------------------------------
// Correctness invariant (the load-bearing one)
// -----------------------------------------------------------------------------
// The two-address copy `%5 = pseudo.copy %0` (inserted by TwoAddressInstructionPass
// so the destructive `adc` re-defines %5 while %0 survives) is removed ONLY when
// %0 is dead after it. If %0 were still read past the adc, %0 and %5 would both
// be live across the adc and Interfere returns true — the copy is preserved.
// This is exactly why TwoAddress inserts the copy and why the coalescer may drop
// it. The interference test alone guarantees this.
//
// -----------------------------------------------------------------------------
// Reanalysis instead of incremental liveness
// -----------------------------------------------------------------------------
// llvm maintains LiveIntervals incrementally across joins; we recompute them
// from scratch after each successful join. The same trade GreedyRegisterAllocator
// documents: function sizes on this target make a fresh O(n) analysis cheaper
// than incremental-update machinery. The outer "loop until no progress" mirrors
// the reference's copyCoalesceWorkList re-drain (RegisterCoalescer.cpp:4088/4246).
public sealed class RegisterCoalescerPass(TargetRegisterInfo registerInfo) : MirFunctionPass
{
    public override string Name => "RegisterCoalescer";

    public override void Run(MirFunction function)
    {
        var changed = true;
        while (changed)
        {
            changed = false;

            // Fresh interference + class-intersection picture each scan. Both are
            // global (slots span every block), so cross-block phi-elimination
            // copies coalesce for free and cross-block interference is exact.
            var intervals = new LiveIntervalsAnalysis().Compute(function);
            var vregs = RegisterAllocationSupport.CollectReferencedVregs(function);
            var allowed = RegisterAllocationSupport.ComputeAllowedColours(function, registerInfo, vregs);

            foreach (var block in function.Blocks)
            {
                foreach (var copy in block.Instructions)
                {
                    if (!IsVregVregCopy(copy, out var dst, out var src))
                        continue; // not a copy, or a physreg copy (left as a hint).

                    if (dst == src)
                    {
                        // Already the same vreg — an identity copy. Drop it.
                        block.Instructions.Remove(copy);
                        changed = true;
                        break;
                    }

                    // The two ends must never be simultaneously live...
                    if (intervals.Interfere(dst, src)) continue;
                    // ...and the merged value must still be placeable: the
                    // class-intersection the allocator would compute is non-empty.
                    if (!AllowedSetsIntersect(allowed, dst, src)) continue;

                    // Deterministic survivor (lower id) so the printed vreg
                    // numbering — which the characterization goldens pin — is stable.
                    var (survivor, victim) = dst < src ? (dst, src) : (src, dst);
                    RenameRegister(function, from: victim, to: survivor);
                    block.Instructions.Remove(copy);
                    changed = true;
                    break; // recompute intervals/allowed, then rescan.
                }

                if (changed) break;
            }
        }
    }

    // A pseudo.copy whose BOTH operands are virtual registers. Copies touching a
    // physical register are intentionally rejected here (they are left as hints).
    private static bool IsVregVregCopy(MirInstruction instr, out int dst, out int src)
    {
        dst = src = 0;
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        if (instr.Operands.Length != 2) return false;
        if (instr.Operands[0] is not VirtualReg { IsDefinition: true } d) return false;
        if (instr.Operands[1] is not VirtualReg { IsDefinition: false } s) return false;
        dst = d.Id;
        src = s.Id;
        return true;
    }

    // The merged value's real allowed set ⊇ allowed[a] ∩ allowed[b] (its operand
    // sites are the union of a's and b's, minus the deleted copy), so a non-empty
    // intersection guarantees the allocator can still colour it. allowed[] already
    // folds in copy-only widening and per-operand-class hard constraints.
    private static bool AllowedSetsIntersect(IReadOnlyDictionary<int, int[]> allowed, int a, int b)
    {
        if (!allowed.TryGetValue(a, out var ra) || !allowed.TryGetValue(b, out var rb))
            return false;
        foreach (var r in ra)
            if (RegisterAllocationSupport.Contains(rb, r))
                return true;
        return false;
    }

    // Rewrite every occurrence of vreg `from` — DEFS as well as uses — to `to`,
    // preserving IsDefinition and recursing into BlockTarget.Args. (MirFunction's
    // ReplaceAllUsesOfRegister rewrites uses only; the victim's two-address def in
    // e.g. the adc must be renamed too.)
    private static void RenameRegister(MirFunction function, int from, int to)
    {
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                    operands[i] = RenameInOperand(operands[i], from, to);
            }
    }

    private static MirOperand RenameInOperand(MirOperand op, int from, int to) => op switch
    {
        VirtualReg v when v.Id == from => v with { Id = to },
        BlockTarget bt => bt with { Args = RenameInArgs(bt.Args, from, to) },
        _ => op,
    };

    private static MirOperand[] RenameInArgs(MirOperand[] args, int from, int to)
    {
        var rewritten = new MirOperand[args.Length];
        for (var i = 0; i < args.Length; i++)
            rewritten[i] = RenameInOperand(args[i], from, to);
        return rewritten;
    }
}
