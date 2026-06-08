using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// RegisterAllocatorPass — graph-colouring register allocation with iterated
// register coalescing (Chaitin / Briggs / George–Appel).
// =============================================================================
//
// Reference: Appel, *Modern Compiler Implementation* ch. 11. The worklist
// structure, the simplify/coalesce/freeze/select loop, Briggs' conservative
// coalescing test and George's precoloured test are all taken directly from
// that chapter. A reader who has not written a register allocator should be
// able to follow the algorithm from the comments here together with the
// "Reader's primer" in notes/register-allocator-redesign-plan.md.
//
// Input:  post-isel, post-PhiElim, post-TwoAddress MIR. Every vreg has a
//         ClassedVReg annotation; no block parameters; tied uses already
//         materialised as a pseudo.copy that re-defines the def vreg.
// Output: physreg-only MIR. Every VirtualReg operand has been replaced with a
//         PhysicalReg. Block live-ins are recomputed against the final
//         assignment. The vreg annotation table is cleared.
//
// -----------------------------------------------------------------------------
// What this phase changed (Phase 3 of the register-allocator redesign)
// -----------------------------------------------------------------------------
// The previous pass was linear scan wrapped in compensating machinery: three
// "widen to flexible class" pre-passes, InsertConstraintFixupCopies,
// InsertResultPreservationCopies, a single-direction copy hint with a
// starvation guard, and a TryGetDeadCopySourcePhysReg special case. ALL of
// that is deleted here. Iterated register coalescing subsumes every one of
// them:
//
//   * The widen passes existed because instruction selection pins flexible
//     values to single-physreg classes (e.g. `ac`). We no longer widen the
//     *class*; instead each vreg's allocatable register set is the
//     INTERSECTION of the register classes implied by all its defs and uses
//     (AllowedRegistersOf below). A value that is only ever `ac` keeps $a; a
//     value that is `ac` at one use but `any8` elsewhere is free to colour
//     anywhere $a/$x/zp. This is plan §3.2's sanctioned "interim path":
//     compute the class-intersection in RA rather than relocating constraint
//     copies into isel.
//
//   * InsertConstraintFixupCopies / InsertResultPreservationCopies inserted
//     copies to relieve class pressure. The coalescer makes them unnecessary:
//     a tied-def whose result is read later simply does not coalesce with the
//     next chain link if doing so would make the graph uncolourable, and a
//     real move survives.
//
//   * The copy-hint hack and TryGetDeadCopySourcePhysReg are the trivial cases
//     of coalescing — a copy whose two ends can share a register vanishes; a
//     dead copy is a copy that coalesces with its source and disappears.
//
// Spilling is still NOT implemented (Phase 4). We use Briggs' OPTIMISTIC
// colouring: a node that cannot be proven low-degree is pushed onto the select
// stack anyway ("spill candidate") in the hope a colour is free when we pop it.
// Only a genuine select failure — a popped node with no free colour — throws
// NotImplementedException. Phase 4 turns that throw into a real spill.
public sealed class RegisterAllocatorPass(TargetRegisterInfo registerInfo) : MirFunctionPass
{
    public override string Name => "RegisterAllocator";

    public override void Run(MirFunction function)
    {
        // Relocation-copy insertion: a value produced in a single-physreg-class
        // register (e.g. a `mos6502.adc` result, hard-constrained to $a by its
        // def operand class) that is read again AFTER another instruction
        // re-defines that same physreg cannot stay there — there is one $a and
        // two values needing it live at once. We insert a `pseudo.copy` into a
        // FLEXIBLE-class fresh vreg right after such a def and redirect the later
        // uses to it. This does NOT decide a register — it merely materialises
        // the move the coalescer then relocates onto whatever register is free
        // (the reference parks it in $y; we park it in the zp pool). It is the
        // standard "result must vacate a constrained reg" shape; the coalescer
        // consumes it exactly like the tied-operand and phi copies (plan §3.3).
        //
        // (This is the surviving, genuinely-load-bearing remnant of the old
        // InsertResultPreservationCopies: without an explicit move in the IR no
        // colourer — coalescing or otherwise — can relocate a value off a
        // single-physreg class, because there is no instruction to carry it.
        // The old heuristic register choice and its multi-block TODO are gone;
        // the coalescer now picks the register from real interference.)
        var flexibleI8 = registerInfo.FlexibleI8ClassId;
        if (flexibleI8 != 0)
            InsertRelocationCopiesForConstrainedDefs(function, flexibleI8);

        // Physreg-aware live intervals (with holes) are the single source of
        // interference truth. LiveIntervals.Interfere(a,b) is the vreg↔vreg
        // edge test; LiveIntervals.Overlaps(vreg, physReg) is the vreg↔precolour
        // edge test. Computed AFTER relocation-copy insertion so it sees the
        // final IR shape (we mutate the function, so the cached analysis cannot
        // be reused).
        var intervals = new LiveIntervalsAnalysis().Compute(function);

        var allocator = new GraphColouringAllocator(function, registerInfo, intervals);
        var assignment = allocator.Run();

        ApplyAssignment(function, assignment);
        function.ClearVRegAnnotations();
        RecomputeBlockLiveIns(function, intervals, assignment);
    }

