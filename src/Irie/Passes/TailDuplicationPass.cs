using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// Tail-duplication — the llvm-mos `tailduplication` (TailDuplicator) analogue.
// It *rebalances* what the eager return-merge (ReturnMergePass, our SimplifyCFG
// mergeReturnBlocks analogue) and the branch-folder over-merged: when funnelling
// several returns through one shared block did not pay off — because the shared
// tail is trivial — it clones the tail back into the predecessors that jump to
// it, eliminating the jump.
//
// Generic, like LLVM's TailDuplicator: the duplication logic is target-agnostic
// and the few target-specific questions are delegated, exactly as LLVM routes
// them through TargetInstrInfo. "Is this a return?" → `Dialect.IsReturn`
// (MCInstrDesc::isReturn). "Is this an unconditional branch, and to where?" →
// `BranchLowering.TryGetUnconditionalBranchTarget` (the unconditional arm of
// analyzeBranch). "Is this a terminator?" → `Dialect.IsTerminator`.
//
// Placement: this runs as the LAST pass, just after the target's block-placement
// — not before it as llvm-mos's standalone `tailduplication` does. llvm-mos's
// block-placement integrates tail duplication (layout-aware TailDupPlacement);
// ours does not, so this post-placement pass is the faithful analogue. By running
// after placement we only ever see the unconditional-branch edges that placement
// could NOT turn into a free fall-through — exactly the jumps worth replacing
// with the inlined tail. Running before placement would clone over jumps that
// placement was about to drop for free, growing code (e.g. a single return
// reached by an adjacent jump that becomes a fall-through). By this point
// pseudo-expansion has also run, so the tail is final real instructions and the
// duplication-cost count is exact.
//
// Cost model (mirrors TailDuplicator::shouldTailDuplicate at -Os, where
// MaxDuplicateCount == 1: "duplicate only one, because one branch instruction can
// be eliminated to compensate for the duplication"): a tail block is duplicated
// only when its real-instruction count is <= 1. So a bare `rts` (1 instr) is
// cloned back — removing the jump that reached it at zero net instruction cost —
// while a genuinely-shared epilogue like `tya ; rts` (2 instrs) is left merged.
//
// Scope: we only duplicate **return-terminated** tails (last instruction
// satisfies Dialect.IsReturn) into predecessors that reach them via an
// unconditional branch (a redirectable edge). A return tail has no successors,
// so cloning it needs no successor/PHI fix-up, and post-RA physregs mean no SSA
// update — exactly the trivial-tail rebalancing we need, nothing more.
// Predecessors that fall through into the tail, or reach it via a conditional
// branch, are left untouched (the tail stays for them).
public sealed class TailDuplicationPass(BranchLowering branchLowering) : MirFunctionPass
{
    public override string Name => "TailDuplication";

    public override void Run(MirFunction function)
    {
        bool changed;
        do
        {
            changed = false;

            // Never duplicate the entry block (index 0).
            for (var i = 1; i < function.Blocks.Count; i++)
            {
                var tail = function.Blocks[i];
                if (!IsDuplicableReturnTail(tail)) continue;

                // Collect predecessors that reach `tail` via an unconditional
                // branch (the redirectable edges).
                var branchPreds = new List<MirBlock>();
                foreach (var pred in function.Blocks)
                {
                    if (ReferenceEquals(pred, tail)) continue;
                    if (pred.Instructions.Count == 0) continue;
                    if (branchLowering.TryGetUnconditionalBranchTarget(pred.Instructions[^1], out var target)
                        && ReferenceEquals(target, tail))
                    {
                        branchPreds.Add(pred);
                    }
                }

                if (branchPreds.Count == 0) continue;

                // Clone the tail into each branch-predecessor, replacing the branch.
                foreach (var pred in branchPreds)
                {
                    pred.Instructions.RemoveAt(pred.Instructions.Count - 1);
                    foreach (var instr in tail.Instructions)
                        pred.AddInstruction(instr.Opcode, CloneOperands(instr.Operands));
                }

                // If nothing reaches the tail any more (no remaining branch into
                // it and nothing falls through to it physically), remove it.
                function.RebuildCfg();
                if (tail.Predecessors.Count == 0 && !IsFallenInto(function, tail))
                    function.Blocks.Remove(tail);

                changed = true;
                break; // CFG / indices changed; restart the scan
            }
        } while (changed);

        function.RebuildCfg();
    }

    // A block is a duplicable return tail when its last instruction is a return
    // (Dialect.IsReturn) and its real-instruction count is within the cost limit
    // (<= 1; see the header comment). Identity `pseudo.copy $r -> $r` instructions
    // generate no code and are treated as transparent.
    private static bool IsDuplicableReturnTail(MirBlock block)
    {
        if (block.Instructions.Count == 0) return false;
        if (!IsReturn(block.Instructions[^1])) return false;

        var realCount = 0;
        foreach (var instr in block.Instructions)
        {
            if (IsIdentityCopy(instr)) continue;
            realCount++;
            if (realCount > 1) return false;
        }
        return true;
    }

    private static bool IsReturn(MirInstruction instr)
        => DialectRegistry.ById(instr.Opcode.Dialect).IsReturn(instr.Opcode.Code);

    private static bool IsIdentityCopy(MirInstruction instr)
    {
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        return instr.Operands.Length == 2
            && instr.Operands[0] is PhysicalReg { IsDefinition: true } def
            && instr.Operands[1] is PhysicalReg { IsDefinition: false } use
            && def.Id == use.Id;
    }

    // True if `block` is reached by physical fall-through: the block laid out
    // immediately before it does not end in a terminator (so control flows into
    // `block`). A block ending in a conditional branch counts — conditional
    // branches are not terminators in our model and fall through.
    private static bool IsFallenInto(MirFunction function, MirBlock block)
    {
        var index = function.Blocks.IndexOf(block);
        if (index <= 0) return false; // entry, or not found
        var prev = function.Blocks[index - 1];
        if (prev.Instructions.Count == 0) return true; // empty block falls through
        return !IsTerminator(prev.Instructions[^1]);
    }

    private static bool IsTerminator(MirInstruction instr)
        => DialectRegistry.ById(instr.Opcode.Dialect).IsTerminator(instr.Opcode.Code);

    // Deep-copy the operand array (a fresh array sharing the immutable operand
    // records) so the clone and original never share a mutable operand slot.
    private static MirOperand[] CloneOperands(MirOperand[] operands)
        => (MirOperand[])operands.Clone();
}
