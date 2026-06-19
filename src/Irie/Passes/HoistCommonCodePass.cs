using Irie.Mir;

namespace Irie.Passes;

// Generic mid-level code hoisting on generic-dialect SSA MIR — the scoped
// analogue of LLVM's *common-code hoisting* family.
//
// LLVM implements this idea at two altitudes, and that split is the design we
// mirror here:
//
//   * SimplifyCFG's `hoistCommonCodeFromSuccessors`
//     (llvm/lib/Transforms/Utils/SimplifyCFG.cpp) — the *local* version: when a
//     branch's successor arms begin with identical instructions, hoist one copy
//     into the predecessor. It only ever looks one edge up, at successors whose
//     sole predecessor is the branch block.
//   * `GVNHoist` (llvm/lib/Transforms/Scalar/GVNHoist.cpp) — the *global*
//     version: a dedicated pass that hoists fully-anticipated expressions to
//     their nearest common dominator, across merges and blocks with more than
//     two successors, driven by an ANTIC (anticipation) analysis.
//
// This pass starts at the SimplifyCFG-local altitude but lives in its own pass
// (rather than folded into a broader CFG-cleanup pass) precisely because its
// intended growth path is GVNHoist, which LLVM also ships as a standalone pass —
// and because Irie already factors SimplifyCFG transforms into focused passes
// (see ReturnMergePass, the `tailMergeBlocksWithSimilarFunctionTerminators`
// analogue). Keeping it separate means the generalization to full GVN-hoist
// happens *in here* by swapping one decision — "which block do common successor
// instructions hoist to?" — from "the sole predecessor" to "the nearest common
// dominator + anticipation", without disturbing the matching/cloning machinery.
//
// Why the sole-predecessor restriction is sound *and* needs no dominance check:
// if `block` is the only predecessor of `succ`, then every value visible at
// `succ`'s entry is either defined in `block` or dominates `block`, so it is
// necessarily available at the end of `block` (our insertion point, just before
// the terminator). A leading instruction's operands are visible at `succ`'s
// entry by SSA, hence available where we hoist to — for free. And because we
// only hoist an instruction present in *every* successor, hoisting it to run
// once in `block` never speculates work onto a path that didn't already do it,
// so it is value-preserving and strictly reduces the static instruction count.
//
// EXPANSION TO FULL GVN-HOIST (future): the only piece tied to the local
// restriction is `TryGetHoistTargets` — it answers "given a block, which
// successor blocks can we hoist *out of*, and where do we hoist *to*?" Today it
// requires sole-predecessor successors and hoists into the branch block itself.
// A dominator-tree + anticipation version would instead, for each expression,
// find the set of blocks that compute it on all forward paths and the nearest
// common dominator to hoist into (adding an availability check, since the
// sole-predecessor "free availability" guarantee no longer holds). The
// `SameComputation` matcher and `HoistInto` cloner are already
// dominance-agnostic and would carry over unchanged.
//
// Runs early, on generic SSA after ArithSimplify and before ABI lowering, so the
// hoisted form is exposed to legalization/isel and (for the common pattern of a
// partial sum reused across guard-clause exits, e.g. the early-return corpus
// case) a duplicated multi-byte add becomes a single one.
public sealed class HoistCommonCodePass : MirFunctionPass
{
    public override string Name => "HoistCommonCode";

    public override void Run(MirFunction function)
    {
        var builder = new MirBuilder(function);

        // We read Successors/Predecessors below; ensure they reflect this MIR.
        // Hoisting moves instructions but never changes edges, so one build holds.
        function.RebuildCfg();

        // Iterate to a fixpoint: hoisting one leading instruction exposes the next
        // one underneath it as the new common leading instruction.
        bool changedAny;
        do
        {
            changedAny = false;
            foreach (var block in function.Blocks)
                while (TryHoistOnce(function, builder, block))
                    changedAny = true;
        } while (changedAny);
    }

    // Attempt to hoist a single common leading instruction out of `block`'s
    // successors into `block`. Returns true if it hoisted (so the caller retries
    // to peel the next common instruction).
    private static bool TryHoistOnce(MirFunction function, MirBuilder builder, MirBlock block)
    {
        if (!TryGetHoistTargets(block, out var targets, out var terminator))
            return false;

        // The leading hoistable instruction of the first target is the template;
        // every other target must begin with an identical computation.
        if (LeadingHoistable(function, targets[0]) is not { } template)
            return false;
        for (var i = 1; i < targets.Count; i++)
            if (LeadingHoistable(function, targets[i]) is not { } candidate ||
                !SameComputation(template, candidate))
                return false;

        HoistInto(function, builder, block, terminator, template, targets);
        return true;
    }

