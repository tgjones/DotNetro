using Irie.Dialects.Cf;
using Irie.Dialects.Core;
using Irie.Mir;

namespace Irie.Passes;

// Merges multiple function-terminating `core.return` blocks into a single common
// return block, mirroring LLVM SimplifyCFG's
// `tailMergeBlocksWithSimilarFunctionTerminators` /
// `performBlockTailMerging` (llvm/lib/Transforms/Scalar/SimplifyCFGPass.cpp).
//
// A function with N separate `core.return %v` blocks lowers, at AbiLowering, to N
// copies of the same return epilogue (unmerge + per-byte copy to the result
// physregs + pseudo.return). Funnelling all returns through one block turns those
// into a single epilogue: each original return becomes `cf.br retblock(%v)`, and
// the returned value rides in as a block parameter (the PHI). PhiElimination then
// breaks the block-arg into per-edge copies, the coalescing allocator collapses
// them, and the existing branch-folder / block-placement passes recover the
// fall-through. The net effect matches llvm-mos's shared `bb.N: ...; rts` tail.
//
// Like the LLVM analogue this carries **no cost model** — the only gate is "at
// least two return blocks" ("We don't want to change IR just because we can.").
// LLVM merges unconditionally here and relies on a downstream tail-duplicator to
// rebalance: where merging didn't pay (a trivial `rts`-only tail), the post-RA
// TailDuplication pass clones the tail back into its jmp-predecessors, undoing
// the merge. We follow the same division of labour — merge eagerly here, rebalance
// later (see MOS6502TailDuplicationPass) — rather than guessing profitability
// up-front with a target-specific size heuristic.
//
// Runs first, before AbiLowering lowers `core.return`, on generic SSA MIR. Void
// and value returns never mix within one function (the return arity is fixed by
// the signature), so a single uniform merge handles either case.
public sealed class ReturnMergePass : MirFunctionPass
{
    public override string Name => "ReturnMerge";

    public override void Run(MirFunction function)
    {
        // Collect every block whose terminator is a `core.return`.
        var returnBlocks = new List<MirBlock>();
        foreach (var block in function.Blocks)
        {
            if (block.Instructions.Count == 0) continue;
            if (IsCoreReturn(block.Instructions[^1]))
                returnBlocks.Add(block);
        }

        // "Only do that if there are at least two blocks we'll tail-merge."
        if (returnBlocks.Count < 2) return;

        var builder = new MirBuilder(function);
        var isVoid = function.ReturnType is VoidType;

        // Create the common return block: `retblock(%p): core.return %p` for a
        // value return, or `retblock(): core.return` for void.
        var commonBlock = function.CreateBlock();
        builder.SetInsertionPointAtEnd(commonBlock);
        if (isVoid)
        {
            builder.BuildInstruction(CoreDialect.OpRef(CoreOp.Return));
        }
        else
        {
            var param = function.CreateVirtualRegister(function.ReturnType);
            commonBlock.Parameters.Add(param);
            builder.BuildInstruction(
                CoreDialect.OpRef(CoreOp.Return),
                new VirtualReg(param, IsDefinition: false));
        }

        // Rewrite each original `core.return %v` into `cf.br retblock(%v)`,
        // carrying the returned value as the edge's block argument.
        foreach (var block in returnBlocks)
        {
            var terminator = block.Instructions[^1];
            var args = isVoid || terminator.Operands.Length == 0
                ? []
                : new[] { terminator.Operands[0] };

            builder.SetInsertionPointBefore(terminator);
            builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(commonBlock, args));
            builder.Remove(terminator);
        }

        function.RebuildCfg();
    }

    private static bool IsCoreReturn(MirInstruction instr) =>
        instr.Opcode.Dialect == CoreDialect.Id
        && (CoreOp)instr.Opcode.Code == CoreOp.Return;
}
