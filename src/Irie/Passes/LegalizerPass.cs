using Irie.Dialects.Arith;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// Worklist-driven legalizer for the unified-MIR pipeline. Mirrors the old
// Irie.CodeGen.Passes.LegalizerPass:
//
//   * Two worklists: instList (non-artifacts) and artifactList (pseudo.merge /
//     pseudo.unmerge).
//   * Initial population is bottom-up: instructions added top-down, popped LIFO.
//   * Alternating drain: legalize each non-artifact (which may emit new
//     artifacts), then run the artifact combiner over each artifact, repeating
//     until instList is empty.
//   * An IMirObserver wired into MirBuilder routes newly-created instructions
//     into the right worklist and removes erased ones, so worklist coherency is
//     maintained automatically across builder operations.
//   * Bottom-up traversal + an IsTriviallyDead check at the top of each drain
//     step gives DCE for free.
public sealed class LegalizerPass(Irie.Target.LegalizerInfo legalizerInfo) : MirFunctionPass
{
    public override string Name => "Legalizer";

    public override void Run(MirFunction function)
    {
        var builder = new MirBuilder(function);
        var combiner = new LegalizationArtifactCombiner(function);

        var instList = new InstructionWorkList();
        var artifactList = new InstructionWorkList();

        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
                if (dialect.IsArtifact(instr.Opcode.Code))
                    artifactList.Add(instr);
                else
                    instList.Add(instr);
            }
        }

        var observer = new WorkListObserver(instList, artifactList);
        builder.SetObserver(observer);

        do
        {
            while (!instList.IsEmpty)
            {
                var instr = instList.Pop();
                if (instr.Parent == null) continue;
                if (function.IsTriviallyDead(instr))
                {
                    builder.Remove(instr);
                    continue;
                }

                LegalizeInstruction(instr, function, builder);
            }

            while (!artifactList.IsEmpty)
            {
                var instr = artifactList.Pop();
                if (instr.Parent == null) continue;
                if (function.IsTriviallyDead(instr))
                {
                    builder.Remove(instr);
                    continue;
                }

                var deadInstrs = new List<MirInstruction>();
                if (combiner.TryCombineInstruction(instr, deadInstrs))
                {
                    foreach (var dead in deadInstrs)
                    {
                        if (dead.Parent != null)
                            builder.Remove(dead);
                    }
                }
                // If not combined, fall through: artifacts that don't fold are
                // assumed legal as-is by the target's legalizer info.
            }
        } while (!instList.IsEmpty);

        builder.SetObserver(null);
    }

    private void LegalizeInstruction(MirInstruction instr, MirFunction function, MirBuilder builder)
    {
        // The legality query keys off the type of the first vreg def.
        VirtualReg? firstDef = null;
        foreach (var op in instr.Operands)
        {
            if (op is VirtualReg v && v.IsDefinition)
            {
                firstDef = v;
                break;
            }
        }
        if (firstDef == null) return;

        var annotation = function.GetVRegAnnotation(firstDef.Id);
        if (annotation is not TypedVReg typed)
            throw new InvalidOperationException(
                $"Legalizer: vreg %{firstDef.Id} has non-typed annotation {annotation}");

        var action = legalizerInfo.GetAction(instr.Opcode, typed.Type);

        if (action == LegalityAction.Legal) return;
        if (action == LegalityAction.Unsupported)
        {
            var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
            throw new InvalidOperationException(
                $"Legalizer: opcode {dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)} " +
                $"with type {typed.Type.DisplayName} is unsupported on this target.");
        }

        var narrowType = legalizerInfo.GetNarrowType(instr.Opcode, typed.Type);
        LegalizeNarrowScalar(instr, function, builder, typed.Type, narrowType);
    }

    // Splits a wide arith.addi / arith.subi into a chain of narrow
    // arith.addi_with_carry / arith.subi_with_borrow instructions. The
    // pseudo.unmerge / pseudo.merge artifacts inserted here are folded by the
    // artifact combiner whenever they meet matching upstream / downstream
    // artifacts.
    private static void LegalizeNarrowScalar(
        MirInstruction instr,
        MirFunction function,
        MirBuilder builder,
        IRType wideType,
        IRType narrowType)
    {
        if (instr.Opcode.Dialect != ArithDialect.Id
            || ((ArithOp)instr.Opcode.Code != ArithOp.AddI
                && (ArithOp)instr.Opcode.Code != ArithOp.SubI))
        {
            var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
            throw new NotSupportedException(
                $"Legalizer: NarrowScalar not implemented for opcode {dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}");
        }

        var op = (ArithOp)instr.Opcode.Code;

        VirtualReg? def = null;
        var uses = new List<VirtualReg>(2);
        foreach (var operand in instr.Operands)
        {
            if (operand is VirtualReg v)
            {
                if (v.IsDefinition && def == null) def = v;
                else if (!v.IsDefinition) uses.Add(v);
            }
        }
        if (def == null || uses.Count != 2)
            throw new InvalidOperationException(
                $"Legalizer: arith.{(op == ArithOp.AddI ? "addi" : "subi")} instruction has unexpected operand shape.");

        var lhsVreg    = uses[0].Id;
        var rhsVreg    = uses[1].Id;
        var resultVreg = def.Id;

        var count = wideType.SizeInBits / narrowType.SizeInBits;

        builder.SetInsertionPointBefore(instr);

        var lhsParts = builder.BuildUnmerge(narrowType, lhsVreg, count);
        var rhsParts = builder.BuildUnmerge(narrowType, rhsVreg, count);

        // Chain head: addi chains start with a 0 carry-in (CLC); subi chains
        // start with a 1 borrow-in (SEC) since borrow_in uses 6502 C-flag
        // polarity. Either way, the constant lets every link have a uniform
        // 3-use shape.
        var chainHeadIn = op == ArithOp.AddI ? 0L : 1L;
        var carryOrBorrowIn = builder.BuildConstant(IRType.I1, chainHeadIn);
        var resultParts = new int[count];

        for (var i = 0; i < count; i++)
        {
            int r, cOrB;
            if (op == ArithOp.AddI)
                (r, cOrB) = builder.BuildAddCarry(narrowType, lhsParts[i], rhsParts[i], carryOrBorrowIn);
            else
                (r, cOrB) = builder.BuildSubBorrow(narrowType, lhsParts[i], rhsParts[i], carryOrBorrowIn);
            resultParts[i] = r;
            carryOrBorrowIn = cOrB;
        }

        builder.BuildMergeInto(resultVreg, resultParts);
        builder.Remove(instr);
    }

    // Routes builder-driven IR mutations into the appropriate worklist.
    private sealed class WorkListObserver(InstructionWorkList instList, InstructionWorkList artifactList)
        : IMirObserver
    {
        public void OnInstructionCreated(MirInstruction instruction)
        {
            var dialect = DialectRegistry.ById(instruction.Opcode.Dialect);
            if (dialect.IsArtifact(instruction.Opcode.Code))
                artifactList.Add(instruction);
            else
                instList.Add(instruction);
        }

        public void OnInstructionErased(MirInstruction instruction)
        {
            instList.Remove(instruction);
            artifactList.Remove(instruction);
        }
    }

    // Ordered set of instructions, popped LIFO. Re-adding an existing entry is
    // a no-op.
    private sealed class InstructionWorkList
    {
        private readonly LinkedList<MirInstruction> _list = new();
        private readonly Dictionary<MirInstruction, LinkedListNode<MirInstruction>> _nodes = new();

        public bool IsEmpty => _list.Count == 0;

        public void Add(MirInstruction instr)
        {
            if (_nodes.ContainsKey(instr)) return;
            _nodes[instr] = _list.AddLast(instr);
        }

        public MirInstruction Pop()
        {
            var node = _list.Last!;
            var instr = node.Value;
            _list.RemoveLast();
            _nodes.Remove(instr);
            return instr;
        }

        public void Remove(MirInstruction instr)
        {
            if (_nodes.TryGetValue(instr, out var node))
            {
                _list.Remove(node);
                _nodes.Remove(instr);
            }
        }
    }

    // Folds redundant pseudo.merge / pseudo.unmerge pairs introduced by
    // legalization. The legalizer alternates this with per-instruction
    // legalization: legalizing a wide op typically inserts unmerge/merge pairs
    // around the narrowed body, which this combiner then collapses by RAUW-ing
    // through the original components.
    private sealed class LegalizationArtifactCombiner(MirFunction function)
    {
        // Returns true if the instruction was combined. Adds any instructions
        // observed dead by the combine into deadInstrs (the caller erases them).
        public bool TryCombineInstruction(MirInstruction instr, List<MirInstruction> deadInstrs)
        {
            if (instr.Opcode.Dialect != Irie.Dialects.Pseudo.PseudoDialect.Id)
                return false;
            return (Irie.Dialects.Pseudo.PseudoOp)instr.Opcode.Code switch
            {
                Irie.Dialects.Pseudo.PseudoOp.Unmerge => TryCombineUnmerge(instr, deadInstrs),
                _ => false,
            };
        }

        // Pattern: Unmerge(Merge(p0..pN)) -> for each i: replace unmerge def_i with p_i
        //
        // Equal-arity, equal-element-type case only. The wider/narrower cases
        // (NumMergeRegs != NumDefs) are not handled.
        private bool TryCombineUnmerge(MirInstruction unmerge, List<MirInstruction> deadInstrs)
        {
            var defs = new List<VirtualReg>();
            VirtualReg? src = null;
            foreach (var op in unmerge.Operands)
            {
                if (op is VirtualReg v)
                {
                    if (v.IsDefinition) defs.Add(v);
                    else if (src == null) src = v;
                }
            }
            if (src == null) return false;

            var mergeInstr = function.GetDefinition(src.Id);
            if (mergeInstr == null) return false;
            if (mergeInstr.Opcode.Dialect != Irie.Dialects.Pseudo.PseudoDialect.Id) return false;
            if ((Irie.Dialects.Pseudo.PseudoOp)mergeInstr.Opcode.Code != Irie.Dialects.Pseudo.PseudoOp.Merge)
                return false;

            var mergeSources = new List<VirtualReg>();
            VirtualReg? mergeDef = null;
            foreach (var op in mergeInstr.Operands)
            {
                if (op is VirtualReg v)
                {
                    if (v.IsDefinition && mergeDef == null) mergeDef = v;
                    else if (!v.IsDefinition) mergeSources.Add(v);
                }
            }
            if (mergeDef == null) return false;
            if (mergeSources.Count != defs.Count) return false;

            for (var i = 0; i < defs.Count; i++)
            {
                var defType = AnnotationType(function.GetVRegAnnotation(defs[i].Id));
                var srcType = AnnotationType(function.GetVRegAnnotation(mergeSources[i].Id));
                if (defType is null || srcType is null || !defType.Equals(srcType))
                    return false;
            }

            for (var i = 0; i < defs.Count; i++)
                function.ReplaceAllUsesOfRegister(oldVreg: defs[i].Id, newVreg: mergeSources[i].Id);

            deadInstrs.Add(unmerge);

            // If the unmerge was the merge's only user, the merge becomes dead
            // the moment the unmerge is erased. Add it proactively — the
            // bottom-up trivially-dead check is unreliable when the merge is
            // added to the worklist *after* its consumer (e.g. when
            // legalization inserts a fresh BuildMergeInto whose result feeds an
            // existing unmerge that was popped earlier).
            if (function.GetUseCount(mergeDef.Id) == 1)
                deadInstrs.Add(mergeInstr);

            return true;
        }

        private static IRType? AnnotationType(VRegAnnotation annotation) => annotation switch
        {
            TypedVReg t => t.Type,
            _ => null,
        };
    }
}
