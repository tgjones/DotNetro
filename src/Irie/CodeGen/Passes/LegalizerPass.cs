using Irie.IR;

namespace Irie.CodeGen.Passes;

public sealed class LegalizerPass(LegalizerInfo legalizerInfo) : MachineFunctionPass
{
    public override string Name => "Legalizer";

    public override void Run(MachineFunction function)
    {
        var builder = new MachineIRBuilder(function);

        // Worklist initialised with all instructions; new instructions from
        // legalization are also added so they get a chance to be checked.
        var worklist = new Queue<MachineInstruction>(
            function.Blocks.SelectMany(b => b.Instructions));

        while (worklist.Count > 0)
        {
            var instr = worklist.Dequeue();
            if (instr.Parent == null) continue; // already removed by a previous legalization

            var firstDef = instr.Operands
                .OfType<VirtualRegisterOperand>()
                .FirstOrDefault(o => o.IsDefinition);

            if (firstDef == null) continue;

            var type = function.GetVirtualRegisterType(firstDef.VirtualRegister);
            var action = legalizerInfo.GetAction(instr.Opcode, type);

            if (action == LegalityAction.Legal) continue;
            if (action == LegalityAction.Unsupported)
                throw new InvalidOperationException(
                    $"Legalizer: opcode {GenericOpcode.GetName(instr.Opcode) ?? instr.Opcode.ToString()} " +
                    $"with type {type.DisplayName} is unsupported on this target.");

            // NarrowScalar: split the instruction into narrower operations.
            var narrowType = legalizerInfo.GetNarrowType(instr.Opcode, type);
            var newInstrs = LegalizeNarrowScalar(instr, function, builder, type, narrowType);
            foreach (var ni in newInstrs)
                worklist.Enqueue(ni);
        }
    }

    // Handles NarrowScalar for GenericAdd: splits into a GenericAddCarry chain.
    private static IReadOnlyList<MachineInstruction> LegalizeNarrowScalar(
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

        // First byte: immediate carry-in of 0 (no CLC needed in the generic layer).
        var (r0, c0) = builder.BuildAddCarryImm(narrowType, lhsParts[0], rhsParts[0], 0);
        var resultParts = new int[count];
        resultParts[0] = r0;
        var carryIn = c0;

        for (var i = 1; i < count; i++)
        {
            var (r, c) = builder.BuildAddCarry(narrowType, lhsParts[i], rhsParts[i], carryIn);
            resultParts[i] = r;
            carryIn = c;
        }

        // Reuse the original result vreg so all downstream uses stay valid.
        builder.BuildMergeInto(resultVreg, resultParts);

        // Collect the newly-inserted instructions before removing the original.
        var block = instr.Parent!;
        var insertedStart = block.Instructions.IndexOf(
            block.Instructions.First(i => i.Opcode == GenericOpcode.GenericUnmerge
                                       && i.Operands.Any(o => o is VirtualRegisterOperand v
                                                           && v.IsDefinition
                                                           && v.VirtualRegister == lhsParts[0])));
        var insertedEnd = block.Instructions.IndexOf(instr);
        var newInstrs = block.Instructions[insertedStart..insertedEnd].ToList();

        builder.Remove(instr);
        return newInstrs;
    }
}