    // The local sole-predecessor policy (the GVN-hoist expansion seam — see the
    // header comment). Yields the successor blocks we may hoist out of and the
    // instruction we hoist in front of. Requires a real branch (≥2 distinct
    // successors), each successor having `block` as its only predecessor and no
    // block parameters (a parameter would not be available in `block`).
    private static bool TryGetHoistTargets(MirBlock block,
        out List<MirBlock> targets, out MirInstruction terminator)
    {
        targets = [];
        terminator = null!;

        if (block.Instructions.Count == 0)
            return false;
        terminator = block.Instructions[^1];

        foreach (var succ in block.Successors)
            if (!targets.Contains(succ))
                targets.Add(succ);

        if (targets.Count < 2)
            return false;

        foreach (var succ in targets)
            if (succ.Predecessors.Count != 1 || succ.Predecessors[0] != block || succ.Parameters.Count != 0)
                return false;

        return true;
    }

    // The first instruction of `block`, if it is a pure, hoistable computation:
    // side-effect-free, not a terminator, not an artifact, with every definition
    // still a pre-isel TypedVReg (so we can clone its type) and no operand that
    // resists relocation (physregs / block targets).
    private static MirInstruction? LeadingHoistable(MirFunction function, MirBlock block)
    {
        if (block.Instructions.Count == 0)
            return null;

        var instr = block.Instructions[0];
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        if (dialect.IsTerminator(instr.Opcode.Code) ||
            dialect.IsArtifact(instr.Opcode.Code) ||
            !dialect.IsSideEffectFree(instr.Opcode.Code))
            return null;

        foreach (var operand in instr.Operands)
            switch (operand)
            {
                case VirtualReg { IsDefinition: true } def
                    when function.GetVRegAnnotation(def.Id) is not TypedVReg:
                    return null;
                case VirtualReg:
                case Immediate:
                case Symbol:
                    break;
                default:
                    // PhysicalReg, BlockTarget, etc. — not a pre-isel pure value op.
                    return null;
            }

        return instr;
    }

    // Two instructions compute the same value iff they share an opcode and have
    // pairwise-matching operands, where definition vregs are allowed to differ
    // (they name the *result*, not the computation) but every use, immediate and
    // symbol must be identical.
    private static bool SameComputation(MirInstruction a, MirInstruction b)
    {
        if (a.Opcode != b.Opcode || a.Operands.Length != b.Operands.Length)
            return false;

        for (var i = 0; i < a.Operands.Length; i++)
        {
            if (a.Operands[i] is VirtualReg { IsDefinition: true } &&
                b.Operands[i] is VirtualReg { IsDefinition: true })
                continue; // result vregs may differ
            if (!a.Operands[i].Equals(b.Operands[i]))
                return false;
        }

        return true;
    }

    // Hoist a copy of `template` into `block` before `terminator`, then redirect
    // every target's matching result vreg to the hoisted result and delete the
    // now-redundant per-target copies. Operand availability is guaranteed by the
    // sole-predecessor invariant (see header), so the clone reuses the template's
    // use-operands verbatim and only its definitions get fresh vregs.
    private static void HoistInto(MirFunction function, MirBuilder builder,
        MirBlock block, MirInstruction terminator, MirInstruction template, List<MirBlock> targets)
    {
        var defPositions = new List<int>();
        var hoistedOperands = new MirOperand[template.Operands.Length];
        for (var i = 0; i < template.Operands.Length; i++)
        {
            if (template.Operands[i] is VirtualReg { IsDefinition: true } def)
            {
                var type = ((TypedVReg)function.GetVRegAnnotation(def.Id)).Type;
                hoistedOperands[i] = new VirtualReg(function.CreateVirtualRegister(type), IsDefinition: true);
                defPositions.Add(i);
            }
            else
            {
                hoistedOperands[i] = template.Operands[i];
            }
        }

        builder.SetInsertionPointBefore(terminator);
        builder.BuildInstruction(template.Opcode, hoistedOperands);

        // For each target, map its leading instruction's result vreg(s) to the
        // hoisted result(s), then remove that instruction.
        foreach (var target in targets)
        {
            var original = target.Instructions[0];
            foreach (var position in defPositions)
            {
                var oldDef = ((VirtualReg)original.Operands[position]).Id;
                var newDef = ((VirtualReg)hoistedOperands[position]).Id;
                function.ReplaceAllUsesOfRegister(oldDef, newDef);
            }
            builder.Remove(original);
        }
    }
}
