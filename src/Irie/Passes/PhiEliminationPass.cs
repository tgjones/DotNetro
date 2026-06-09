using Irie.Dialects.Cf;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// Destroys SSA block-argument form by replacing block parameters with explicit
// pseudo.copy instructions inserted at the end of each predecessor (before its
// terminator). After this pass no block has parameters; copies carry the
// values that the terminators used to carry via BlockTarget.Args.
//
// Parallel-copy semantics are preserved: when the values being copied form a
// cycle (e.g. a loop that swaps two live variables), a fresh temporary vreg
// breaks the cycle.
//
// Critical-edge / multi-successor handling
// ----------------------------------------
// The phi-copies for an edge pred -> block must execute *only when that edge is
// taken*. Inserting them before pred's terminator is correct only if pred has a
// single successor — then control always flows pred -> block and the copies run
// on exactly that edge.
//
// If pred has more than one successor (its terminator is a `cf.cond_br`, which
// has a taken and a fall-through edge), inserting the copies before the shared
// terminator would run them on *both* edges. That is the bug described in the
// register-allocator redesign plan §1.4 / §5: a `cf.cond_br` carrying block-args
// on an edge leaves stray vregs after RA because the copies landed in the wrong
// place.
//
// The standard fix is **critical-edge splitting**: for any edge whose source has
// multiple successors, we insert a fresh intermediate block on that edge. The
// pred's branch target is redirected to the new block; the new block contains a
// plain `cf.br block(args)`. The new block has a single successor, so the
// phi-copies can be inserted there safely. (We split on "pred has >1 successor"
// rather than the textbook "critical edge" test — source has >1 successor *and*
// target has >1 predecessor — because even a non-critical edge from a multi-
// successor branch needs its copies isolated to one edge; splitting it too is
// harmless.)
public sealed class PhiEliminationPass : MirFunctionPass
{
    // When phi-elim runs after instruction selection, a generic `cf.br` placed
    // in a split block would never be lowered and would crash machine-code
    // emission. This supplies the target's real unconditional jump for those
    // blocks. Null (or a still-generic source terminator) keeps `cf.br`.
    private readonly BranchLowering? _branchLowering;

    public PhiEliminationPass(BranchLowering? branchLowering = null)
    {
        _branchLowering = branchLowering;
    }

    public override string Name => "PhiElimination";

    public override void Run(MirFunction function)
    {
        function.RebuildCfg();

        // Phase 1: split every multi-successor edge that feeds a block with
        // parameters, so each such edge owns a single-successor block into which
        // its phi-copies can be placed. This rewrites BlockTarget operands, so it
        // must run (and the CFG be rebuilt) before we insert any copies.
        SplitMultiSuccessorEdgesIntoParamBlocks(function, _branchLowering);
        function.RebuildCfg();

        // Phase 2: replace each block's parameters with copies in its
        // predecessors. After phase 1 every predecessor of a parameterized block
        // has exactly one successor, so inserting copies before its terminator is
        // correct.
        foreach (var block in function.Blocks.ToList())
        {
            if (block.Parameters.Count == 0) continue;

            foreach (var pred in block.Predecessors)
            {
                // Phase 1 guarantees this: a parameterized block is only ever
                // reached from single-successor predecessors.
                if (pred.Successors.Count > 1)
                    throw new InvalidOperationException(
                        "PhiEliminationPass: parameterized block still reached from a " +
                        "multi-successor predecessor after critical-edge splitting.");

                var (termIdx, terminator, btOperandIdx, target) = FindBranch(pred, block);

                if (target.Args.Length != block.Parameters.Count)
                    throw new InvalidOperationException(
                        $"Block has {block.Parameters.Count} parameters but predecessor " +
                        $"passes {target.Args.Length} arguments.");

                var copies = block.Parameters
                    .Zip(target.Args, (param, arg) => (dst: param, src: arg))
                    .ToList();

                var seq = Sequentialize(copies, function);

                for (var i = 0; i < seq.Count; i++)
                {
                    var (dst, src) = seq[i];
                    pred.InsertInstruction(termIdx + i,
                        PseudoDialect.OpRef(PseudoOp.Copy),
                        new VirtualReg(dst, IsDefinition: true),
                        src);
                }

                terminator.Operands[btOperandIdx] = new BlockTarget(target.Block, []);
            }

            block.Parameters.Clear();
        }
    }

