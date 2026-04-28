using Irie.IR;

namespace Irie.CodeGen.Passes;

// Translates an IRFunction into a MachineFunction populated with generic opcodes.
// Calling convention is applied inline via the provided CallLowering.
public sealed class IRTranslatorPass(CallLowering callLowering)
{
    public MachineFunction Translate(IRFunction irFunction)
    {
        var machineFunction = new MachineFunction(irFunction.Name);
        var valueMap = new Dictionary<IRValue, int>();
        var blockMap = new Dictionary<IRBasicBlock, MachineBasicBlock>();

        // Pre-create all machine blocks to enable forward references in branches.
        foreach (var irBlock in irFunction.Blocks)
            blockMap[irBlock] = machineFunction.CreateBasicBlock();

        var entryBlock = blockMap[irFunction.Blocks[0]];
        var builder = new MachineIRBuilder(machineFunction);
        builder.SetInsertionPointAtEnd(entryBlock);

        // Materialize arguments via the calling convention.
        callLowering.LowerFormalArguments(irFunction, machineFunction, entryBlock, builder, valueMap);

        // Translate each block's instructions.
        foreach (var irBlock in irFunction.Blocks)
        {
            var machineBlock = blockMap[irBlock];
            builder.SetInsertionPointAtEnd(machineBlock);

            foreach (var irInstr in irBlock.Instructions)
                TranslateInstruction(irInstr, machineFunction, machineBlock, builder, valueMap, irFunction);
        }

        return machineFunction;
    }

    private void TranslateInstruction(
        IRInstruction irInstr,
        MachineFunction machineFunction,
        MachineBasicBlock machineBlock,
        MachineIRBuilder builder,
        Dictionary<IRValue, int> valueMap,
        IRFunction irFunction)
    {
        switch (irInstr)
        {
            case IRBinaryOperatorInstruction { Kind: BinaryOperatorKind.IntegerAdd } bin:
            {
                var lhsVreg = valueMap[bin.Operands[0].Value];
                var rhsVreg = valueMap[bin.Operands[1].Value];
                var resultVreg = machineFunction.CreateVirtualRegister(bin.Type);
                machineBlock.AddInstruction(GenericOpcode.GenericAdd,
                    new VirtualRegisterOperand(resultVreg, IsDefinition: true),
                    new VirtualRegisterOperand(lhsVreg,   IsDefinition: false),
                    new VirtualRegisterOperand(rhsVreg,   IsDefinition: false));
                valueMap[irInstr] = resultVreg;
                break;
            }

            case IRReturnInstruction ret:
            {
                int? returnVreg = ret.Operands.Length > 0
                    ? valueMap[ret.Operands[0].Value]
                    : null;
                callLowering.LowerReturn(irFunction.ReturnType, returnVreg, machineBlock, builder);
                break;
            }

            default:
                throw new NotSupportedException(
                    $"IRTranslatorPass: unsupported IR instruction type '{irInstr.GetType().Name}'");
        }
    }
}
