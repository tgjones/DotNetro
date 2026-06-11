using Irie.Mir;

namespace Irie.Target;

// Target hook for PhiEliminationPass. When the pass splits a critical edge
// *after* instruction selection, the fresh split block needs an unconditional
// branch into the real target. A generic `cf.br` would never get lowered and
// would crash machine-code emission, so the target supplies its own jump
// (e.g. `mos6502.jmp.abs`).
//
// This mirrors LLVM's `TargetInstrInfo::insertUnconditionalBranch`, which
// `MachineBasicBlock::SplitCriticalEdge` calls for exactly this reason: PHI
// elimination runs on already-selected machine IR, so the split block's
// terminator must be a target branch, not a generic one.
//
// The pass only consults this for edges whose source terminator has already
// been selected; while the function is still generic it emits a plain `cf.br`
// itself (see PhiEliminationPass.SplitMultiSuccessorEdgesIntoParamBlocks).
public abstract class BranchLowering
{
    // Emit an unconditional branch to `target` at the builder's current
    // insertion point (the end of a freshly-created split block).
    public abstract void InsertUnconditionalBranch(MirBuilder builder, BlockTarget target);
}
