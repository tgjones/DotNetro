using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Target-private pass that refines each pre-AMS `mos6502.*` opcode to a
// concrete addressing-mode form based on its post-RA operands. The only family
// handled here is `mos6502.st.abs` → `sta/stx/sty.abs`, whose choice depends
// on the physical register RA assigned to the value — a decision not available
// at isel. (adc/sbc/cmp are not handled here: the selector picks their
// concrete form directly since the choice follows from the operand kind.)
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

    // Returns null if the opcode is already addressing-mode-specific or has no
    // refinement rule.
    private static MOS6502Op? TryRefine(MOS6502Op op, MirOperand[] operands) => op switch
    {
        // st.abs: the generic absolute store. Its value is operands[0]; select the
        // concrete sta.abs / stx.abs / sty.abs for whichever of $a/$x/$y RA placed
        // it in. All three share st.abs's exact operand layout (value in slot 0,
        // Symbol in slot 1), so only the opcode tag changes.
        MOS6502Op.StAbs => RefineStoreBySource(operands),
        _ => null,
    };

    // Select the concrete absolute store (STA/STX/STY) for a generic st.abs from
    // its source register.
    private static MOS6502Op? RefineStoreBySource(MirOperand[] operands)
    {
        if (operands.Length == 0 || operands[0] is not PhysicalReg src) return null;
        return src.Id switch
        {
            MOS6502Registers.A => MOS6502Op.StaAbs,
            MOS6502Registers.X => MOS6502Op.StxAbs,
            MOS6502Registers.Y => MOS6502Op.StyAbs,
            _                  => null,
        };
    }

}