    // -------------------------------------------------------------------------
    // Insert a relocation pseudo.copy after each tied-operand def whose result
    // is read later in the same block. The fresh vreg is in the flexible 8-bit
    // class, so the coalescer is free to place the relocated value on ANY free
    // register (it does not commit to one here). Later in-block uses of the
    // original def vreg are redirected to the fresh vreg.
    //
    // Trigger: a tied def (the def end of a two-address instruction such as
    // mos6502.adc). After TwoAddressInstructionPass the def vreg is also the
    // tied use, so it is hard-pinned to the op's single-physreg class ($a). If
    // its value outlives the instruction it MUST be moved off that register
    // before the next link in the chain re-defines it; that is what this copy
    // provides. Block-local: a value live OUT of the block is handled the same
    // way the old pass did (no cross-block redirect), which suffices for the
    // straight-line arithmetic chains this targets — branchy cases keep the
    // value in the constrained reg only within the defining block.
    // -------------------------------------------------------------------------
    private void InsertRelocationCopiesForConstrainedDefs(MirFunction function, int flexibleI8)
    {
        var flexibleName = registerInfo.GetRegisterClassName(flexibleI8) ?? $"class{flexibleI8}";

        foreach (var block in function.Blocks)
        {
            var i = 0;
            while (i < block.Instructions.Count)
            {
                var instr = block.Instructions[i];
                var info = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code);
                var tiedOperands = info.TiedOperands;
                if (tiedOperands == null) { i++; continue; }

                var operands = instr.Operands;
                var inserted = 0;

                for (var defIdx = 0; defIdx < operands.Length; defIdx++)
                {
                    if (operands[defIdx] is not VirtualReg defOp || !defOp.IsDefinition)
                        continue;
                    if (!IsTiedDef(tiedOperands, defIdx)) continue;
                    if (!IsVregUsedLaterInBlock(block, i + inserted, defOp.Id)) continue;

                    var tempVreg = function.CreateVirtualRegisterInClass(flexibleI8, flexibleName);
                    block.InsertInstruction(
                        i + 1 + inserted,
                        Dialects.Pseudo.PseudoDialect.OpRef(Dialects.Pseudo.PseudoOp.Copy),
                        new VirtualReg(tempVreg, IsDefinition: true),
                        new VirtualReg(defOp.Id, IsDefinition: false));
                    inserted++;

                    RedirectLaterUsesInBlock(block, i + 1 + inserted, defOp.Id, tempVreg);
                }

                i += 1 + inserted;
            }
        }
    }

    private static bool IsTiedDef(int[] tiedOperands, int defIdx)
    {
        foreach (var t in tiedOperands)
            if (t == defIdx) return true;
        return false;
    }

    // Is the vreg read by some instruction after instrIdx in this block (before
    // any re-definition)? A def re-establishes a fresh value, so we stop there.
    private static bool IsVregUsedLaterInBlock(MirBlock block, int instrIdx, int vreg)
    {
        for (var j = instrIdx + 1; j < block.Instructions.Count; j++)
        {
            foreach (var op in block.Instructions[j].Operands)
                if (op is VirtualReg v && v.Id == vreg)
                    return !v.IsDefinition;
        }
        return false;
    }

    private static void RedirectLaterUsesInBlock(MirBlock block, int startIdx, int origVreg, int newVreg)
    {
        for (var j = startIdx; j < block.Instructions.Count; j++)
        {
            var operands = block.Instructions[j].Operands;
            for (var k = 0; k < operands.Length; k++)
                if (operands[k] is VirtualReg v && !v.IsDefinition && v.Id == origVreg)
                    operands[k] = new VirtualReg(newVreg, IsDefinition: false);
        }
    }

    // -------------------------------------------------------------------------
    // Rewrite every VirtualReg operand to PhysicalReg using the final colouring.
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // Block live-ins = physregs live on entry. The entry block's CC live-ins
    // are already populated by AbiLowering; for every other block, replace
    // whatever was there with the physregs whose vregs are live on entry, per
    // the final assignment. "Live on entry to block B" is read straight off the
    // intervals: a vreg is live-in iff its interval covers B's first use point.
    // -------------------------------------------------------------------------
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
}
