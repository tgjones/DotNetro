using Irie.Dialects.Pseudo;
using Irie.Mir;

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
// Critical edges (one predecessor with multiple successors, one successor with
// multiple predecessors) cannot be handled without splitting the edge first.
// If a critical edge is detected and the target block has parameters, this
// pass throws NotImplementedException.
public sealed class PhiEliminationPass : MirFunctionPass
{
    public override string Name => "PhiElimination";

    public override void Run(MirFunction function)
    {
        function.RebuildCfg();

        foreach (var block in function.Blocks.ToList())
        {
            if (block.Parameters.Count == 0) continue;

            foreach (var pred in block.Predecessors)
            {
                if (pred.Successors.Count > 1 && block.Predecessors.Count > 1)
                    throw new NotImplementedException(
                        "PhiEliminationPass: critical edge with block parameters — " +
                        "split the critical edge before running this pass.");

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
