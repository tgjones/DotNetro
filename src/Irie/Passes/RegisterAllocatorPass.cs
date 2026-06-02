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
            WidenLiveinsToFlexibleClass(function, flexibleI8);

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

    // Step 5 — linear scan with copy hints. Returns vreg → physreg.
    private Dictionary<int, int> LinearScanAllocate(MirFunction function, Liveness liveness)
    {
        var intervals = liveness.RangeOf
            .Select(kv => (vreg: kv.Key, range: kv.Value))
            .OrderBy(x => x.range.Start)
            .ThenBy(x => x.vreg)
            .ToList();

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

            int? chosen = null;
            if (hint.HasValue && !physregActive.ContainsKey(hint.Value))
                chosen = hint.Value;

            if (chosen == null)
            {
                foreach (var candidate in allocatable)
                {
                    if (!physregActive.ContainsKey(candidate))
                    {
                        chosen = candidate;
                        break;
                    }
                }
            }

            if (chosen == null)
                throw new NotImplementedException(
                    $"RegisterAllocator: no free physical register in class {classed.Name} " +
                    $"for vreg %{vreg} (spilling not implemented).");

            assignment[vreg] = chosen.Value;
            physregActive[chosen.Value] = vreg;
        }

        return assignment;
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
