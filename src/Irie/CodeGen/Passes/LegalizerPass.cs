using Irie.IR;

namespace Irie.CodeGen.Passes;

// Mirrors LLVM's GlobalISel Legalizer.cpp:
//
//   * Two worklists: instList (non-artifacts) and artifactList (Merge/Unmerge/...).
//   * Initial population is bottom-up: instructions added top-down, popped LIFO.
//   * Alternating drain: legalize each non-artifact (which may emit new artifacts),
//     then run the LegalizationArtifactCombiner over each artifact, repeating until
//     instList is empty.
//   * An observer wired into MachineIRBuilder routes newly-created instructions
//     into the right worklist and removes erased ones, so worklist coherency is
//     maintained automatically across builder operations.
//   * Bottom-up traversal + IsTriviallyDead check at the top of each drain step
//     gives DCE for free: producers of just-folded values are processed after
//     their consumers, by which time their use count is zero.
public sealed class LegalizerPass(LegalizerInfo legalizerInfo) : MachineFunctionPass
{
    public override string Name => "Legalizer";

    public override void Run(MachineFunction function)
    {
        var builder = new MachineIRBuilder(function);
        var combiner = new LegalizationArtifactCombiner(function);

        var instList = new InstructionWorkList();
        var artifactList = new InstructionWorkList();

        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (!GenericOpcode.IsGeneric(instr.Opcode))
                    continue;
                if (GenericOpcode.IsArtifact(instr.Opcode))
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

                var deadInstrs = new List<MachineInstruction>();
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

    private void LegalizeInstruction(MachineInstruction instr, MachineFunction function, MachineIRBuilder builder)
    {
        var firstDef = instr.Operands
            .OfType<VirtualRegisterOperand>()
            .FirstOrDefault(o => o.IsDefinition);

        if (firstDef == null) return;

        var type = function.GetVirtualRegisterType(firstDef.VirtualRegister);
        var action = legalizerInfo.GetAction(instr.Opcode, type);

        if (action == LegalityAction.Legal) return;
        if (action == LegalityAction.Unsupported)
            throw new InvalidOperationException(
                $"Legalizer: opcode {GenericOpcode.GetName(instr.Opcode) ?? instr.Opcode.ToString()} " +
                $"with type {type.DisplayName} is unsupported on this target.");

        var narrowType = legalizerInfo.GetNarrowType(instr.Opcode, type);
        LegalizeNarrowScalar(instr, function, builder, type, narrowType);
    }

    // Splits a wide GenericAdd into a chain of narrow GenericAddCarry instructions.
    // The Unmerge/Merge artifacts inserted here are folded by the artifact combiner.
    private static void LegalizeNarrowScalar(
        MachineInstruction instr,
        MachineFunction function,
        MachineIRBuilder builder,
        IRType wideType,
        IRType narrowType)
    {
        if (instr.Opcode != GenericOpcode.GenericAdd)
            throw new NotSupportedException(
                $"Legalizer: NarrowScalar not implemented for opcode {GenericOpcode.GetName(instr.Opcode)}");

        var uses = instr.Operands
            .OfType<VirtualRegisterOperand>()
            .Where(o => !o.IsDefinition)
            .ToArray();
        var def = instr.Operands
            .OfType<VirtualRegisterOperand>()
            .First(o => o.IsDefinition);

        var lhsVreg    = uses[0].VirtualRegister;
        var rhsVreg    = uses[1].VirtualRegister;
        var resultVreg = def.VirtualRegister;

        var count = wideType.SizeInBits / narrowType.SizeInBits;

        builder.SetInsertionPointBefore(instr);

        var lhsParts = builder.BuildUnmerge(narrowType, lhsVreg, count);
        var rhsParts = builder.BuildUnmerge(narrowType, rhsVreg, count);

        // Mirrors llvm-mos's MOSLegalizerInfo::legalizeAddSubO: the chain head needs
        // an explicit zero carry-in vreg so that every AddCarry has a uniform 3-use
        // shape. The selector lowers `GenericConstant i1 0` to a target instruction
        // (LDImm1) whose def lives in the carry register class.
        var carryIn = builder.BuildConstant(IRType.I1, 0);
        var resultParts = new int[count];

        for (var i = 0; i < count; i++)
        {
            var (r, c) = builder.BuildAddCarry(narrowType, lhsParts[i], rhsParts[i], carryIn);
            resultParts[i] = r;
            carryIn = c;
        }

        builder.BuildMergeInto(resultVreg, resultParts);
        builder.Remove(instr);
    }

    // Routes builder-driven IR mutations to the appropriate worklist.
    private sealed class WorkListObserver(InstructionWorkList instList, InstructionWorkList artifactList)
        : IMachineFunctionObserver
    {
        public void OnInstructionCreated(MachineInstruction instruction)
        {
            if (!GenericOpcode.IsGeneric(instruction.Opcode))
                return;
            if (GenericOpcode.IsArtifact(instruction.Opcode))
                artifactList.Add(instruction);
            else
                instList.Add(instruction);
        }

        public void OnInstructionErased(MachineInstruction instruction)
        {
            instList.Remove(instruction);
            artifactList.Remove(instruction);
        }
    }

    // Ordered set of instructions, popped LIFO. Re-adding an existing entry is a no-op.
    private sealed class InstructionWorkList
    {
        private readonly LinkedList<MachineInstruction> _list = new();
        private readonly Dictionary<MachineInstruction, LinkedListNode<MachineInstruction>> _nodes = new();

        public bool IsEmpty => _list.Count == 0;

        public void Add(MachineInstruction instr)
        {
            if (_nodes.ContainsKey(instr)) return;
            _nodes[instr] = _list.AddLast(instr);
        }

        public MachineInstruction Pop()
        {
            var node = _list.Last!;
            var instr = node.Value;
            _list.RemoveLast();
            _nodes.Remove(instr);
            return instr;
        }

        public void Remove(MachineInstruction instr)
        {
            if (_nodes.TryGetValue(instr, out var node))
            {
                _list.Remove(node);
                _nodes.Remove(instr);
            }
        }
    }
}
