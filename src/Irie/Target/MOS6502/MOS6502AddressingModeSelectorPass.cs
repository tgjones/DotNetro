using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Target-private pass that refines each pre-AMS `mos6502.*` opcode to a
// concrete addressing-mode form (e.g. `mos6502.adc` → `mos6502.adc.zp` /
// `mos6502.adc.imm`) based on its post-RA operands.
//
// Lives entirely under the target — added to the pipeline by
// MOS6502TargetV2.AddPostRegisterAllocationPasses. Per unified-IR plan §5.5
// there is no generic "addressing mode selection" stage; the pipeline simply
// calls Target.AddPostRegisterAllocationPasses between CopyElimination and
// PseudoExpansion and the target inserts whatever it wants.
//
// Runs post-RA: every operand is a PhysicalReg or an Immediate. The pass
// rewrites the opcode tag in place and leaves the operand array untouched.
public sealed class MOS6502AddressingModeSelectorPass : MirFunctionPass
{
    public override string Name => "MOS6502AddressingModeSelector";

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (instr.Opcode.Dialect != MOS6502Dialect.Id) continue;

                if (TryRefine((MOS6502Op)instr.Opcode.Code, instr.Operands) is { } refined)
                {
                    block.Instructions[i] = new MirInstruction(
                        MOS6502Dialect.OpRef(refined),
                        instr.Operands)
                    {
                        Parent = block,
                    };
                }
            }
        }
    }

    // Pre-AMS opcodes are refined based on the source operand (the RHS).
    // Returns null if the opcode is already addressing-mode-specific or has no
    // refinement rule yet.
    private static MOS6502Op? TryRefine(MOS6502Op op, MirOperand[] operands) => op switch
    {
        // adc/sbc: 2 defs precede the uses; RHS is operands[3].
        MOS6502Op.Adc => RefineByOperand(operands, 3, MOS6502Op.AdcZp, MOS6502Op.AdcImm),
        MOS6502Op.Sbc => RefineByOperand(operands, 3, MOS6502Op.SbcZp, MOS6502Op.SbcImm),
        // cmp: implicit flag defs come AFTER explicit uses in the operand array;
        // explicit operands are use[0]=a (operands[0]) and use[1]=b (operands[1]).
        // RHS is operands[1].
        MOS6502Op.Cmp => RefineByOperand(operands, 1, MOS6502Op.CmpZp, MOS6502Op.CmpImm),
        _ => null,
    };

    // Refine an opcode based on the operand at the given index.
    //   PhysicalReg in zero-page → zp form.
    //   Immediate                → imm form.
    private static MOS6502Op? RefineByOperand(MirOperand[] operands, int index, MOS6502Op zpForm, MOS6502Op immForm)
    {
        if (operands.Length <= index) return null;
        return operands[index] switch
        {
            PhysicalReg phys when IsZeroPage(phys.Id) => zpForm,
            Immediate                                 => immForm,
            _ => null,
        };
    }

    private static bool IsZeroPage(int physReg) =>
        physReg >= MOS6502Registers.RC(0);
}
