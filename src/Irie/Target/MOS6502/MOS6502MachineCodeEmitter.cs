using Irie.MachineCode;
using Irie.Mir;

namespace Irie.Target.MOS6502;

// Lowers a post-PseudoExpansion MirModule into a MachineCodeModule by
// consulting MOS6502MachineCodeEmitTable for each instruction's opcode byte
// and operand-encoding rule. See notes/mir-to-machinecode-plan.md §2.3.
public sealed class MOS6502MachineCodeEmitter : Irie.Target.MachineCodeEmitter
{
    public override MachineCodeModule Emit(MirModule module)
    {
        var result = new MachineCodeModule();
        foreach (var function in module.Functions)
            EmitFunction(function, result.CreateFunction(function.Name));
        return result;
    }

    private static void EmitFunction(MirFunction src, MachineCodeFunction dst)
    {
        for (var blockIndex = 0; blockIndex < src.Blocks.Count; blockIndex++)
        {
            var block = src.Blocks[blockIndex];

            // The entry block falls through from the function label, so it
            // gets no label of its own; every other block does.
            if (blockIndex > 0)
                dst.EmitLabel(BlockLabel(blockIndex));

            foreach (var instr in block.Instructions)
                EmitInstruction(instr, src, dst);
        }
    }

    private static void EmitInstruction(MirInstruction instr, MirFunction parent, MachineCodeFunction dst)
    {
        if (instr.Opcode.Dialect != MOS6502Dialect.Id)
            throw new InvalidOperationException(
                $"MOS6502MachineCodeEmitter: cannot emit non-mos6502 opcode (dialect={instr.Opcode.Dialect.Index}, code={instr.Opcode.Code}).");

        var op = (MOS6502Op)instr.Opcode.Code;
        var rule = MOS6502MachineCodeEmitTable.Get(op);

        var operand = rule.Kind switch
        {
            EmitOperandKind.Implied         => null,
            EmitOperandKind.ZeroPageAddress => EmitZeroPage(instr, rule.OperandIndex!.Value),
            EmitOperandKind.Immediate       => EmitImmediate(instr, rule.OperandIndex!.Value),
            EmitOperandKind.BranchTarget    => EmitBranchTarget(instr, rule.OperandIndex!.Value, parent),
            EmitOperandKind.AbsoluteAddress => EmitAbsoluteAddress(instr, rule.OperandIndex!.Value, parent),
            EmitOperandKind.SymbolLowByte   => EmitSymbolHalf(instr, rule.OperandIndex!.Value, SymbolHalf.LowByte),
            EmitOperandKind.SymbolHighByte  => EmitSymbolHalf(instr, rule.OperandIndex!.Value, SymbolHalf.HighByte),
            _ => throw new InvalidOperationException($"Unhandled EmitOperandKind {rule.Kind}."),
        };

        dst.EmitInstruction(rule.OpcodeByte, operand == null ? [] : [operand]);
    }

    private static MachineCodeOperand EmitZeroPage(MirInstruction instr, int index)
    {
        var operand = instr.Operands[index];
        if (operand is not PhysicalReg phys || phys.IsImplicit || phys.Id < MOS6502Registers.RC(0))
            throw new InvalidOperationException(
                $"MOS6502MachineCodeEmitter: expected an explicit zero-page PhysicalReg at operand[{index}] of {(MOS6502Op)instr.Opcode.Code}, got {operand}.");
        return new MachineCodeOperand.Immediate(phys.Id - MOS6502Registers.RC(0));
    }

    private static MachineCodeOperand EmitImmediate(MirInstruction instr, int index)
    {
        var operand = instr.Operands[index];
        if (operand is not Mir.Immediate imm)
            throw new InvalidOperationException(
                $"MOS6502MachineCodeEmitter: expected an Immediate at operand[{index}] of {(MOS6502Op)instr.Opcode.Code}, got {operand}.");
        return new MachineCodeOperand.Immediate(imm.Value);
    }

    private static MachineCodeOperand EmitBranchTarget(MirInstruction instr, int index, MirFunction parent)
    {
        var operand = instr.Operands[index];
        if (operand is not BlockTarget target)
            throw new InvalidOperationException(
                $"MOS6502MachineCodeEmitter: expected a BlockTarget at operand[{index}] of {(MOS6502Op)instr.Opcode.Code}, got {operand}.");
        return new MachineCodeOperand.LabelRef(BlockLabel(parent.Blocks.IndexOf(target.Block)));
    }

    private static MachineCodeOperand EmitSymbolHalf(MirInstruction instr, int index, SymbolHalf half)
    {
        var operand = instr.Operands[index];
        if (operand is not Symbol(var name))
            throw new InvalidOperationException(
                $"MOS6502MachineCodeEmitter: expected a Symbol at operand[{index}] of {(MOS6502Op)instr.Opcode.Code}, got {operand}.");
        return new MachineCodeOperand.ExternalRef(name, half);
    }

    private static MachineCodeOperand EmitAbsoluteAddress(MirInstruction instr, int index, MirFunction parent)
    {
        var operand = instr.Operands[index];
        return operand switch
        {
            Symbol(var name)   => new MachineCodeOperand.ExternalRef(name),
            BlockTarget target => new MachineCodeOperand.LabelRef(BlockLabel(parent.Blocks.IndexOf(target.Block))),
            // Immediate carries a literal 16-bit address (e.g. `JSR $FFEE` for an
            // OS call). The assembly writer formats this as `$XXXX`.
            Mir.Immediate imm  => new MachineCodeOperand.Immediate(imm.Value),
            _ => throw new InvalidOperationException(
                $"MOS6502MachineCodeEmitter: expected a Symbol, BlockTarget or Immediate at operand[{index}] of {(MOS6502Op)instr.Opcode.Code}, got {operand}."),
        };
    }

    private static string BlockLabel(int blockIndex) => $"bb{blockIndex}";
}
