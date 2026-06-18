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
// Unlike the LLVM analogue — which merges every function-terminating block of a
// kind with no cost model — we add one minimal profitability gate: merge only
// when the return value is wider than one byte. LLVM can merge unconditionally
// because its backend tail-duplication / branch-folding rebalances afterwards;
// Irie has no such pass, so an unconditional merge pessimizes the trivial-tail
// cases. The epilogue length is proportional to the return-value byte count:
// merging N returns saves (N-1) epilogues but adds (N-1) branches plus per-edge
// copies (which mostly coalesce away). For a void or single-byte return the
// epilogue is just `rts` (or one copy), so the added branch never pays for
// itself; from i16 up the saved copies dominate. Hence the `> 8 bits` gate.
//
// Runs first, before AbiLowering lowers `core.return`, on generic SSA MIR.
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

        // Profitability gate: only multi-byte returns have an epilogue worth
        // sharing (see the header comment).
        if (function.ReturnType.SizeInBits <= 8) return;

        var builder = new MirBuilder(function);

        // Create the common return block: `retblock(%p): core.return %p`.
        var commonBlock = function.CreateBlock();
        var param = function.CreateVirtualRegister(function.ReturnType);
        commonBlock.Parameters.Add(param);
        builder.SetInsertionPointAtEnd(commonBlock);
        builder.BuildInstruction(
            CoreDialect.OpRef(CoreOp.Return),
            new VirtualReg(param, IsDefinition: false));

        // Rewrite each original `core.return %v` into `cf.br retblock(%v)`,
        // carrying the returned value as the edge's block argument.
        foreach (var block in returnBlocks)
        {
            var terminator = block.Instructions[^1];
            if (terminator.Operands.Length == 0)
                throw new InvalidOperationException(
                    $"ReturnMergePass: core.return in '{function.Name}' has no operand " +
                    $"but the function returns {function.ReturnType.DisplayName}.");

            builder.SetInsertionPointBefore(terminator);
            builder.BuildInstruction(
                CfDialect.OpRef(CfOp.Br),
                new BlockTarget(commonBlock, [terminator.Operands[0]]));
            builder.Remove(terminator);
        }

        function.RebuildCfg();
    }

    private static bool IsCoreReturn(MirInstruction instr) =>
        instr.Opcode.Dialect == CoreDialect.Id
        && (CoreOp)instr.Opcode.Code == CoreOp.Return;
}
