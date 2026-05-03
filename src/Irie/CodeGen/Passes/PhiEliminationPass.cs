namespace Irie.CodeGen.Passes;

// Destroys SSA block-argument form by replacing block parameters with explicit
// GenericCopy instructions inserted at the end of each predecessor (before its
// terminator).  After this pass no block has parameters; copies carry the
// values that the terminators used to carry implicitly.
//
// Parallel-copy semantics are preserved: when the values being copied form a
// cycle (e.g. a loop that swaps two live variables), a fresh temporary vreg
// breaks the cycle.
//
// Critical edges (one predecessor with multiple successors, one successor with
// multiple predecessors) cannot be handled without splitting the edge first.
// If a critical edge is detected and the target block has parameters, this
// pass throws NotImplementedException.
public sealed class PhiEliminationPass : MachineFunctionPass
{
    public override string Name => "PhiElimination";

    public override void Run(MachineFunction function)
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

                var (termIdx, terminator, args) = FindBranch(pred, block);

                if (args.Count != block.Parameters.Count)
                    throw new InvalidOperationException(
                        $"Block has {block.Parameters.Count} parameters but predecessor " +
                        $"passes {args.Count} arguments.");

                var copies = block.Parameters
                    .Zip(args, (param, arg) => (dst: param, src: arg))
                    .ToList();

                var seq = Sequentialize(copies, function);

                for (var i = 0; i < seq.Count; i++)
                {
                    var (dst, src) = seq[i];
                    pred.InsertInstruction(termIdx + i, GenericOpcode.GenericCopy,
                        new VirtualRegisterOperand(dst, IsDefinition: true),
                        src);
                }

                terminator.Operands = RemoveArgs(terminator.Operands, block);
            }

            block.Parameters.Clear();
        }
    }

    // Returns (terminator index, terminator instruction, block-argument operands) for
    // the instruction in pred that carries a BlockOperand pointing to target.
    // The block-argument operands are the consecutive vreg/physreg operands that
    // immediately follow the BlockOperand in the terminator's operand list.
    private static (int index, MachineInstruction term, List<MachineOperand> args)
        FindBranch(MachineBasicBlock pred, MachineBasicBlock target)
    {
        for (var i = 0; i < pred.Instructions.Count; i++)
        {
            var instr = pred.Instructions[i];
            for (var j = 0; j < instr.Operands.Length; j++)
            {
                if (instr.Operands[j] is not BlockOperand bo || bo.Block != target)
                    continue;

                var args = new List<MachineOperand>();
                for (var k = j + 1; k < instr.Operands.Length; k++)
                {
                    if (instr.Operands[k] is VirtualRegisterOperand or PhysicalRegisterOperand)
                        args.Add(instr.Operands[k]);
                    else
                        break;
                }
                return (i, instr, args);
            }
        }
        throw new InvalidOperationException("No terminator found in predecessor that branches to the target block.");
    }

    // Returns a copy of ops with the block-argument operands removed from the
    // entry for target.  The BlockOperand itself is kept; only the consecutive
    // vreg/physreg operands that follow it are dropped.
    private static MachineOperand[] RemoveArgs(MachineOperand[] ops, MachineBasicBlock target)
    {
        var result = new List<MachineOperand>(ops.Length);
        var i = 0;
        while (i < ops.Length)
        {
            result.Add(ops[i]);
            if (ops[i] is BlockOperand bo && bo.Block == target)
            {
                i++;
                while (i < ops.Length && ops[i] is VirtualRegisterOperand or PhysicalRegisterOperand)
                    i++;
            }
            else
                i++;
        }
        return result.ToArray();
    }

    // Converts a list of parallel copies {dst_i := src_i} into an ordered
    // sequence of sequential copies that produce the same effect.
    // When the copies form a cycle (e.g. two variables swapping), a fresh
    // temporary vreg is inserted to break the cycle.
    private static List<(int dst, MachineOperand src)> Sequentialize(
        List<(int dst, MachineOperand src)> copies,
        MachineFunction function)
    {
        var result = new List<(int, MachineOperand)>();
        var remaining = copies.ToList();

        while (remaining.Count > 0)
        {
            var emitted = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var (dst, _) = remaining[i];
                // Safe to emit when no other pending copy reads dst as its source.
                var dstUsedAsSrc = remaining
                    .Where((_, j) => j != i)
                    .Any(c => c.src is VirtualRegisterOperand vv && vv.VirtualRegister == dst);
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
                // Every remaining copy is part of a cycle.  Save the source of the
                // first copy to a temporary to break the cycle.
                var (cycleDst, cycleSrc) = remaining[0];
                if (cycleSrc is not VirtualRegisterOperand cycleVreg)
                    throw new InvalidOperationException(
                        "PhiEliminationPass: cycle involving a non-vreg source operand.");

                if (!function.TryGetVirtualRegisterClass(cycleVreg.VirtualRegister, out var classId))
                    throw new InvalidOperationException(
                        $"PhiEliminationPass: vreg %{cycleVreg.VirtualRegister} has no register class.");

                var temp = function.CreateVirtualRegisterWithClass(classId);
                result.Add((temp, cycleSrc));
                remaining[0] = (cycleDst, new VirtualRegisterOperand(temp, IsDefinition: false));
            }
        }

        return result;
    }
}
