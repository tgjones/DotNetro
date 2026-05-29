using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502.V2;

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

    // Pre-AMS opcodes are refined based on the second use (the RHS source).
    // Returns null if the opcode is already addressing-mode-specific or has no
    // refinement rule yet.
    private static MOS6502Op? TryRefine(MOS6502Op op, MirOperand[] operands) => op switch
    {
        MOS6502Op.Adc => RefineAdc(operands),
        _ => null,
    };

    // mos6502.adc operand layout (defs first):
    //   def[0]: result, def[1]: carry_out,
    //   use[0]: a (operands[2]), use[1]: b (operands[3]), use[2]: carry_in (operands[4]).
    // The RHS (use[1]) drives the addressing mode.
    private static MOS6502Op? RefineAdc(MirOperand[] operands)
    {
        if (operands.Length < 4) return null;
        return operands[3] switch
        {
            PhysicalReg phys when IsZeroPage(phys.Id) => MOS6502Op.AdcZp,
            Immediate                                 => MOS6502Op.AdcImm,
            _ => null,
        };
    }

    private static bool IsZeroPage(int physReg) =>
        physReg >= MOS6502Registers.RC(0) && physReg < MOS6502Registers.N;
}