    // Splits every edge whose source block has more than one successor and whose
    // target block has parameters. For each such edge a fresh block is created
    // holding a plain `cf.br target(args)`; the source's branch operand is
    // redirected from `target(args)` to `splitBlock()` (no args). The args travel
    // with the unconditional branch into the new single-successor block, where
    // phase 2 can lower them to copies safely.
    private static void SplitMultiSuccessorEdgesIntoParamBlocks(
        MirFunction function, BranchLowering? branchLowering)
    {
        var builder = new MirBuilder(function);

        foreach (var block in function.Blocks.ToList())
        {
            // A block's terminator may carry several BlockTarget operands (e.g.
            // cf.cond_br has two). Walk every instruction's operands and split
            // each multi-successor edge that targets a parameterized block.
            //
            // We must decide "multi-successor" from the source's distinct
            // successor set, so compute it once per source block.
            var distinctSuccessors = new HashSet<MirBlock>();
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is BlockTarget t)
                        distinctSuccessors.Add(t.Block);

            if (distinctSuccessors.Count <= 1) continue;

            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var j = 0; j < operands.Length; j++)
                {
                    if (operands[j] is not BlockTarget target) continue;
                    if (target.Block.Parameters.Count == 0) continue;

                    // Create the intermediate block carrying the unconditional
                    // branch (with the original args) into the real target. If
                    // the source terminator has already been instruction-selected
                    // (it is no longer a generic `cf.*` op) a generic `cf.br`
                    // here would never be lowered, so ask the target for its jump.
                    var edge = new BlockTarget(target.Block, target.Args);
                    var splitBlock = function.CreateBlock();
                    builder.SetInsertionPointAtEnd(splitBlock);
                    if (branchLowering != null && instr.Opcode.Dialect != CfDialect.Id)
                    {
                        branchLowering.InsertUnconditionalBranch(builder, edge);
                    }
                    else
                    {
                        builder.BuildInstruction(CfDialect.OpRef(CfOp.Br), edge);
                    }

                    // Redirect the source's branch to the split block; the args
                    // now live on the cf.br inside it, not on this edge.
                    operands[j] = new BlockTarget(splitBlock, []);
                }
            }
        }
    }

    // Returns (terminator index, terminator instruction, operand index of the
    // BlockTarget in the terminator's operand array, the BlockTarget itself)
    // for the first terminator operand in pred that points at target.
    private static (int termIdx, MirInstruction term, int btOperandIdx, BlockTarget target)
        FindBranch(MirBlock pred, MirBlock targetBlock)
    {
        for (var i = 0; i < pred.Instructions.Count; i++)
        {
            var instr = pred.Instructions[i];
            for (var j = 0; j < instr.Operands.Length; j++)
            {
                if (instr.Operands[j] is BlockTarget bt && bt.Block == targetBlock)
                    return (i, instr, j, bt);
            }
        }
        throw new InvalidOperationException(
            "No terminator found in predecessor that branches to the target block.");
    }

    // Converts a list of parallel copies {dst_i := src_i} into an ordered
    // sequence of sequential copies that produce the same effect.
    // When the copies form a cycle (e.g. two variables swapping), a fresh
    // temporary vreg is inserted to break the cycle.
    private static List<(int dst, MirOperand src)> Sequentialize(
        List<(int dst, MirOperand src)> copies,
        MirFunction function)
    {
        var result = new List<(int, MirOperand)>();
        var remaining = copies.ToList();

        while (remaining.Count > 0)
        {
            var emitted = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var (dst, _) = remaining[i];
                var dstUsedAsSrc = remaining
                    .Where((_, j) => j != i)
                    .Any(c => c.src is VirtualReg vv && vv.Id == dst);
                if (!dstUsedAsSrc)
                {
                    result.Add(remaining[i]);
                    remaining.RemoveAt(i);
                    emitted = true;
                    break;
                }
            }

            if (!emitted)
            {
                var (cycleDst, cycleSrc) = remaining[0];
                if (cycleSrc is not VirtualReg cycleVreg)
                    throw new InvalidOperationException(
                        "PhiEliminationPass: cycle involving a non-vreg source operand.");

                var annotation = function.GetVRegAnnotation(cycleVreg.Id);
                var temp = annotation switch
                {
                    TypedVReg t   => function.CreateVirtualRegister(t.Type),
                    ClassedVReg c => function.CreateVirtualRegisterInClass(c.ClassId, c.Name),
                    _ => throw new InvalidOperationException(
                        $"PhiEliminationPass: vreg %{cycleVreg.Id} has unknown annotation {annotation}."),
                };

                result.Add((temp, cycleSrc));
                remaining[0] = (cycleDst, new VirtualReg(temp, IsDefinition: false));
            }
        }

        return result;
    }
}
